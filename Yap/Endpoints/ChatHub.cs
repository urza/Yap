using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Yap.Helpers;
using Yap.Models.Accounts;
using Yap.Models.Admin;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Admin;
using Yap.Services.Chat;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>
/// The live session. Everything a connected client does inside a conversation is a method here; everything it
/// learns arrives as a hub message from <see cref="ChatHubBroadcaster"/>. Sending a message is HTTP, for the
/// offline outbox (docs/architecture/chat-hub.md, wire-contract.md).
///
/// A Hub instance lives for one invocation. Per-connection state is the session in PresenceService. This class
/// stores nothing. There are no SignalR groups: every conversation is a direct message, and its events go to
/// the two people by user id (docs/dmonly.md).
/// </summary>
[Authorize]
public sealed class ChatHub(ChatService chat, PresenceService presence, UserService users, MessageMapper map, UserActionLogService log) : Hub
{
    private const int PageSize = 50;   // TL-01, TL-06

    private Guid Me => Context.User!.Id();
    private string MyName => Context.User!.Username();

    // ---- the connection ---------------------------------------------------------------------------------

    public override Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext()!;
        if (http.Request.Query["pwa"] == "1") users.MarkPwaInstalled(Me);   // AD-05: the client runs as the installed app
        var deviceId = http.Request.Query["device"].FirstOrDefault() ?? "unknown";
        users.RecordDevice(Me, deviceId);                                   // AU-20: the device's first hour starts here
        var session = new Session
        {
            ConnectionId = Context.ConnectionId,
            UserId = Me,
            Username = MyName,
            DeviceId = deviceId,
            IsMobile = UserAgent.IsMobile(http.Request.Headers.UserAgent),
            Browser = UserAgent.Browser(http.Request.Headers.UserAgent),
            Os = UserAgent.Os(http.Request.Headers.UserAgent),
            Pwa = http.Request.Query["pwa"] == "1",
            ClientIp = IpHelper.GetClientIp(http),
            Transport = Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "",   // the connection's features, not the request's (AD-26)
            Abort = Context.Abort,
        };
        var joined = presence.Connect(session);
        // After the presence call, not before: the join it raises is where the bot reads LastSeenAt as the end
        // of the previous visit, for the return notice (PR-18). The stamp itself serves AD-04 (the admin list
        // orders by the newest activity) and AD-39 (the inactive sweep).
        users.UpdateLastSeen(Me);
        // A join is a row in the admin's log (AD-18), written here and not from the presence event because the
        // address and the browser are known here. A reload or a second tab is not a join and writes nothing.
        if (joined)
        {
            var device = string.Join(" on ", new[] { session.Browser, session.Os }.Where(s => s != "")) + (session.Pwa ? " (app)" : "");
            log.Log(Me.ToString(), UserActionLog.KnownActions.JOIN, info: device, ip: session.ClientIp, userAgent: http.Request.Headers.UserAgent.ToString());
        }
        return Task.CompletedTask;
    }

    // SignalR passes null when the client closed the connection and an exception when the transport failed.
    // PresenceService keeps the two apart: a closed tab and a lost connection have different grace periods.
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnect(Context.ConnectionId, closedByClient: exception is null);
        users.UpdateLastSeen(Me);   // a long session counts to its end, not to its start (AD-39: the inactive sweep reads this)
        return Task.CompletedTask;
    }

    // ---- the open conversation ---------------------------------------------------------------------------

    /// <summary>One round trip for a switch: viewing, mark-read and the tail (TL-01, TL-03). A person may open the
    /// admin only; the admin may open anyone (docs/dmonly.md). Anything else is "Unknown user".</summary>
    public Conversation OpenDm(string username)
    {
        var channel = chat.GetOrCreateDm(Me, username) ?? throw new HubException("Unknown user");
        presence.SetViewing(Context.ConnectionId, channel.Id);
        chat.MarkRead(Me, channel.Id, "open", Context.ConnectionId);
        var (messages, hasMore) = chat.Tail(channel.Id, PageSize, Me);
        chat.EnsurePreviews(messages);
        return new Conversation(channel.Id, messages.Select(m => map.From(m, Me)).ToList(), hasMore, presence.Typists(channel.Id, except: MyName),
            chat.PinsFor(channel.Id));
    }

    /// <summary>Before Settings, Admin or the people list: the connection stays, the conversation is closed.</summary>
    public void Leave() => presence.SetViewing(Context.ConnectionId, null);

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

    /// <summary>Once per page load, from the first conversation when the server's rows are on screen: the phases of
    /// the load and what the network did, for the load-time table (AD-41).</summary>
    public void ReportLoad(LoadReport report) => presence.ReportLoad(Context.ConnectionId, report);

    /// <summary>The single-use nonce for the IPv4 beacon of this connection. The hub mints it because a client that
    /// skips the negotiate step does not know its connection id, so the bootstrap request cannot name it.</summary>
    public string? BeaconNonce() => presence.CreateBeaconNonce(Context.ConnectionId, Me);

    public void SetStatus(UserStatus status) => presence.SetChosenStatus(Me, status);   // PR-01

    // ---- messages ----------------------------------------------------------------------------------------

    public HubError? React(Guid messageId, string emoji) => chat.ToggleReaction(Me, MyName, messageId, emoji) is { } error ? new(error) : null;

    public HubError? Edit(Guid messageId, string text) => chat.Edit(Me, messageId, text) is { } error ? new(error) : null;

    public HubError? Delete(Guid messageId) => chat.Delete(Me, messageId) is { } error ? new(error) : null;

    /// <summary>MR-77. Both name the conversation too: an unpin may concern a message older than the ones in memory.</summary>
    public HubError? Pin(Guid channelId, Guid messageId) => chat.Pin(Me, MyName, channelId, messageId) is { } error ? new(error) : null;

    public HubError? Unpin(Guid channelId, Guid messageId) => chat.Unpin(Me, MyName, channelId, messageId) is { } error ? new(error) : null;

    /// <summary>A pick in the composer's emoji picker counts as a use (IB-44, PK-07).</summary>
    public void EmojiUsed(string emoji) => users.RecordEmojiUse(Me, emoji);
}
