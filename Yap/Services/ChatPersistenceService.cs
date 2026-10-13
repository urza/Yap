using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Data;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Optional copy of ChatService's memory in the database, plus the startup snapshot that
/// refills that memory. ChatService accepts every change in memory and returns; the copy is
/// queued here and written by one background task in the order it was queued. No request
/// waits for the database. A failed copy is logged and never undoes an acceptance.
/// When disabled, every write is a no-op and every read returns empty.
/// </summary>
public class ChatPersistenceService : IHostedService
{
    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;
    private readonly ILogger<ChatPersistenceService> _logger;
    // One writer, first in first out. SQLite takes one writer at a time anyway, and one queue
    // keeps the copies in memory order: an insert queued under the conversation lock runs
    // before any edit of the same message, and read states land as readStateGate ordered them.
    private readonly System.Threading.Channels.Channel<Write> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<Write>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer = Task.CompletedTask;

    // Work is null for a flush marker: it only reports that everything before it has run.
    private readonly record struct Write(string What, Func<ChatDbContext, Task>? Work, TaskCompletionSource? Done = null);

    public bool IsEnabled { get; }

    public ChatPersistenceService(
        IServiceProvider serviceProvider,
        IOptions<PersistenceSettings> settings,
        ILogger<ChatPersistenceService> logger)
    {
        _logger = logger;
        IsEnabled = settings.Value.Enabled;

        if (IsEnabled)
        {
            _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();
            if (_dbFactory == null)
            {
                _logger.LogWarning("Persistence is enabled but DbContextFactory is not registered");
                IsEnabled = false;
            }
            else
            {
                _logger.LogInformation("Chat persistence enabled with {Provider}", settings.Value.Provider);
                _writer = Task.Run(DrainAsync);
            }
        }
    }

    #region Write queue

    // Every write goes through here. The work runs later on the writer task while memory keeps
    // changing, so callers pass detached copies or objects that are never changed in place.
    private void Enqueue(string what, Func<ChatDbContext, Task> work)
    {
        if (!IsEnabled) return;
        if (!_queue.Writer.TryWrite(new Write(what, work)))
            _logger.LogWarning("Database copy skipped after shutdown: {What}", what);
    }

    private async Task DrainAsync()
    {
        await foreach (var write in _queue.Reader.ReadAllAsync())
        {
            if (write.Work != null)
            {
                try
                {
                    await using var db = await _dbFactory!.CreateDbContextAsync();
                    await write.Work(db);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Database copy failed: {What}", write.What);
                }
            }
            write.Done?.TrySetResult();
        }
    }

    /// <summary>
    /// Completes when every copy queued before this call has been written or has failed.
    /// For the contract harness, which reads the database right after a write.
    /// </summary>
    internal Task FlushAsync()
    {
        if (!IsEnabled) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queue.Writer.TryWrite(new Write("flush", null, done)) ? done.Task : Task.CompletedTask;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Runs after the server has stopped taking requests. The queue gets to catch up, so a
    // deploy does not lose the last accepted writes.
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        try { await _writer.WaitAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Shutdown ended before the database copy caught up"); }
    }

    #endregion

    #region Channel Operations

    public void PersistChannel(Channel channel)
    {
        if (!IsEnabled) return;
        var copy = channel.Detached();
        Enqueue($"channel {copy.Id}", async db =>
        {
            var existing = await db.Channels.FindAsync(copy.Id);
            if (existing != null)
                db.Entry(existing).CurrentValues.SetValues(copy);
            else
                db.Channels.Add(copy);
            await db.SaveChangesAsync();
        });
    }

    public void DeleteChannel(Guid channelId) => Enqueue($"delete channel {channelId}", async db =>
    {
        // Notification overrides have no FK to Channel (see ChatDbContext), so nothing
        // cascades them away — delete them here or a recycled id inherits stale mutes.
        await db.ChannelNotificationSettings.Where(s => s.ChannelId == channelId).ExecuteDeleteAsync();
        await db.Channels.Where(c => c.Id == channelId).ExecuteDeleteAsync();
    });

    #endregion

    #region Message Acceptance

    // Copy persisted scalar/media fields once; navigation properties belong to the new context.
    private static ChatMessage DetachedMessage(ChatMessage message) => new(
        message.ChannelId, message.UserId, message.Username, message.Content, message.Timestamp,
        message.ImageUrls.ToList(), message.ReplyToMessageId, message.VideoUrls.ToList(), message.GifAttachments.ToList())
    {
        Id = message.Id,
        OperationId = message.OperationId,
        IsEdited = message.IsEdited
    };

    /// <summary>
    /// Queues the copy of an accepted message, its receipt and the recipients' read states as
    /// one transaction, so the database never holds a message without its receipt or counts.
    /// Call it where the message becomes visible: the copy then precedes every later mutation.
    /// </summary>
    public void PersistAcceptedMessage(ChatMessage message, TextSendReceipt receipt, IReadOnlyList<ChannelReadState> recipients)
    {
        if (!IsEnabled) return;
        // Taken now, not on the writer: an edit can change the live object before the copy runs.
        var copy = DetachedMessage(message);
        Enqueue($"message {copy.Id} (operation {receipt.OperationId})", async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Messages.Add(copy);
            db.TextSendReceipts.Add(receipt);
            foreach (var state in recipients)
                await UpsertReadStateAsync(db, state);
            await db.SaveChangesAsync();
            await CapReceiptsAsync(db, receipt.UserId);
            await transaction.CommitAsync();
        });
    }

