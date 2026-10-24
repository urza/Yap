using Microsoft.EntityFrameworkCore;
using Yap.Models.Accounts;
using Yap.Models.Admin;
using Yap.Models.Chat;
using Yap.Models.Gifs;
using Yap.Models.Media;
using Yap.Models.Notifications;

namespace Yap.Services.Persistence;

/// <summary>
/// Every write to the database, as a function the <see cref="PersistenceWriter"/> runs later. A service
/// changes its memory, then enqueues one of these. The names say what changed. Outside this file, Entity
/// Framework appears only in the startup loads of the services and in the admin's log pages.
///
/// A row write reads the row's values when it runs, not when it was enqueued. The database converges to
/// memory, and the queue order keeps a delete after the insert it follows. Entities with navigation
/// properties are copied before they are added, so a live object is never attached to a context.
/// </summary>
public static class Writes
{
    // ---- Chat ----------------------------------------------------------------------------------------

    public static Write Channel(Channel channel) => (db, ct) => Upsert(db, db.Channels, channel, ct, channel.Id);

    public static Write DeleteChannel(Guid channelId) => async (db, ct) =>
    {
        // Mute overrides have no foreign key to Channel, so nothing cascades them away.
        await db.ChannelNotificationSettings.Where(s => s.ChannelId == channelId).ExecuteDeleteAsync(ct);
        await db.Channels.Where(c => c.Id == channelId).ExecuteDeleteAsync(ct);
    };

    public static Write Message(ChatMessage message) => (db, ct) =>
    {
        db.Messages.Add(Detached(message));
        return Task.CompletedTask;
    };

    public static Write Messages(IReadOnlyList<ChatMessage> messages) => (db, ct) =>
    {
        db.Messages.AddRange(messages.Select(Detached));
        return Task.CompletedTask;
    };

