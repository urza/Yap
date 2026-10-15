using Microsoft.EntityFrameworkCore;
using Yap.Models.Chat;
using Yap.Models.Notifications;

namespace Yap.Services.Persistence;

/// <summary>Channels, messages with their reactions, and read states, as stored.</summary>
public sealed record ChatSnapshot(List<Channel> Channels, Dictionary<Guid, List<ChatMessage>> MessagesByChannel, List<ChannelReadState> ReadStates);

/// <summary>
/// The chat snapshot read at startup. The other services load their own tables in their own load methods;
/// after startup the database is read only by the admin's log pages, which show stored logs, not chat state.
/// </summary>
public static class Loads
{
    public static async Task<ChatSnapshot> ChatSnapshotAsync(IDbContextFactory<ChatDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var channels = await db.Channels.AsNoTracking().ToListAsync();
        var messages = await db.Messages.Include(m => m.Reactions).AsNoTracking().ToListAsync();
        var readStates = await db.ChannelReadStates.AsNoTracking().ToListAsync();
        var byChannel = messages.GroupBy(m => m.ChannelId).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ToList());
        return new ChatSnapshot(channels, byChannel, readStates);
    }

    public static async Task<List<ChannelNotificationSetting>> ChannelNotificationSettingsAsync(IDbContextFactory<ChatDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ChannelNotificationSettings.AsNoTracking().ToListAsync();
    }

    public static async Task<List<PushSubscription>> PushSubscriptionsAsync(IDbContextFactory<ChatDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PushSubscriptions.AsNoTracking().ToListAsync();
    }
}
