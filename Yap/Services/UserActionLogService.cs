using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Data;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Queues user action logs and flushes them to the database periodically.
/// No-op when persistence is disabled.
/// </summary>
public class UserActionLogService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;
    private readonly ILogger<UserActionLogService> _logger;
    private readonly ConcurrentQueue<UserActionLog> _queue = new();

    public bool IsEnabled { get; }

    public UserActionLogService(
        IServiceProvider serviceProvider,
        IOptions<PersistenceSettings> settings,
        ILogger<UserActionLogService> logger)
    {
        _logger = logger;
        IsEnabled = settings.Value.Enabled;

        if (IsEnabled)
        {
            _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();
            if (_dbFactory == null)
            {
                IsEnabled = false;
            }
        }
    }

    /// <summary>
    /// Enqueues a log entry. Synchronous, non-blocking.
    /// </summary>
    public void Log(string? userUid, string action, string? url = null, string? info = null, string? ip = null)
    {
        if (!IsEnabled) return;

        _queue.Enqueue(new UserActionLog
        {
            Date = DateTime.UtcNow,
            UserUid = userUid ?? "",
            Action = action,
            Url = url,
            Info = info,
            IP = ip
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled) return;

        using var timer = new PeriodicTimer(FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync();
            }
        }
        catch (OperationCanceledException) { }

        // Final flush on shutdown
        await FlushAsync();
    }

    /// <summary>
    /// Deletes entries older than 6 months, then trims to last 100 per user.
    /// </summary>
    public async Task CleanupAsync()
    {
        if (!IsEnabled || _dbFactory == null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Delete everything older than 6 months
            var cutoff = DateTime.UtcNow.AddMonths(-6);
            var deletedOld = await db.UserActionLogs
                .Where(l => l.Date < cutoff)
                .ExecuteDeleteAsync();

            // Per user, keep only the last 100 entries
            var usersWithExcess = await db.UserActionLogs
                .GroupBy(l => l.UserUid)
                .Where(g => g.Count() > 100)
                .Select(g => g.Key)
                .ToListAsync();

            var deletedExcess = 0;
            foreach (var userUid in usersWithExcess)
            {
                var keepIds = await db.UserActionLogs
                    .Where(l => l.UserUid == userUid)
                    .OrderByDescending(l => l.Date)
                    .Take(100)
                    .Select(l => l.Id)
                    .ToListAsync();

                deletedExcess += await db.UserActionLogs
                    .Where(l => l.UserUid == userUid && !keepIds.Contains(l.Id))
                    .ExecuteDeleteAsync();
            }

            if (deletedOld > 0 || deletedExcess > 0)
            {
                _logger.LogInformation("Action log cleanup: deleted {Old} old + {Excess} excess entries",
                    deletedOld, deletedExcess);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up action logs");
        }
    }

    private async Task FlushAsync()
    {
        if (_queue.IsEmpty || _dbFactory == null) return;

        var entries = new List<UserActionLog>();
        while (_queue.TryDequeue(out var entry))
            entries.Add(entry);

        if (entries.Count == 0) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.UserActionLogs.AddRange(entries);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush {Count} action log entries", entries.Count);
        }
    }
}
