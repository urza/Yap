using Microsoft.EntityFrameworkCore;
using Yap.Models;
using Yap.Models.Media;
using Yap.Services.Persistence;

namespace Yap.Services.Media;

/// <summary>
/// Writes media upload log entries through the persistence writer and serves the admin queries over them.
/// No-op when persistence is disabled.
/// </summary>
public class MediaUploadLogService
{
    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;   // admin reads only
    private readonly PersistenceWriter _writer;
    private readonly ILogger<MediaUploadLogService> _logger;

    public MediaUploadLogService(IServiceProvider serviceProvider, PersistenceWriter writer, ILogger<MediaUploadLogService> logger)
    {
        _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();
        _writer = writer;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a media upload log entry. Synchronous, non-blocking.
    /// </summary>
    public void Log(Guid userId, string username, string originalFileName, string storedFileName,
                    long fileSize, string fileType, string extension)
    {
        _writer.Enqueue(Writes.MediaUploadLog(new MediaUploadLog
        {
            Date = DateTime.UtcNow,
            UserId = userId,
            Username = username,
            OriginalFileName = originalFileName,
            StoredFileName = storedFileName,
            FileSize = fileSize,
            FileType = fileType,
            Extension = extension
        }));
    }

    /// <summary>
    /// Records the processing duration after background processing completes. The writer runs
    /// this after the insert from <see cref="Log"/>, because both go through the same queue in order.
    /// </summary>
    public void SetCompressDuration(string storedFileName, long durationMs) =>
        _writer.Enqueue(Writes.MediaUploadDuration(storedFileName, durationMs));

    /// <summary>
    /// Gets paginated, filtered media upload logs.
    /// </summary>
    public async Task<List<MediaUploadLog>> GetLogsAsync(string? username = null, string? fileType = null,
                                                          int skip = 0, int take = 100)
    {
        if (_dbFactory is null) return new();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            IQueryable<MediaUploadLog> query = db.MediaUploadLogs;

            if (!string.IsNullOrEmpty(username))
                query = query.Where(l => l.Username.ToLower().Contains(username.ToLower()));

            if (!string.IsNullOrEmpty(fileType))
                query = query.Where(l => l.FileType == fileType);

            return await query
                .OrderByDescending(l => l.Date)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media upload logs");
            return new();
        }
    }

    /// <summary>One row, for the admin's delete (AD-19).</summary>
    public async Task<MediaUploadLog?> GetAsync(int id)
    {
        if (_dbFactory is null) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MediaUploadLogs.FindAsync(id);
    }

    /// <summary>Every image, video and voice row, oldest first: the clean-up's candidates. GIF rows belong to the GIF service.</summary>
    public async Task<List<MediaUploadLog>> GetUploadsAsync()
    {
        if (_dbFactory is null) return new();
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MediaUploadLogs.AsNoTracking().Where(l => l.FileType == "image" || l.FileType == "video" || l.FileType == "voice").OrderBy(l => l.Date).ToListAsync();
    }

    /// <summary>Drops the row once its files are gone. Through the writer, like the insert; the page updates itself.</summary>
    public void Delete(int id) => _writer.Enqueue(Writes.DeleteMediaUploadLog(id));

    /// <summary>
    /// Gets aggregate stats for the media section header.
    /// </summary>
    public async Task<(int TotalFiles, long TotalBytes, int ImageCount, int VideoCount, int VoiceCount)> GetStatsAsync()
    {
        if (_dbFactory is null) return (0, 0, 0, 0, 0);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var stats = await db.MediaUploadLogs
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    TotalFiles = g.Count(),
                    TotalBytes = g.Sum(l => l.FileSize),
                    ImageCount = g.Count(l => l.FileType == "image"),
                    VideoCount = g.Count(l => l.FileType == "video"),
                    VoiceCount = g.Count(l => l.FileType == "voice")
                })
                .FirstOrDefaultAsync();

            return stats != null
                ? (stats.TotalFiles, stats.TotalBytes, stats.ImageCount, stats.VideoCount, stats.VoiceCount)
                : (0, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media stats");
            return (0, 0, 0, 0, 0);
        }
    }
}
