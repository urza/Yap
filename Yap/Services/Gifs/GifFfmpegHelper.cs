using System.Diagnostics;
using System.Globalization;

namespace Yap.Services.Gifs;

/// <summary>
/// ffprobe + ffmpeg subprocess wrappers tailored for the GIF pipeline.
/// Mirrors the patterns in MediaCacheService: timeouts, async process management,
/// SemaphoreSlim(2) concurrency, metadata stripping (-map_metadata -1), audio stripping (-an).
/// </summary>
public class GifFfmpegHelper
{
    private readonly ILogger<GifFfmpegHelper> _logger;
    private readonly SemaphoreSlim _transcodeSemaphore = new(2);

    private const int ProbeTimeoutMs = 10_000;
    private const int TranscodeTimeoutMs = 120_000;

    /// <summary>Whether ffmpeg+ffprobe are available on this system.</summary>
    public static bool IsAvailable { get; private set; }

    public GifFfmpegHelper(ILogger<GifFfmpegHelper> logger)
    {
        _logger = logger;
        DetectFfmpeg();
    }

    private void DetectFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            var exited = process?.WaitForExit(3000) ?? false;
            IsAvailable = exited && process?.ExitCode == 0;
            if (IsAvailable) _logger.LogInformation("ffmpeg detected for GIF pipeline");
            else _logger.LogWarning("ffmpeg not found — GIF transcoding will be unavailable");
        }
        catch
        {
            IsAvailable = false;
            _logger.LogWarning("ffmpeg not found — GIF transcoding will be unavailable");
        }
    }

    /// <summary>
    /// Probes a media file with ffprobe. Returns null on failure.
    /// </summary>
    public async Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsAvailable || !File.Exists(filePath)) return null;

        // We request both video and audio stream info plus format duration in one ffprobe call.
        var args = $"-v error -show_entries " +
                   $"stream=codec_type,codec_name,width,height:format=duration " +
                   $"-of default=noprint_wrappers=1 \"{filePath}\"";

        var (exitCode, stdout, _) = await RunProcessAsync("ffprobe", args, ProbeTimeoutMs, ct);
        if (exitCode != 0) return null;

        string? videoCodec = null;
        int width = 0, height = 0;
        bool hasAudio = false;
        double duration = 0;
        // ffprobe with multiple stream entries emits a flat key=value list per stream.
        // We rely on stream ordering: each stream block starts with codec_type=.
        string? currentStreamType = null;
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];

            switch (key)
            {
                case "codec_type":
                    currentStreamType = value;
                    if (value == "audio") hasAudio = true;
                    break;
                case "codec_name":
                    if (currentStreamType == "video" && videoCodec == null) videoCodec = value;
                    break;
                case "width":
                    if (currentStreamType == "video" && width == 0) int.TryParse(value, out width);
                    break;
                case "height":
                    if (currentStreamType == "video" && height == 0) int.TryParse(value, out height);
                    break;
                case "duration":
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                    break;
            }
        }

        return new MediaProbeResult(videoCodec, width, height, hasAudio, duration);
    }

    /// <summary>
    /// Transcodes a source media file to browser-friendly H.264 MP4. Strips audio + metadata.
    /// Suitable for input: HEVC mov, vp8/vp9 webm, gif, animated webp, etc.
    /// </summary>
    public async Task<bool> TranscodeToMp4Async(string srcPath, string dstPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;

        await _transcodeSemaphore.WaitAsync(ct);
        try
        {
            var args = $"-y -i \"{srcPath}\" -c:v libx264 -preset veryfast -crf 23 " +
                       $"-pix_fmt yuv420p -movflags +faststart -an -map_metadata -1 \"{dstPath}\"";
            var (exit, _, stderr) = await RunProcessAsync("ffmpeg", args, TranscodeTimeoutMs, ct);
            if (exit != 0)
            {
                _logger.LogWarning("ffmpeg mp4 transcode failed (exit={Exit}): {Stderr}", exit, Truncate(stderr));
                TryDelete(dstPath);
                return false;
            }
            return File.Exists(dstPath) && new FileInfo(dstPath).Length > 0;
        }
        finally
        {
            _transcodeSemaphore.Release();
        }
    }

    /// <summary>
    /// Transcodes a source media file to VP9 WebM (smaller, browser-preferred when available).
    /// Strips audio + metadata. Slower than MP4 — used as a background pass.
    /// </summary>
    public async Task<bool> TranscodeToWebmAsync(string srcPath, string dstPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;

        await _transcodeSemaphore.WaitAsync(ct);
        try
        {
            var args = $"-y -i \"{srcPath}\" -c:v libvpx-vp9 -b:v 0 -crf 33 -row-mt 1 " +
                       $"-pix_fmt yuv420p -an -map_metadata -1 \"{dstPath}\"";
            var (exit, _, stderr) = await RunProcessAsync("ffmpeg", args, TranscodeTimeoutMs, ct);
            if (exit != 0)
            {
                _logger.LogWarning("ffmpeg webm transcode failed (exit={Exit}): {Stderr}", exit, Truncate(stderr));
                TryDelete(dstPath);
                return false;
            }
            return File.Exists(dstPath) && new FileInfo(dstPath).Length > 0;
        }
        finally
        {
            _transcodeSemaphore.Release();
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return (-1, "", "Failed to start process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            _logger.LogWarning("{Cmd} timed out after {TimeoutMs}ms", fileName, timeoutMs);
            return (-1, "", "Timeout");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Truncate(string s, int max = 400)
        => s.Length <= max ? s : s[..max] + "…";
}

public record MediaProbeResult(
    string? VideoCodec,
    int Width,
    int Height,
    bool HasAudio,
    double DurationSeconds
);