    public static Write MessageEdit(Guid messageId, string content) => (db, ct) =>
        db.Messages.Where(m => m.Id == messageId)
          .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, content).SetProperty(m => m.IsEdited, true), ct);

    public static Write DeleteMessage(Guid messageId) => (db, ct) =>
        db.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync(ct);

    public static Write Reaction(Guid messageId, Guid userId, string username, string emoji) => async (db, ct) =>
    {
        if (!await db.Reactions.AnyAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, ct))
            db.Reactions.Add(new Reaction { MessageId = messageId, UserId = userId, Username = username, Emoji = emoji });
    };

    public static Write DeleteReaction(Guid messageId, Guid userId, string emoji) => (db, ct) =>
        db.Reactions.Where(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji).ExecuteDeleteAsync(ct);

    /// <summary>The whole read state of one person in one conversation. An upsert, so the first unread of a
    /// pair is stored too (NT-05).</summary>
    public static Write ReadState(ChannelReadState state) => (db, ct) =>
        Upsert(db, db.ChannelReadStates,
               new ChannelReadState { UserId = state.UserId, ChannelId = state.ChannelId, LastReadAt = state.LastReadAt, UnreadCount = state.UnreadCount },
               ct, state.UserId, state.ChannelId);

    // ---- Notifications -------------------------------------------------------------------------------

    public static Write ChannelNotificationSetting(ChannelNotificationSetting setting) => (db, ct) =>
        Upsert(db, db.ChannelNotificationSettings,
               new ChannelNotificationSetting { UserId = setting.UserId, ChannelId = setting.ChannelId, Muted = setting.Muted },
               ct, setting.UserId, setting.ChannelId);

    public static Write PushSubscription(PushSubscription subscription) => (db, ct) =>
        Upsert(db, db.PushSubscriptions, subscription, ct, subscription.Endpoint);

    public static Write DeletePushSubscription(string endpoint) => (db, ct) =>
        db.PushSubscriptions.Where(p => p.Endpoint == endpoint).ExecuteDeleteAsync(ct);

    // ---- Accounts ------------------------------------------------------------------------------------

    public static Write NewUser(User user) => (db, ct) =>
    {
        db.Users.Add(user);
        return Task.CompletedTask;
    };

    /// <summary>The whole account row, whatever changed. One write for every settings change.</summary>
    public static Write User(User user) => (db, ct) => Update(db, db.Users, user, ct, user.Id);

    public static Write AccessLink(AccessLink link) => (db, ct) => Upsert(db, db.AccessLinks, link, ct, link.Id);

    /// <summary>A deleted account's messages and reactions carry its ghost name from now on (AD-40). Database-wide,
    /// so the rows a startup did not load are relabelled too.</summary>
    public static Write RelabelAuthor(Guid userId, string username) => async (db, ct) =>
    {
        await db.Messages.Where(m => m.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Username, username), ct);
        await db.Reactions.Where(r => r.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Username, username), ct);
    };

    /// <summary>The account row. Its read states, mute overrides, GIF favorites and access links cascade; rooms it created
    /// and GIFs it uploaded lose the reference. Messages and reactions have no foreign key and stay (RelabelAuthor runs
    /// ahead of this in the queue). Push subscriptions live in their own store, logs are kept on purpose.</summary>
    public static Write DeleteUser(Guid userId) => (db, ct) =>
        db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

    // ---- GIFs ----------------------------------------------------------------------------------------

    public static Write NewGifEntry(GifEntry entry) => (db, ct) =>
    {
        db.GifEntries.Add(entry);
        return Task.CompletedTask;
    };

    /// <summary>The whole GIF row, whatever changed: formats, tags, use counts, server library, soft delete.</summary>
    public static Write GifEntry(GifEntry entry) => (db, ct) => Update(db, db.GifEntries, entry, ct, entry.Id);

    public static Write DeleteGifEntry(Guid entryId) => (db, ct) =>
        db.GifEntries.Where(g => g.Id == entryId).ExecuteDeleteAsync(ct);

    public static Write Favorite(Guid userId, Guid gifEntryId, string? folder) => (db, ct) =>
        Upsert(db, db.FavoriteGifs, new FavoriteGif(userId, gifEntryId) { Folder = folder }, ct, userId, gifEntryId);

    public static Write DeleteFavorite(Guid userId, Guid gifEntryId) => (db, ct) =>
        db.FavoriteGifs.Where(f => f.UserId == userId && f.GifEntryId == gifEntryId).ExecuteDeleteAsync(ct);

    public static Write DeleteFavoritesOf(Guid gifEntryId) => (db, ct) =>
        db.FavoriteGifs.Where(f => f.GifEntryId == gifEntryId).ExecuteDeleteAsync(ct);

    // ---- Logs ----------------------------------------------------------------------------------------

    public static Write ActionLog(UserActionLog entry) => (db, ct) =>
    {
        db.UserActionLogs.Add(entry);
        return Task.CompletedTask;
    };

    public static Write MediaUploadLog(MediaUploadLog entry) => (db, ct) =>
    {
        db.MediaUploadLogs.Add(entry);
        return Task.CompletedTask;
    };

    public static Write MediaUploadDuration(string storedFileName, long durationMs) => (db, ct) =>
        db.MediaUploadLogs.Where(l => l.StoredFileName == storedFileName)
          .ExecuteUpdateAsync(s => s.SetProperty(l => l.CompressDurationMs, durationMs), ct);

    public static Write DeleteMediaUploadLog(int id) => (db, ct) =>
        db.MediaUploadLogs.Where(l => l.Id == id).ExecuteDeleteAsync(ct);

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task Upsert<T>(ChatDbContext db, DbSet<T> set, T row, CancellationToken ct, params object[] key) where T : class
    {
        var existing = await set.FindAsync(key, ct);
        if (existing is null) set.Add(row);
        else db.Entry(existing).CurrentValues.SetValues(row);
    }

    // For rows that have their own insert write (NewUser, NewGifEntry). A missing row means a delete ran
    // before this write, so the write is dropped: an upsert would bring the deleted row back.
    private static async Task Update<T>(ChatDbContext db, DbSet<T> set, T row, CancellationToken ct, params object[] key) where T : class
    {
        var existing = await set.FindAsync(key, ct);
        if (existing is not null) db.Entry(existing).CurrentValues.SetValues(row);
    }

    // A message in memory carries its reactions and navigation objects. Adding it as is would attach them
    // too and insert rows that exist already, so the insert gets a copy of the scalar values.
    // ClientId is part of the copy: the retry dictionary is rebuilt from the stored rows at startup, so a send
    // repeated across a restart finds its first copy instead of storing a second one.
    private static ChatMessage Detached(ChatMessage m) =>
        new(m.ChannelId, m.UserId, m.Username, m.Content, m.Timestamp, m.ImageUrls.ToList(),
            m.ReplyToMessageId, m.VideoUrls.ToList(), m.GifAttachments.ToList(), m.Voice)
        { Id = m.Id, IsEdited = m.IsEdited, ClientId = m.ClientId, Kind = m.Kind };
}
