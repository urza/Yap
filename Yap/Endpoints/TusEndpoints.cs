using System.Collections.Concurrent;
using System.Net;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using Yap.Middleware;
using Yap.Services;
using Yap.Services.Gifs;

namespace Yap.Endpoints;

public static class TusEndpoints
{
    private static readonly ConcurrentDictionary<string, object> _completedFiles = new();
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
    private static readonly HashSet<string> _videoExtensionsForGifProbe = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mov", ".mkv" };

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
                    var gifService = eventContext.HttpContext.RequestServices.GetRequiredService<GifService>();

                    var file = await eventContext.GetFileAsync();
                    var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);

                    // Null when tus metadata carried no filename; originalFileName keeps the
                    // "unknown" placeholder for logging, but tag-seeding must not see it.
                    var uploadedFileName = metadata.TryGetValue("filename", out var fName)
                        ? fName.GetString(System.Text.Encoding.UTF8) : null;
                    var originalFileName = uploadedFileName ?? "unknown";
                    var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
                    var kind = metadata.TryGetValue("kind", out var kindMeta)
                        ? kindMeta.GetString(System.Text.Encoding.UTF8) : null;

                    string type;
                    if (kind == "gif" || _imageExtensions.Contains(extension))
                        type = "image"; // Will be re-classified to gif below if applicable.
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

                    var token = eventContext.HttpContext.Request.Cookies[AuthMiddleware.CookieName];
                    var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;

                    // GIF routing: explicit kind=gif, .gif extension, or a short silent video clip.
                    // GifService consumes the source file on success; failure leaves it in place for the regular pipeline.
                    var isExplicitGif = kind == "gif" || extension == ".gif";
                    var probeForGif = isExplicitGif
                        || (type == "video" && _videoExtensionsForGifProbe.Contains(extension));
                    if (probeForGif)
                    {
                        var contentType = MimeFromExtension(extension);
                        var gifAttachment = await gifService.TryAcceptAsGifAsync(
                            filePath, contentType, isExplicitGif, user?.Id, uploadedFileName);
                        if (gifAttachment != null)
                        {
                            logger.LogDebug("Tus upload classified as GIF: {FileName} ({EntryId})",
                                originalFileName, gifAttachment.GifEntryId);
                            if (user != null)
                            {
                                mediaLog.Log(user.Id, user.Username, originalFileName ?? "unknown",
                                    $"gif/{gifAttachment.GifEntryId}", fileSize, "gif", extension);
                            }
                            _completedFiles[file.Id] = new
                            {
                                type = "gif",
                                gifEntryId = gifAttachment.GifEntryId,
                                width = gifAttachment.Width,
                                height = gifAttachment.Height,
                                url = $"/uploads/gifs/{gifAttachment.GifEntryId}.mp4"
                            };
                            return;
                        }
                        else if (isExplicitGif)
                        {
                            logger.LogWarning("Explicit kind=gif upload rejected: {FileName}", originalFileName);
                            // Fall through to regular pipeline so the file isn't lost.
                        }
                    }

                    logger.LogDebug("Tus upload complete: {FileName} ({Type}, {Size}KB)", uniqueFileName, type, fileSize / 1024);

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

    private static string MimeFromExtension(string ext) => ext switch
    {
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".webm" => "video/webm",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream"
    };
}
