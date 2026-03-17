using System.Collections.Concurrent;
using System.Net;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using Yap.Middleware;
using Yap.Services;

namespace Yap.Endpoints;

public static class TusEndpoints
{
    private static readonly ConcurrentDictionary<string, object> _completedFiles = new();
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    public static void MapTusEndpoints(this WebApplication app)
    {
        var tusStorePath = Path.Combine(app.Environment.WebRootPath, "uploads", "tus-temp");
        Directory.CreateDirectory(tusStorePath);
        var webRootPath = app.Environment.WebRootPath;

        app.MapTus("/api/tus", async httpContext => new()
        {
            Store = new TusDiskStore(tusStorePath),
            MaxAllowedUploadSizeInBytesLong = (long)httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue<int>("ChatSettings:MaxUploadSizeMB", 100) * 1024 * 1024,
            Events = new()
            {
                OnAuthorizeAsync = eventContext =>
                {
                    if (eventContext.HttpContext.Request.Method == "OPTIONS")
                        return Task.CompletedTask;

                    var token = eventContext.HttpContext.Request.Cookies[AuthMiddleware.CookieName];
                    var userService = eventContext.HttpContext.RequestServices.GetRequiredService<UserService>();
                    var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;
                    if (user == null)
                    {
                        eventContext.FailRequest(HttpStatusCode.Unauthorized, "Authentication required");
                    }
                    return Task.CompletedTask;
                },

                OnFileCompleteAsync = async eventContext =>
                {
                    var logger = eventContext.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TusUpload");
                    var imageService = eventContext.HttpContext.RequestServices.GetRequiredService<ImageService>();
                    var videoService = eventContext.HttpContext.RequestServices.GetRequiredService<VideoService>();
                    var mediaLog = eventContext.HttpContext.RequestServices.GetRequiredService<MediaUploadLogService>();
                    var userService = eventContext.HttpContext.RequestServices.GetRequiredService<UserService>();

                    var file = await eventContext.GetFileAsync();
                    var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);

                    var originalFileName = metadata.TryGetValue("filename", out var fName)
                        ? fName.GetString(System.Text.Encoding.UTF8) : "unknown";
                    var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

                    string type;
                    if (_imageExtensions.Contains(extension))
                        type = "image";
                    else if (VideoService.IsVideoFile(extension))
                        type = "video";
                    else
                    {
                        logger.LogWarning("Unsupported file type uploaded via tus: {Extension}", extension);
                        return;
                    }

                    var uploadsFolder = Path.Combine(webRootPath, "uploads");
                    var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    var tusFilePath = Path.Combine(tusStorePath, file.Id);

                    File.Move(tusFilePath, filePath);
                    foreach (var metaFile in Directory.GetFiles(tusStorePath, $"{file.Id}.*"))
                        try { File.Delete(metaFile); } catch { }

                    var fileSize = new FileInfo(filePath).Length;
                    logger.LogDebug("Tus upload complete: {FileName} ({Type}, {Size}KB)", uniqueFileName, type, fileSize / 1024);

                    var token = eventContext.HttpContext.Request.Cookies[AuthMiddleware.CookieName];
                    var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;
                    if (user != null)
                    {
                        mediaLog.Log(user.Id, user.Username, originalFileName, uniqueFileName, fileSize, type, extension);
                    }

                    if (type == "image")
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        await imageService.GenerateMediumThumbnailAsync(filePath);
                        var mediumMs = sw.ElapsedMilliseconds;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var swLarge = System.Diagnostics.Stopwatch.StartNew();
                                await imageService.GenerateLargeThumbnailAsync(filePath);
                                swLarge.Stop();
                                await mediaLog.SetCompressDurationAsync(uniqueFileName, mediumMs + swLarge.ElapsedMilliseconds);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Error generating large thumbnail for {FileName}", uniqueFileName);
                            }
                        });
                    }
                    else // video
                    {
                        if (VideoService.IsAvailable)
                        {
                            await videoService.GeneratePosterAsync(filePath);

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var (compressedPath, durationMs) = await videoService.CompressVideoAsync(filePath);
                                    if (compressedPath != null && durationMs > 0)
                                        await mediaLog.SetCompressDurationAsync(uniqueFileName, durationMs);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Error compressing video {FileName}", uniqueFileName);
                                }
                            });
                        }
                    }

                    _completedFiles[file.Id] = new { url = $"/uploads/{uniqueFileName}", path = filePath, type };
                }
            }
        }).RequireCors("TusUpload");

        app.MapGet("/api/tus/info/{fileId}", (string fileId) =>
        {
            if (_completedFiles.TryRemove(fileId, out var result))
                return Results.Ok(result);
            return Results.NotFound(new { error = "File not found or still processing" });
        }).RequireCors("TusUpload");
    }
}
