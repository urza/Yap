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
    public void Log(string? userUid, string action, string? url = null, string? info = null, string? ip = null, string? userAgent = null)
    {
        if (!IsEnabled) return;

        _queue.Enqueue(new UserActionLog
        {
            Date = DateTime.UtcNow,
            UserUid = userUid ?? "",
            Action = action,
            Url = url,
            Info = info,
            IP = ip,
            UserAgent = userAgent
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

    /// <summary>
    /// Gets the last action log entry per user.
    /// </summary>
    public async Task<Dictionary<string, UserActionLog>> GetLastActionPerUserAsync()
    {
        if (!IsEnabled || _dbFactory == null)
            return new();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // Load all logs ordered by date desc, then group in memory
            // (complex GroupBy + OrderBy + First doesn't translate to SQL in all providers)
            var allLogs = await db.UserActionLogs
                .Where(l => l.UserUid != "")
                .OrderByDescending(l => l.Date)
                .ToListAsync();

            return allLogs
                .GroupBy(l => l.UserUid)
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get last action per user");
            return new();
        }
    }

    /// <summary>
    /// Gets recent distinct IPs per user (up to 5).
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetRecentIPsPerUserAsync()
    {
        if (!IsEnabled || _dbFactory == null)
            return new();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // Load logs with IPs, then group in memory
            var logsWithIps = await db.UserActionLogs
                .Where(l => l.UserUid != "" && l.IP != null && l.IP != "")
                .OrderByDescending(l => l.Date)
                .Select(l => new { l.UserUid, l.IP })
                .ToListAsync();

            return logsWithIps
                .GroupBy(l => l.UserUid)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(l => l.IP!).Distinct().Take(5).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent IPs per user");
            return new();
        }
    }

    /// <summary>
    /// Gets distinct IPs with last-seen date and distinct user agents for a specific user.
    /// </summary>
    public async Task<(List<(string IP, DateTime LastSeen)> IPs, List<(string UserAgent, DateTime LastSeen)> UserAgents)>
        GetUserConnectionDetailsAsync(string userUid)
    {
        if (!IsEnabled || _dbFactory == null)
            return (new(), new());

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var logs = await db.UserActionLogs
                .Where(l => l.UserUid == userUid)
                .OrderByDescending(l => l.Date)
                .Select(l => new { l.IP, l.UserAgent, l.Date })
                .ToListAsync();

            var ips = logs
                .Where(l => !string.IsNullOrEmpty(l.IP))
                .GroupBy(l => l.IP!)
                .Select(g => (IP: g.Key, LastSeen: g.Max(x => x.Date)))
                .OrderByDescending(x => x.LastSeen)
                .ToList();

            var agents = logs
                .Where(l => !string.IsNullOrEmpty(l.UserAgent))
                .GroupBy(l => l.UserAgent!)
                .Select(g => (UserAgent: g.Key, LastSeen: g.Max(x => x.Date)))
                .OrderByDescending(x => x.LastSeen)
                .ToList();

            return (ips, agents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get connection details for user {UserUid}", userUid);
            return (new(), new());
        }
    }

    /// <summary>
    /// Gets paginated, filtered action logs. Excludes HTTP_REQUEST unless explicitly requested.
    /// </summary>
    public async Task<List<UserActionLog>> GetLogsAsync(string? userUid = null, string? action = null, int skip = 0, int take = 100)
    {
        if (!IsEnabled || _dbFactory == null)
            return new();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            IQueryable<UserActionLog> query = db.UserActionLogs
                .Where(l => l.UserUid != "");

            if (!string.IsNullOrEmpty(userUid))
                query = query.Where(l => l.UserUid == userUid);

            if (!string.IsNullOrEmpty(action))
                query = query.Where(l => l.Action == action);
            else
                query = query.Where(l => l.Action != UserActionLog.KnownActions.HTTP_REQUEST);

            return await query
                .OrderByDescending(l => l.Date)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get action logs");
            return new();
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
