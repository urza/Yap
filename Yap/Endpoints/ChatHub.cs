using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Yap.Helpers;
using Yap.Models.Accounts;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Chat;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>
/// The live session. Everything a connected client does inside a conversation is a method here; everything it
/// learns arrives as a hub message from <see cref="ChatHubBroadcaster"/>. Sending a message is HTTP, for the
/// offline outbox (docs/architecture/chat-hub.md, wire-contract.md).
///
/// A Hub instance lives for one invocation. Per-connection state is the session in PresenceService and the
/// SignalR group membership. This class stores nothing.
/// </summary>
[Authorize]
public sealed class ChatHub(ChatService chat, PresenceService presence, UserService users, MessageMapper map) : Hub
{
    private const int PageSize = 50;   // TL-01, TL-06

    private Guid Me => Context.User!.Id();
    private string MyName => Context.User!.Username();
    public static string Group(Guid channelId) => $"channel:{channelId}";   // shared with the broadcaster

    // ---- the connection ---------------------------------------------------------------------------------

    public override Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext()!;
        users.UpdateLastSeen(Me);                                           // AD-04: the admin list orders by the newest activity
        if (http.Request.Query["pwa"] == "1") users.MarkPwaInstalled(Me);   // AD-05: the client runs as the installed app
        var deviceId = http.Request.Query["device"].FirstOrDefault() ?? "unknown";
        users.RecordDevice(Me, deviceId);                                   // AU-20: the device's first hour starts here
        presence.Connect(new Session
        {
            ConnectionId = Context.ConnectionId,
            UserId = Me,
            Username = MyName,
            DeviceId = deviceId,
            IsMobile = UserAgent.IsMobile(http.Request.Headers.UserAgent),
            ClientIp = IpHelper.GetClientIp(http),
            Transport = Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "",   // the connection's features, not the request's (AD-26)
            Abort = Context.Abort,
        });
        return Task.CompletedTask;
    }

    // SignalR passes null when the client closed the connection and an exception when the transport failed.
    // PresenceService keeps the two apart: a closed tab and a lost connection have different grace periods.
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnect(Context.ConnectionId, closedByClient: exception is null);
        return Task.CompletedTask;
    }

    // ---- the open conversation ---------------------------------------------------------------------------

    /// <summary>One round trip for a switch: group, viewing, mark-read and the tail (TL-01, TL-03).</summary>
    public async Task<Conversation> OpenRoom(Guid channelId)
    {
        var room = chat.GetRoom(channelId) ?? throw new HubException("Unknown room");   // the client goes to the lobby (SH-43)
        await Switch(room.Id, joinGroup: true);
        return Open(room);
    }

    public async Task<Conversation> OpenDm(string username)
    {
        var channel = chat.GetOrCreateDm(Me, username) ?? throw new HubException("Unknown user");
        await Switch(channel.Id, joinGroup: false);   // direct-message events go to the two people, not to a group
        return Open(channel);
    }

    /// <summary>Before Settings or Admin: the connection stays, the conversation is closed.</summary>
    public async Task Leave()
    {
        await LeaveCurrentGroup();
        presence.SetViewing(Context.ConnectionId, null);
    }

    public Page Older(Guid channelId, DateTime beforeTimestamp, Guid beforeId)
    {
        var (messages, hasMore) = chat.Older(channelId, beforeTimestamp, beforeId, PageSize, Me);
        chat.EnsurePreviews(messages);   // the cards of history arrive by the "previews" message (MR-22)
        return new Page(messages.Select(m => map.From(m, Me)).ToList(), hasMore);
    }

    /// <summary>The client calls this when the page is visible, the person is not away and the view is at the bottom (NT-07).</summary>
    public void MarkRead(Guid channelId) => chat.MarkRead(Me, channelId, "read", Context.ConnectionId);

    public void Typing(Guid channelId, bool on) => presence.SetTyping(Context.ConnectionId, channelId, on);

    // ---- liveness and status -----------------------------------------------------------------------------

    /// <summary>Every 10 s visible, 60 s hidden. Carries the waits the client measured since the last beat, and
    /// returns the server time so the client can measure the round trip (AD-26).</summary>
    public long Heartbeat(bool visible, int idleSeconds, int? lastRttMs, WaitSample[]? samples)
    {
        presence.Heartbeat(Context.ConnectionId, visible, idleSeconds, lastRttMs, samples);
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void SetStatus(UserStatus status) => presence.SetChosenStatus(Me, status);   // PR-01

    // ---- messages ----------------------------------------------------------------------------------------

    public HubError? React(Guid messageId, string emoji) => chat.ToggleReaction(Me, MyName, messageId, emoji) is { } error ? new(error) : null;

    public HubError? Edit(Guid messageId, string text) => chat.Edit(Me, messageId, text) is { } error ? new(error) : null;

    public HubError? Delete(Guid messageId) => chat.Delete(Me, messageId) is { } error ? new(error) : null;

    /// <summary>MR-76. Both name the conversation too: an unpin may concern a message older than the ones in memory.</summary>
    public HubError? Pin(Guid channelId, Guid messageId) => chat.Pin(Me, MyName, channelId, messageId) is { } error ? new(error) : null;

    public HubError? Unpin(Guid channelId, Guid messageId) => chat.Unpin(Me, MyName, channelId, messageId) is { } error ? new(error) : null;

    /// <summary>A pick in the composer's emoji picker counts as a use (IB-44, PK-07).</summary>
    public void EmojiUsed(string emoji) => users.RecordEmojiUse(Me, emoji);

    // ---- inside -------------------------------------------------------------------------------------------

    private async Task Switch(Guid channelId, bool joinGroup)
    {
        await LeaveCurrentGroup();
        if (joinGroup) await Groups.AddToGroupAsync(Context.ConnectionId, Group(channelId));
        presence.SetViewing(Context.ConnectionId, channelId);
    }

    private async Task LeaveCurrentGroup()
    {
        if (presence.Get(Context.ConnectionId)?.Viewing is { } previous && chat.GetRoom(previous) is not null)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(previous));
    }

    private Conversation Open(Channel channel)
    {
        chat.MarkRead(Me, channel.Id, "open", Context.ConnectionId);
        var (messages, hasMore, restricted) = chat.Tail(channel.Id, PageSize, Me);
        chat.EnsurePreviews(messages);
        return new Conversation(channel.Id, messages.Select(m => map.From(m, Me)).ToList(), hasMore, restricted, presence.Typists(channel.Id, except: MyName),
            chat.PinsFor(channel.Id, Me));
    }
}