    /// <summary>
    /// Queues an accepted edit, delete or reaction for the stored row, with its receipt.
    /// </summary>
    public void PersistMutation(TextSendReceipt receipt, ChatMutation mutation) =>
        Enqueue($"{mutation.Kind} of message {receipt.MessageId} (operation {receipt.OperationId})", async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var message = await db.Messages.Include(m => m.Reactions).SingleOrDefaultAsync(m => m.Id == receipt.MessageId && m.ChannelId == receipt.ChannelId);
            // A missing row means an earlier copy failed or the message is gone. The receipt is
            // still saved, so a restart keeps answering the retry instead of repeating the change.
            if (message != null)
            {
                if (mutation.Kind == "delete") db.Messages.Remove(message);
                else mutation.Apply(message);
            }
            db.TextSendReceipts.Add(receipt);
            await db.SaveChangesAsync();
            await CapReceiptsAsync(db, receipt.UserId);
            await transaction.CommitAsync();
        });

    // The state instances ChatService publishes are never changed in place, so the queued
    // reference still holds the values that were current when the write was queued.
    public void PersistReadState(ChannelReadState state) =>
        Enqueue($"read state for user {state.UserId} channel {state.ChannelId}", async db =>
        {
            await UpsertReadStateAsync(db, state);
            await db.SaveChangesAsync();
        });

    /// <summary>
    /// Drops receipts older than the retention window, and beyond the per-account cap for
    /// accounts imported from releases that had only age-based retention.
    /// </summary>
    public void PruneReceipts(DateTime before) => Enqueue("prune receipts", async db =>
    {
        await db.TextSendReceipts.Where(r => r.AcceptedAt < before).ExecuteDeleteAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM TextSendReceipts WHERE rowid IN (SELECT rowid FROM (SELECT rowid, ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY rowid DESC) AS position FROM TextSendReceipts) WHERE position > {ChatReceiptCleanup.MaxReceiptsPerUser})");
    });

    // SQLite assigns increasing rowids at insertion; UTC can move backward between accepts.
    private static Task CapReceiptsAsync(ChatDbContext db, Guid userId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM TextSendReceipts WHERE UserId = {userId} AND OperationId IN (SELECT OperationId FROM TextSendReceipts WHERE UserId = {userId} ORDER BY rowid DESC LIMIT -1 OFFSET {ChatReceiptCleanup.MaxReceiptsPerUser})");

    // Writes the whole state, never a delta: memory is the truth, and a row that missed a write
    // catches up on the next one. Callers queue under readStateGate, so rows land in memory order.
    private static async Task UpsertReadStateAsync(ChatDbContext db, ChannelReadState state)
    {
        var existing = await db.ChannelReadStates.FindAsync(state.UserId, state.ChannelId);
        if (existing == null)
        {
            existing = new ChannelReadState { UserId = state.UserId, ChannelId = state.ChannelId };
            db.ChannelReadStates.Add(existing);
        }
        existing.LastReadAt = state.LastReadAt;
        existing.UnreadCount = state.UnreadCount;
        existing.ReceivedCount = state.ReceivedCount;
        existing.ReadThrough = state.ReadThrough;
    }

    #endregion

    #region Reaction Operations

    /// <summary>
    /// The emojis this user reacts with most, straight from reaction history.
    /// Deliberately reactions-only — what you react with and what you type into messages
    /// are different habits, so typed emojis never influence the quick-reaction bar.
    /// Ties break toward the most recently used emoji (higher row id = newer reaction).
    /// </summary>
    public async Task<List<string>> GetTopReactionEmojisAsync(Guid userId, int count)
    {
        if (!IsEnabled) return new();

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            return await db.Reactions
                .Where(r => r.UserId == userId)
                .GroupBy(r => r.Emoji)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(r => r.Id))
                .Take(count)
                .Select(g => g.Key)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load top reaction emojis for user {UserId}", userId);
            return new();
        }
    }

    #endregion

    #region Notification Settings Operations

    /// <summary>
    /// Queues one user's mute override for one channel.
    /// </summary>
    public void PersistChannelNotificationSetting(ChannelNotificationSetting setting) =>
        Enqueue($"notification setting for user {setting.UserId} channel {setting.ChannelId}", async db =>
        {
            var existing = await db.ChannelNotificationSettings.FindAsync(setting.UserId, setting.ChannelId);
            if (existing != null)
            {
                existing.Muted = setting.Muted;
            }
            else
            {
                db.ChannelNotificationSettings.Add(new ChannelNotificationSetting
                {
                    UserId = setting.UserId,
                    ChannelId = setting.ChannelId,
                    Muted = setting.Muted
                });
            }

            await db.SaveChangesAsync();
        });

    /// <summary>
    /// Loads every per-channel mute override. Called once at startup.
    /// </summary>
    public async Task<List<ChannelNotificationSetting>> LoadChannelNotificationSettingsAsync()
    {
        if (!IsEnabled) return new();

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            return await db.ChannelNotificationSettings.AsNoTracking().ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load channel notification settings");
            return new();
        }
    }

    #endregion

    #region Push Subscription Operations

    public void SavePushSubscription(PushSubscription subscription)
    {
        if (!IsEnabled) return;
        var copy = new PushSubscription
        {
            Endpoint = subscription.Endpoint,
            Username = subscription.Username,
            P256dh = subscription.P256dh,
            Auth = subscription.Auth,
            CreatedAt = subscription.CreatedAt
        };
        Enqueue($"push subscription for {copy.Username}", async db =>
        {
            var existing = await db.PushSubscriptions.FindAsync(copy.Endpoint);
            if (existing != null)
            {
                existing.Username = copy.Username;
                existing.P256dh = copy.P256dh;
                existing.Auth = copy.Auth;
            }
            else
            {
                db.PushSubscriptions.Add(copy);
            }

            await db.SaveChangesAsync();
        });
    }

    public void RemovePushSubscription(string endpoint) => Enqueue("remove push subscription", db =>
        db.PushSubscriptions.Where(p => p.Endpoint == endpoint).ExecuteDeleteAsync());

    public void RemovePushSubscriptionsByUsername(string username) => Enqueue($"remove push subscriptions for {username}", db =>
        db.PushSubscriptions.Where(p => p.Username == username).ExecuteDeleteAsync());

    public async Task<List<PushSubscription>> GetAllPushSubscriptionsAsync()
    {
        if (!IsEnabled) return new List<PushSubscription>();

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            return await db.PushSubscriptions.AsNoTracking().ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all push subscriptions");
            return new List<PushSubscription>();
        }
    }

    #endregion

    #region Snapshot Loading

    /// <summary>
    /// Loads all channels, messages, and read states from the database.
    /// Returns null if persistence is disabled.
    /// </summary>
    public async Task<ChatSnapshot?> LoadSnapshotAsync()
    {
        if (!IsEnabled) return null;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            var channels = await db.Channels
                .AsNoTracking()
                .ToListAsync();

            var messages = await db.Messages
                .Include(m => m.Reactions)
                .AsNoTracking()
                .ToListAsync();

            var readStates = await db.ChannelReadStates
                .AsNoTracking()
                .ToListAsync();

            // Bounded by the per-account cap and the daily prune, so the whole table fits.
            var receipts = await db.TextSendReceipts
                .AsNoTracking()
                .ToListAsync();

            var messagesByChannel = messages
                .GroupBy(m => m.ChannelId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(m => m.Timestamp).ToList()
                );

            _logger.LogInformation(
                "Loaded {ChannelCount} channels, {MessageCount} messages, {ReadStateCount} read states, {ReceiptCount} receipts from database",
                channels.Count, messages.Count, readStates.Count, receipts.Count);

            return new ChatSnapshot(channels, messagesByChannel, readStates, receipts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat snapshot from database");
            return null;
        }
    }

    #endregion
}

/// <summary>
/// Represents a snapshot of chat data loaded from the database.
/// </summary>
public record ChatSnapshot(
    List<Channel> Channels,
    Dictionary<Guid, List<ChatMessage>> MessagesByChannel,
    List<ChannelReadState> ReadStates,
    List<TextSendReceipt> Receipts
);
