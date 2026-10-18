using Microsoft.EntityFrameworkCore;
using Yap.Configuration;
using Yap.Models.Chat;
using Yap.Models.Notifications;

namespace Yap.Services.Persistence;

/// <summary>Channels, messages with their reactions, and read states, as stored. <paramref name="MessagesLeft"/> counts the
/// message rows the load bounds kept in the database.</summary>
public sealed record ChatSnapshot(List<Channel> Channels, Dictionary<Guid, List<ChatMessage>> MessagesByChannel, List<ChannelReadState> ReadStates, int MessagesLeft);

/// <summary>
/// The chat snapshot read at startup. The other services load their own tables in their own load methods;
/// after startup the database is read only by the admin's log pages, which show stored logs, not chat state.
/// </summary>
public static class Loads
{
    public static async Task<ChatSnapshot> ChatSnapshotAsync(IDbContextFactory<ChatDbContext> factory, LoadSettings bounds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var channels = await db.Channels.AsNoTracking().ToListAsync();
        var readStates = await db.ChannelReadStates.AsNoTracking().ToListAsync();

        // One query per conversation, so both bounds run in SQL on the (ChannelId, Timestamp) index: the newest rows,
        // at most MaxMessagesPerChannel of them and none older than MaxDays. The rest never leaves the database.
        var since = DateTime.UtcNow.AddDays(-Math.Min(bounds.MaxDays, 36_500));   // a century at most: a value meant as "forever" must not throw in AddDays
        var byChannel = new Dictionary<Guid, List<ChatMessage>>();
        foreach (var channel in channels)
        {
            IQueryable<ChatMessage> query = db.Messages.Include(m => m.Reactions).AsNoTracking().Where(m => m.ChannelId == channel.Id);
            if (bounds.MaxDays > 0) query = query.Where(m => m.Timestamp >= since);
            query = query.OrderByDescending(m => m.Timestamp);
            if (bounds.MaxMessagesPerChannel > 0) query = query.Take(bounds.MaxMessagesPerChannel);
            var messages = await query.ToListAsync();
            messages.Reverse();   // the lists in memory are oldest first
            byChannel[channel.Id] = messages;
        }
        var left = bounds.Any ? await db.Messages.CountAsync() - byChannel.Values.Sum(list => list.Count) : 0;
        return new ChatSnapshot(channels, byChannel, readStates, left);
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
