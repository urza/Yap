using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Data;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Handles write-through persistence for chat data.
/// When disabled, all methods are no-ops.
/// </summary>
public class ChatPersistenceService
{
    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;
    private readonly ILogger<ChatPersistenceService> _logger;

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
            }
        }
    }

    #region Channel Operations

    public async Task PersistChannelAsync(Channel channel)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            var existing = await db.Channels.FindAsync(channel.Id);
            if (existing != null)
            {
                db.Entry(existing).CurrentValues.SetValues(channel);
            }
            else
            {
                db.Channels.Add(channel);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist channel {ChannelId}", channel.Id);
        }
    }

    public async Task DeleteChannelAsync(Guid channelId)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            await db.Channels.Where(c => c.Id == channelId).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete channel {ChannelId}", channelId);
        }
    }

    #endregion

    #region Message Operations

    public async Task PersistMessageAsync(ChatMessage message)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            var existing = await db.Messages.FindAsync(message.Id);
            if (existing != null)
            {
                // Update only the mutable fields
                existing.Content = message.Content;
                existing.IsEdited = message.IsEdited;
            }
            else
            {
                // Create a detached copy to avoid navigation property issues
                var newMessage = new ChatMessage(
                    message.ChannelId,
                    message.Username,
                    message.Content,
                    message.Timestamp,
                    message.ImageUrls.ToList()
                )
                {
                    Id = message.Id,
                    IsEdited = message.IsEdited
                };

                db.Messages.Add(newMessage);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist message {MessageId}", message.Id);
        }
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            await db.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete message {MessageId}", messageId);
        }
    }

    public async Task TrimMessagesAsync(Guid channelId, int maxCount)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            // Get IDs of messages to delete (oldest ones beyond maxCount)
            var toDelete = await db.Messages
                .Where(m => m.ChannelId == channelId)
                .OrderByDescending(m => m.Timestamp)
                .Skip(maxCount)
                .Select(m => m.Id)
                .ToListAsync();

            if (toDelete.Count > 0)
            {
                await db.Messages.Where(m => toDelete.Contains(m.Id)).ExecuteDeleteAsync();
                _logger.LogDebug("Trimmed {Count} old messages from channel {ChannelId}", toDelete.Count, channelId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trim messages for channel {ChannelId}", channelId);
        }
    }

    #endregion

    #region Reaction Operations

    public async Task AddReactionAsync(Guid messageId, string emoji, string username)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            // Check if reaction already exists
            var exists = await db.Reactions.AnyAsync(r =>
                r.MessageId == messageId &&
                r.Emoji == emoji &&
                r.Username == username);

            if (!exists)
            {
                db.Reactions.Add(new Reaction
                {
                    MessageId = messageId,
                    Emoji = emoji,
                    Username = username
                });
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add reaction to message {MessageId}", messageId);
        }
    }

    public async Task RemoveReactionAsync(Guid messageId, string emoji, string username)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            await db.Reactions
                .Where(r => r.MessageId == messageId && r.Emoji == emoji && r.Username == username)
                .ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove reaction from message {MessageId}", messageId);
        }
    }

    #endregion

    #region Push Subscription Operations

    public async Task SavePushSubscriptionAsync(PushSubscription subscription)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            var existing = await db.PushSubscriptions.FindAsync(subscription.Endpoint);
            if (existing != null)
            {
                existing.Username = subscription.Username;
                existing.P256dh = subscription.P256dh;
                existing.Auth = subscription.Auth;
            }
            else
            {
                db.PushSubscriptions.Add(subscription);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save push subscription");
        }
    }

    public async Task RemovePushSubscriptionAsync(string endpoint)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            await db.PushSubscriptions.Where(p => p.Endpoint == endpoint).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove push subscription");
        }
    }

    public async Task RemovePushSubscriptionsByUsernameAsync(string username)
    {
        if (!IsEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            await db.PushSubscriptions.Where(p => p.Username == username).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove push subscriptions for user {Username}", username);
        }
    }

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
    /// Loads all channels and messages from the database.
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

            var messagesByChannel = messages
                .GroupBy(m => m.ChannelId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(m => m.Timestamp).ToList()
                );

            _logger.LogInformation(
                "Loaded {ChannelCount} channels and {MessageCount} messages from database",
                channels.Count, messages.Count);

            return new ChatSnapshot(channels, messagesByChannel);
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
    Dictionary<Guid, List<ChatMessage>> MessagesByChannel
);
