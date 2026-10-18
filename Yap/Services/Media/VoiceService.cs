using Yap.Services.Gifs;

namespace Yap.Services.Media;

/// <summary>
/// Turns a recorded voice message into the one file every browser plays. Browsers record different
/// containers (Chrome and Firefox Opus in WebM or Ogg, Safari AAC in MP4), and Chrome's WebM carries no
/// duration header, so the raw recording is never served. One ffmpeg pass writes mono AAC in an .m4a with the
/// index at the front (faststart), and ffprobe reads the true duration from the result.
/// </summary>
public sealed class VoiceService(ILogger<VoiceService> logger, GifFfmpegHelper ffmpeg)
{
    /// <summary>The longest voice message. The recorder stops at this length (IB-69); the server allows a little
    /// over it for the recorder's own imprecision.</summary>
    public const int MaxSeconds = 300;
    private const int ToleranceSeconds = 10;
    private const int TranscodeTimeoutMs = 60_000;

    /// <summary>ffmpeg is one binary for the GIF pipeline, the video pipeline and this: the GIF helper's detection serves.</summary>
    public static bool IsAvailable => GifFfmpegHelper.IsAvailable;

    public sealed record Result(string Path, int DurationMs);

    /// <summary>
    /// Writes <paramref name="outputPath"/> (an .m4a) from any recording and returns its duration. Null when
    /// ffmpeg fails, when the file has no audio, or when it is longer than <see cref="MaxSeconds"/>; the output
    /// is removed in every failed case. The source is left to the caller.
    /// </summary>
    public async Task<Result?> TranscodeAsync(string sourcePath, string outputPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;

        // -vn drops a video track a browser may have added; 48 kbps mono AAC is plenty for speech and makes a
        // minute about 360 KB. -map_metadata -1: the recording's own tags carry nothing worth keeping.
        var args = $"-hide_banner -nostats -y -i \"{sourcePath}\" -vn -map_metadata -1 " +
                   $"-ac 1 -ar 48000 -c:a aac -b:a 48k -movflags +faststart \"{outputPath}\"";
        var (exit, _, stderr) = await ffmpeg.RunProcessAsync("ffmpeg", args, TranscodeTimeoutMs, ct);
        if (exit != 0 || !File.Exists(outputPath))
        {
            logger.LogWarning("Voice transcode failed for {Source} (exit={Exit}): {Stderr}", sourcePath, exit, Tail(stderr));
            TryDelete(outputPath);
            return null;
        }

        var probe = await ffmpeg.ProbeAsync(outputPath, ct);
        var seconds = probe?.DurationSeconds ?? 0;
        if (probe is not { HasAudio: true } || seconds <= 0 || seconds > MaxSeconds + ToleranceSeconds)
        {
            logger.LogWarning("Voice message rejected after transcode: {Source}, {Seconds}s", sourcePath, seconds);
            TryDelete(outputPath);
            return null;
        }
        return new Result(outputPath, (int)Math.Round(seconds * 1000));
    }

    /// <summary>"0:07", "1:42": the length as the row and the notification show it.</summary>
    public static string FormatDuration(int durationMs)
    {
        var seconds = (durationMs + 500) / 1000;
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Tail(string s, int max = 600) => s.Length <= max ? s : "…" + s[^max..];
}
