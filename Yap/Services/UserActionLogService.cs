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
