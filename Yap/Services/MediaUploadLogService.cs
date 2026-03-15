using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Data;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Queues media upload log entries and flushes them to the database periodically.
/// No-op when persistence is disabled.
/// </summary>
public class MediaUploadLogService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;
    private readonly ILogger<MediaUploadLogService> _logger;
    private readonly ConcurrentQueue<MediaUploadLog> _queue = new();

    public bool IsEnabled { get; }

    public MediaUploadLogService(
        IServiceProvider serviceProvider,
        IOptions<PersistenceSettings> settings,
        ILogger<MediaUploadLogService> logger)
    {
        _logger = logger;
        IsEnabled = settings.Value.Enabled;

        if (IsEnabled)
        {
            _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();
            if (_dbFactory == null)
                IsEnabled = false;
        }
    }

    /// <summary>
    /// Enqueues a media upload log entry. Synchronous, non-blocking.
    /// </summary>
    public void Log(Guid userId, string username, string originalFileName, string storedFileName,
                    long fileSize, string fileType, string extension)
    {
        if (!IsEnabled) return;

        _queue.Enqueue(new MediaUploadLog
        {
            Date = DateTime.UtcNow,
            UserId = userId,
            Username = username,
            OriginalFileName = originalFileName,
            StoredFileName = storedFileName,
            FileSize = fileSize,
            FileType = fileType,
            Extension = extension
        });
    }

    /// <summary>
    /// Updates the processing duration after background processing completes.
    /// Checks the in-memory queue first (entry may not be flushed yet), then falls back to DB.
    /// </summary>
    public async Task SetCompressDurationAsync(string storedFileName, long durationMs)
    {
        if (!IsEnabled) return;

        // Try to update in the queue first (entry may not be flushed to DB yet)
        foreach (var entry in _queue)
        {
            if (entry.StoredFileName == storedFileName)
            {
                entry.CompressDurationMs = durationMs;
                return;
            }
        }

        // Not in queue — already flushed to DB, update there
        if (_dbFactory == null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.MediaUploadLogs
                .Where(l => l.StoredFileName == storedFileName)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.CompressDurationMs, durationMs));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update compress duration for {FileName}", storedFileName);
        }
    }

    /// <summary>
    /// Gets paginated, filtered media upload logs.
    /// </summary>
    public async Task<List<MediaUploadLog>> GetLogsAsync(string? username = null, string? fileType = null,
                                                          int skip = 0, int take = 100)
    {
        if (!IsEnabled || _dbFactory == null) return new();

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

    /// <summary>
    /// Gets aggregate stats for the media section header.
    /// </summary>
    public async Task<(int TotalFiles, long TotalBytes, int ImageCount, int VideoCount)> GetStatsAsync()
    {
        if (!IsEnabled || _dbFactory == null) return (0, 0, 0, 0);

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
                    VideoCount = g.Count(l => l.FileType == "video")
                })
                .FirstOrDefaultAsync();

            return stats != null
                ? (stats.TotalFiles, stats.TotalBytes, stats.ImageCount, stats.VideoCount)
                : (0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media stats");
            return (0, 0, 0, 0);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled) return;

        using var timer = new PeriodicTimer(FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync();
        }
        catch (OperationCanceledException) { }

        await FlushAsync();
    }

    private async Task FlushAsync()
    {
        if (_queue.IsEmpty || _dbFactory == null) return;

        var entries = new List<MediaUploadLog>();
        while (_queue.TryDequeue(out var entry))
            entries.Add(entry);

        if (entries.Count == 0) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.MediaUploadLogs.AddRange(entries);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush {Count} media upload log entries", entries.Count);
        }
    }
}
