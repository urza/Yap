using Yap.Services.Chat;

namespace Yap.Services.Media;

/// <summary>
/// At start: a poster for every video in memory whose poster file is missing. A video row gets its box from the
/// poster's size (<see cref="MediaSizes"/>). Without a poster the player is 2:1 until one arrives, and the row
/// jumps on every rebuild (the lobby's video of 2026-10-23). An upload from before ffmpeg was present, or one
/// whose poster step failed, has none. ffmpeg needs about a second per video, so the work runs in the background,
/// one video at a time, like the GIF preview backfill.
/// </summary>
public sealed class VideoPosterBackfill(ChatService chat, VideoService videoService, UploadPaths uploads, MediaSizes sizes, ILogger<VideoPosterBackfill> logger)
{
    public void Start()
    {
        if (!VideoService.IsAvailable) return;
        var missing = chat.MediaUrls()
            .Where(url => VideoService.IsVideoFile(Path.GetExtension(url).ToLowerInvariant()))
            .Select(url => (Url: url, Poster: VideoService.GetPosterUrl(url)))
            .DistinctBy(v => v.Poster)   // a pin keeps the compressed URL of the same video
            .Where(v => uploads.PathOf(v.Poster) is { } poster && !File.Exists(poster))
            .ToList();
        if (missing.Count == 0) return;
        logger.LogInformation("Generating posters for {Count} videos without one (backfill)", missing.Count);
        _ = Task.Run(() => RunAsync(missing));
    }

    private async Task RunAsync(List<(string Url, string Poster)> missing)
    {
        var made = 0;
        foreach (var (url, poster) in missing)
        {
            try
            {
                if (SourceOf(url) is not { } source) continue;                      // the upload is gone
                if (await videoService.GeneratePosterAsync(source) is null) continue;   // logged there
                sizes.Forget(poster);   // a request before this point may have kept a null for it
                made++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Poster backfill failed for {Url}", url);
            }
        }
        if (made == 0) return;
        logger.LogInformation("Generated {Count} video posters (backfill)", made);
        chat.RefreshPinMedia();   // a pin's copy carries the size: it follows the new poster now, not at the next start
    }

    /// <summary>The file to take the frame from: the compressed mp4 when compression ran (it deletes the original),
    /// else the upload itself. Null when both are gone.</summary>
    private string? SourceOf(string url)
    {
        if (uploads.PathOf(VideoService.GetCompressedUrl(url)) is { } compressed && File.Exists(compressed)) return compressed;
        return uploads.PathOf(url) is { } original && File.Exists(original) ? original : null;
    }
}
