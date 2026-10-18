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
/// SignalR. A conversation event goes to its two people on every device. Presence and profiles go to the
/// people who may see that person: everyone for the admin and the bot, the admin and the person themself for
/// anyone else, because a person's client knows the admin, the bot and itself only (docs/dmonly.md). The table
/// is in docs/architecture/bootstrap-and-events.md.
/// </summary>
public sealed class ChatHubBroadcaster(IHubContext<ChatHub> hub, ChatService chat, PresenceService presence, UserService users,
    NotificationSettingsService mutes, GifService gifs, RegistrationGateService gate, SystemBotService bot, AccountDeletionService deletion,
    MessageMapper map, LoadCounters load, ILogger<ChatHubBroadcaster> log) : IHostedService
{
    private readonly List<Action> _unsubscribe = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        On<ChatMessage>(h => chat.OnMessageReceived += h, h => chat.OnMessageReceived -= h, m => ToChannel(m.ChannelId, "message", map.From(m, null)));
        On<ChatMessage>(h => chat.OnMessageEdited += h, h => chat.OnMessageEdited -= h, m => ToChannel(m.ChannelId, "messageEdited", map.From(m, null)));
        On<Guid, Guid>(h => chat.OnMessageDeleted += h, h => chat.OnMessageDeleted -= h, (channelId, id) => ToChannel(channelId, "messageDeleted", channelId, id));
        On<ChatMessage>(h => chat.OnReactionChanged += h, h => chat.OnReactionChanged -= h, m => ToChannel(m.ChannelId, "reactions", m.Id, map.Reactions(m)));
        On<ChatMessage>(h => chat.OnPreviewsReady += h, h => chat.OnPreviewsReady -= h, m => ToChannel(m.ChannelId, "previews", m.Id, chat.Previews(m)));
        On<Guid, Guid, int>(h => chat.OnUnreadChanged += h, h => chat.OnUnreadChanged -= h, (userId, channelId, count) => ToUser(userId, "unread", channelId, count));
        On<Channel>(h => chat.OnDmStarted += h, h => chat.OnDmStarted -= h, DmStarted);
        On<Channel>(h => chat.OnPinsChanged += h, h => chat.OnPinsChanged -= h, c => ToChannel(c.Id, "pins", c.Id, chat.PinsFor(c.Id)));   // SH-48

        On<string, EffectiveStatus, PresenceChange>(h => presence.OnPresenceChanged += h, h => presence.OnPresenceChanged -= h,
            (username, status, change) => ToWatchersOf(username, "presence", username, status, change));
        On<Guid, string, bool>(h => presence.OnTypingChanged += h, h => presence.OnTypingChanged -= h,
            (channelId, username, on) => ToChannel(channelId, "typing", channelId, username, on));
        On<Guid, UserStatus>(h => presence.OnChosenStatusChanged += h, h => presence.OnChosenStatusChanged -= h,
            (userId, status) => ToUser(userId, "me", new { status }));

        On<User>(h => users.OnProfileChanged += h, h => users.OnProfileChanged -= h,
            u => ToWatchersOf(u.Username, "profile", UserDto.From(u, users.IsAdmin(u.Id), bot.IsBotUser(u.Username))));
        On<Guid, object>(h => users.OnSettingsChanged += h, h => users.OnSettingsChanged -= h, (userId, partial) => ToUser(userId, "me", partial));
        On<string>(h => deletion.OnAccountDeleted += h, h => deletion.OnAccountDeleted -= h, username => ToAll("accountDeleted", username));   // AD-40
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

    // A conversation event goes to its two people, on all their devices.
    private void ToChannel(Guid channelId, string name, params object?[] args)
    {
        var channel = chat.GetChannel(channelId);
        if (channel is null) return;
        Fire(hub.Clients.Users(channel.ParticipantIds.Select(id => id.ToString()).ToList()).SendCoreAsync(name, args));
    }

    // Who may learn about a person: everyone for the admin and the bot (every client lists them); for anyone else
    // the admin and the person themself, the only clients whose directory holds that person.
    private void ToWatchersOf(string username, string name, params object?[] args)
    {
        if (users.IsAdmin(username) || bot.IsBotUser(username) || users.GetByUsername(username) is not { } subject)
        {
            Fire(hub.Clients.All.SendCoreAsync(name, args));
            return;
        }
        var watchers = new List<string> { subject.Id.ToString() };
        if (users.GetAdminUserId() is { } admin) watchers.Add(admin.ToString());
        Fire(hub.Clients.Users(watchers).SendCoreAsync(name, args));
    }

    private void ToUser(Guid userId, string name, params object?[] args) => Fire(hub.Clients.User(userId.ToString()).SendCoreAsync(name, args));

    // A deleted account (AD-40): every client drops the name; a person's client that never held it drops nothing.
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
