using Microsoft.AspNetCore.SignalR;
using Yap.Models.Accounts;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Admin;
using Yap.Services.Chat;
using Yap.Services.Gifs;
using Yap.Services.Notifications;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>
/// Listens to the services' events and sends one hub message for each. The services know nothing about
/// SignalR. A room event goes to the connections that have the room open; a direct message goes to the two
/// people on every device; presence, rooms and profiles go to everyone. The table is in
/// docs/architecture/bootstrap-and-events.md.
/// </summary>
public sealed class ChatHubBroadcaster(IHubContext<ChatHub> hub, ChatService chat, PresenceService presence, UserService users,
    NotificationSettingsService mutes, GifService gifs, RegistrationGateService gate, SystemBotService bot, MessageMapper map,
    LoadCounters load, ILogger<ChatHubBroadcaster> log) : IHostedService
{
    private readonly List<Action> _unsubscribe = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A new message is inside everyone's history; only the quote of a reply can differ between readers (TL-05).
        On<ChatMessage>(h => chat.OnMessageReceived += h, h => chat.OnMessageReceived -= h, m =>
        {
            if (m.ReplyToMessageId is null) ToChannel(m.ChannelId, "message", map.From(m, null));
            else ToReaders(m, "message", viewer => [map.From(m, viewer)]);
        });
        On<ChatMessage>(h => chat.OnMessageEdited += h, h => chat.OnMessageEdited -= h, m => ToReaders(m, "messageEdited", viewer => [map.From(m, viewer)]));
        On<Guid, Guid>(h => chat.OnMessageDeleted += h, h => chat.OnMessageDeleted -= h, (channelId, id) => ToChannel(channelId, "messageDeleted", channelId, id));
        On<ChatMessage>(h => chat.OnReactionChanged += h, h => chat.OnReactionChanged -= h, m => ToReaders(m, "reactions", _ => [m.Id, map.Reactions(m)]));
        On<ChatMessage>(h => chat.OnPreviewsReady += h, h => chat.OnPreviewsReady -= h, m => ToReaders(m, "previews", _ => [m.Id, map.Previews(m)]));
        On<Guid, Guid, int>(h => chat.OnUnreadChanged += h, h => chat.OnUnreadChanged -= h, (userId, channelId, count) => ToUser(userId, "unread", channelId, count));
        On<Channel>(h => chat.OnDmStarted += h, h => chat.OnDmStarted -= h, DmStarted);
        On(h => chat.OnRoomsChanged += h, h => chat.OnRoomsChanged -= h, () => ToAll("rooms", chat.Rooms().Select(RoomDto.From).ToList()));

        On<string, EffectiveStatus, PresenceChange>(h => presence.OnPresenceChanged += h, h => presence.OnPresenceChanged -= h,
            (username, status, change) => ToAll("presence", username, status, change));
        On<Guid, string, bool>(h => presence.OnTypingChanged += h, h => presence.OnTypingChanged -= h,
            (channelId, username, on) => ToChannel(channelId, "typing", channelId, username, on));
        On<Guid, UserStatus>(h => presence.OnChosenStatusChanged += h, h => presence.OnChosenStatusChanged -= h,
            (userId, status) => ToUser(userId, "me", new { status }));

        On<User>(h => users.OnProfileChanged += h, h => users.OnProfileChanged -= h,
            u => ToAll("profile", UserDto.From(u, users.IsAdmin(u.Id), bot.IsBotUser(u.Username))));
        On<Guid, object>(h => users.OnSettingsChanged += h, h => users.OnSettingsChanged -= h, (userId, partial) => ToUser(userId, "me", partial));
        On<Guid, List<string>>(h => users.OnRecentEmojiChanged += h, h => users.OnRecentEmojiChanged -= h,
            (userId, recent) => ToUser(userId, "me", new { emoji = new { recent, quickReactions = chat.QuickReactions(userId) } }));   // MR-41
        On<Guid>(h => mutes.OnChanged += h, h => mutes.OnChanged -= h, userId => ToUser(userId, "me", new { mutes = mutes.For(userId) }));
        On<Guid>(h => gifs.OnFavoritesChanged += h, h => gifs.OnFavoritesChanged -= h, userId => ToUser(userId, "gifFavorites", gifs.GetFavorites(userId).Select(e => e.Id).ToList()));
        On(h => gate.OnPendingChanged += h, h => gate.OnPendingChanged -= h, () =>
        {
            if (users.GetAdminUserId() is { } admin)
                ToUser(admin, "pending", gate.GetPendingUsers().Select(p => new PendingDto(p.Username, p.RequestedAt)).ToList());
        });
        On<GifImportProgress>(h => gifs.OnImportProgress += h, h => gifs.OnImportProgress -= h, p => ToUser(p.ActorUserId, "gifImport", p));   // ST-22
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var off in _unsubscribe) off();
        _unsubscribe.Clear();
        return Task.CompletedTask;
    }

    // Each person gets their own partner name, so the same event becomes two messages.
    private void DmStarted(Channel c)
    {
        var at = DateTime.UtcNow;
        if (c.Participant1Id is { } p1 && c.Participant2 is { } n2) ToUser(p1, "dm", new DmDto(c.Id, n2, at));
        if (c.Participant2Id is { } p2 && c.Participant1 is { } n1) ToUser(p2, "dm", new DmDto(c.Id, n1, at));
    }

    // A room message goes to the connections that have the room open. A direct message goes to the two
    // people, on all their devices. The channel type decides.
    private void ToChannel(Guid channelId, string name, params object?[] args)
    {
        var channel = chat.GetChannel(channelId);
        if (channel is null) return;
        var target = channel.IsDirectMessage
            ? hub.Clients.Users(new[] { channel.Participant1Id, channel.Participant2Id }.OfType<Guid>().Select(id => id.ToString()).ToList())
            : hub.Clients.Group(ChatHub.Group(channelId));
        Fire(target.SendCoreAsync(name, args));
    }

    // A room with a history cutoff (TL-05) has a different past for each reader: an event about a message goes to
    // the connections whose person may read that message, with the arguments built for that person (the quote of
    // a reply). A direct message and an unrestricted room have one truth for everyone and get one send.
    private void ToReaders(ChatMessage m, string name, Func<Guid?, object?[]> argsFor)
    {
        var channel = chat.GetChannel(m.ChannelId);
        if (channel is null) return;
        if (!chat.IsRestricted(channel)) { ToChannel(m.ChannelId, name, argsFor(null)); return; }
        foreach (var person in presence.All().Where(s => s.Connected && s.Viewing == channel.Id).GroupBy(s => s.UserId))
        {
            if (!chat.MayRead(person.Key, m)) continue;
            Fire(hub.Clients.Clients(person.Select(s => s.ConnectionId).ToList()).SendCoreAsync(name, argsFor(person.Key)));
        }
    }

    private void ToUser(Guid userId, string name, params object?[] args) => Fire(hub.Clients.User(userId.ToString()).SendCoreAsync(name, args));

    private void ToAll(string name, params object?[] args) => Fire(hub.Clients.All.SendCoreAsync(name, args));

    // The service events are synchronous. The send is not awaited, so a slow client never delays a service.
    private void Fire(Task send)
    {
        load.HubSend();   // one per send call, not per recipient: the admin load card shows the fan-out rate
        send.ContinueWith(t => log.LogWarning(t.Exception, "A hub send failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    // Subscribe now, remember how to unsubscribe at stop.
    private void On(Action<Action> add, Action<Action> remove, Action handler) { add(handler); _unsubscribe.Add(() => remove(handler)); }
    private void On<T>(Action<Action<T>> add, Action<Action<T>> remove, Action<T> handler) { add(handler); _unsubscribe.Add(() => remove(handler)); }
    private void On<T1, T2>(Action<Action<T1, T2>> add, Action<Action<T1, T2>> remove, Action<T1, T2> handler) { add(handler); _unsubscribe.Add(() => remove(handler)); }
    private void On<T1, T2, T3>(Action<Action<T1, T2, T3>> add, Action<Action<T1, T2, T3>> remove, Action<T1, T2, T3> handler) { add(handler); _unsubscribe.Add(() => remove(handler)); }
}
