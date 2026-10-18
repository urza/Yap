using Microsoft.EntityFrameworkCore;
using Yap.Models.Media;
using Yap.Services.Chat;
using Yap.Services.Persistence;

namespace Yap.Services.Media;

/// <summary>
/// The admin's clean-up of message uploads (AD-19): one upload, or every upload that no message shows any more.
/// A message names its files by URL, so an upload is in use while any message carries its stem, the GUID the
/// upload got. Memory holds only the newest messages (Persistence:Load), so a scan of the whole database is on
/// offer. Deleting a message keeps its files on purpose; this is the manual way to get the space back.
/// </summary>
public sealed class MediaCleanupService
{
    private readonly ChatService _chat;
    private readonly MediaUploadLogService _log;
    private readonly UploadPaths _uploads;
    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;   // the whole-database scan; null without persistence
    private readonly ILogger<MediaCleanupService> _logger;

    /// <summary>An upload younger than this is never pruned: its message may still sit in a composer, or in the writer's queue.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);

    public MediaCleanupService(ChatService chat, MediaUploadLogService log, UploadPaths uploads, IServiceProvider services, ILogger<MediaCleanupService> logger)
    {
        _chat = chat;
        _log = log;
        _uploads = uploads;
        _dbFactory = services.GetService<IDbContextFactory<ChatDbContext>>();
        _logger = logger;
    }

    public enum DeleteOutcome { Deleted, NotFound, NotAnUpload, InUse }
    public sealed record Unreferenced(int Files, long Bytes, int Recent);
    public sealed record Freed(int Files, long Bytes);

    /// <summary>The stem of an upload: the GUID at the start of its file name, shared by every derived file ({stem}_800px.webp).</summary>
    public static Guid? StemOf(string urlOrFileName) =>
        Guid.TryParse(Path.GetFileNameWithoutExtension(urlOrFileName).Split('_')[0], out var stem) ? stem : null;

    /// <summary>
    /// Deletes one upload with its derived files and its log row. Without <paramref name="force"/> an upload a
    /// message still shows is refused as InUse, with the count, so the page can ask the admin first.
    /// </summary>
    public async Task<(DeleteOutcome Outcome, int Uses, long Bytes)> DeleteAsync(int id, bool force)
    {
        var row = await _log.GetAsync(id);
        if (row is null) return (DeleteOutcome.NotFound, 0, 0);
        if (row.FileType is not ("image" or "video") || StemOf(row.StoredFileName) is not { } stem) return (DeleteOutcome.NotAnUpload, 0, 0);
        var uses = await UseCountAsync(stem);
        if (uses > 0 && !force) return (DeleteOutcome.InUse, uses, 0);
        var bytes = DeleteFiles(stem);
        _log.Delete(id);
        _logger.LogInformation("Media delete: {File} ({Bytes} bytes, {Uses} uses)", row.StoredFileName, bytes, uses);
        return (DeleteOutcome.Deleted, uses, bytes);
    }

    /// <summary>What a prune would delete: the uploads no message shows, past the grace period, and how many are too recent.</summary>
    public async Task<Unreferenced> UnreferencedAsync(bool scanDatabase)
    {
        var (rows, recent) = await CandidatesAsync(scanDatabase);
        return new(rows.Count, rows.Sum(r => BytesOf(StemOf(r.StoredFileName)!.Value)), recent);
    }

    /// <summary>Deletes every upload no message shows, past the grace period.</summary>
    public async Task<Freed> PruneAsync(bool scanDatabase)
    {
        var (rows, _) = await CandidatesAsync(scanDatabase);
        long bytes = 0;
        foreach (var row in rows)
        {
            bytes += DeleteFiles(StemOf(row.StoredFileName)!.Value);
            _log.Delete(row.Id);
        }
        _logger.LogInformation("Media prune: {Count} uploads, {Bytes} bytes (database scan: {Scan})", rows.Count, bytes, scanDatabase);
        return new(rows.Count, bytes);
    }

    private async Task<(List<MediaUploadLog> Rows, int Recent)> CandidatesAsync(bool scanDatabase)
    {
        var referenced = await ReferencedStemsAsync(scanDatabase);
        var cutoff = DateTime.UtcNow - GracePeriod;
        var rows = new List<MediaUploadLog>();
        var recent = 0;
        foreach (var row in await _log.GetUploadsAsync())
        {
            if (StemOf(row.StoredFileName) is not { } stem || referenced.Contains(stem)) continue;
            if (row.Date > cutoff) { recent++; continue; }
            rows.Add(row);
        }
        return (rows, recent);
    }

    /// <summary>Every stem a message carries: the messages in memory, and with the scan the saved ones too.</summary>
    private async Task<HashSet<Guid>> ReferencedStemsAsync(bool scanDatabase)
    {
        var stems = new HashSet<Guid>();
        foreach (var url in _chat.MediaUrls()) if (StemOf(url) is { } stem) stems.Add(stem);
        if (!scanDatabase || _dbFactory is null) return stems;
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Streamed one row at a time, so a long history never lands in memory at once.
        await foreach (var row in db.Messages.AsNoTracking().Select(m => new { m.ImageUrls, m.VideoUrls }).AsAsyncEnumerable())
            foreach (var url in row.ImageUrls.Concat(row.VideoUrls)) if (StemOf(url) is { } stem) stems.Add(stem);
        return stems;
    }

    /// <summary>How many messages show the upload: in memory, and in the database when there is one.</summary>
    private async Task<int> UseCountAsync(Guid stem)
    {
        var inMemory = _chat.MediaUrls().Count(url => StemOf(url) == stem);
        if (_dbFactory is null) return inMemory;
        await using var db = await _dbFactory.CreateDbContextAsync();
        // The media lists are JSON text columns, so the match is one LIKE on the stem: no rows loaded.
        var pattern = $"%{stem}%";
        var saved = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Messages WHERE ImageUrls LIKE {pattern} OR VideoUrls LIKE {pattern}").SingleAsync();
        return Math.Max(inMemory, saved);   // the writer lags memory by a batch, so the larger count is the truth
    }

    // The files of a stem are the top-level files of the uploads folder that start with it: the original, the
    // thumbnails, the poster, the compressed video. The subfolders (profiles, gifs, tus-temp) are never touched.
    private IEnumerable<string> FilesOf(Guid stem) =>
        Directory.Exists(_uploads.Root) ? Directory.EnumerateFiles(_uploads.Root, $"{stem}*") : [];

    private long BytesOf(Guid stem) => FilesOf(stem).Sum(path => new FileInfo(path).Length);

    private long DeleteFiles(Guid stem)
    {
        long bytes = 0;
        foreach (var path in FilesOf(stem).ToList())
        {
            try { bytes += new FileInfo(path).Length; File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {Path}", path); }
        }
        return bytes;
    }
}
