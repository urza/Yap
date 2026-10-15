using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Yap.Models.Accounts;
using Yap.Models.Admin;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Gifs;
using Yap.Services.Notifications;
using Yap.Services.Persistence;
using Yap.Services.Presence;

namespace Yap.Services.Chat;

/// <summary>
/// The chat: rooms, direct messages, messages, reactions and read states, in memory, in one instance. This
/// is the source of truth. Every change happens here first, raises one event, and is enqueued on the
/// persistence writer. The database is read once, at startup. See docs/architecture/message-path.md and
/// chat-hub.md.
/// </summary>
public class ChatService
{
    private readonly PersistenceWriter _writer;
    private readonly IDbContextFactory<ChatDbContext>? _db;   // startup load only; null without persistence
    private readonly PushNotificationService _pushService;
    private readonly PresenceService _presence;
    private readonly UserService _userService;
    private readonly LinkPreviewService _linkPreviewService;
    private readonly LinkPreviewSettingsService _linkPreviewSettings;
    private readonly MediaCacheService _mediaCacheService;
    private readonly GifService _gifService;
    private readonly NotificationAudit _audit;
    private readonly NotificationSettingsService _notifications;
    private readonly ILogger<ChatService> _logger;

    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _channelMessages = new();
    private readonly ConcurrentDictionary<Guid, Lock> _channelLocks = new();          // one lock per message list
    private readonly ConcurrentDictionary<Guid, Guid> _channelOfMessage = new();        // the hub names a message by id alone
    private readonly ConcurrentDictionary<(Guid UserId, Guid ChannelId), ChannelReadState> _readStates = new();

    // (sender, clientId) -> message id, for one day. A repeated send returns the existing message instead
    // of a duplicate (offline design notes, "Server side"). Rebuilt from recent messages at startup.
    private readonly ConcurrentDictionary<(Guid UserId, Guid ClientId), (Guid MessageId, DateTime At)> _byClientId = new();
    private static readonly TimeSpan ClientIdMemory = TimeSpan.FromDays(1);

    private Guid _lobbyId;

    public const int MaxTextLength = 4000;      // IB-14, MR-11
    public const int MaxMediaPerMessage = 10;   // IB-32

    // Every change raises one event. The broadcaster turns each into one hub message; the bot listens too.
    public event Action<ChatMessage>? OnMessageReceived;
    public event Action<ChatMessage>? OnMessageEdited;
    public event Action<Guid, Guid>? OnMessageDeleted;            // channelId, messageId
    public event Action<ChatMessage>? OnReactionChanged;
    public event Action<ChatMessage>? OnPreviewsReady;            // a link card or a cached media player is ready (MR-22, MR-23)
    public event Action<Guid, Guid, int>? OnUnreadChanged;        // userId, channelId, count
    public event Action<Channel>? OnDmStarted;                    // a direct message got its first message (SH-29)
    public event Action? OnRoomsChanged;                          // created, updated, reordered or deleted (SH-25)

    public ChatService(IServiceProvider services, PersistenceWriter writer, PushNotificationService pushService, UserService userService,
        PresenceService presence, LinkPreviewService linkPreviewService, LinkPreviewSettingsService linkPreviewSettings,
        MediaCacheService mediaCacheService, GifService gifService, NotificationAudit audit,
        NotificationSettingsService notifications, ILogger<ChatService> logger)
    {
        _db = services.GetService<IDbContextFactory<ChatDbContext>>();
        _writer = writer;
        _pushService = pushService;
        _presence = presence;
        _userService = userService;
        _linkPreviewService = linkPreviewService;
        _linkPreviewSettings = linkPreviewSettings;
        _mediaCacheService = mediaCacheService;
        _gifService = gifService;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;

        _linkPreviewService.OnPreviewFetched = (messageId, _, _) => RaisePreviewsReady(messageId);

        // A cached media file completes the LinkPreview of its URL, then the card becomes a player (MR-23).
        _mediaCacheService.OnMediaCached = (messageId, url, entry) =>
        {
            var preview = _linkPreviewService.GetOrCreatePreview(url);
            preview.CachedMediaUrl = entry.LocalUrl;
            preview.CachedPosterUrl = entry.PosterUrl;
            preview.MediaType = entry.MediaType;
            preview.MediaDurationSeconds = entry.DurationSeconds;
            if (entry.Width > 0 && entry.Height > 0)
            {
                preview.MediaWidth = entry.Width;
                preview.MediaHeight = entry.Height;
            }
            // yt-dlp's title and thumbnail fill in when the page scrape got none (YouTube serves bots a consent page).
            if (string.IsNullOrEmpty(preview.Title)) preview.Title = entry.Title;
            if (string.IsNullOrEmpty(preview.ImageUrl)) preview.ImageUrl = entry.Thumbnail;
            if (!string.IsNullOrEmpty(preview.Title) || !string.IsNullOrEmpty(preview.ImageUrl) || !string.IsNullOrEmpty(preview.CachedMediaUrl))
                preview.Failed = false;
            RaisePreviewsReady(messageId);
        };

        // The lobby exists from the first second. The startup load replaces it when the database has one.
        var lobby = Channel.CreateRoom("lobby", createdById: null, createdBy: null, isDefault: true);
        _lobbyId = lobby.Id;
        _channels[lobby.Id] = lobby;
        _channelMessages[lobby.Id] = [];
    }

    #region Startup and diagnostics

    /// <summary>Fills memory from the database. Runs once, after the users are loaded.</summary>
    public async Task InitializeAsync()
    {
        if (_db is null) return;
        var snapshot = await Loads.ChatSnapshotAsync(_db);

        _channels.Clear();
        _channelMessages.Clear();
        foreach (var channel in snapshot.Channels)
        {
            _channels[channel.Id] = channel;
            var messages = snapshot.MessagesByChannel.GetValueOrDefault(channel.Id, []);
            _channelMessages[channel.Id] = messages;
            foreach (var m in messages)
            {
                _channelOfMessage[m.Id] = channel.Id;
                if (m.ClientId is { } clientId && DateTime.UtcNow - m.Timestamp < ClientIdMemory)
                    _byClientId[(m.UserId, clientId)] = (m.Id, m.Timestamp);
            }
        }
        foreach (var state in snapshot.ReadStates)
            _readStates[(state.UserId, state.ChannelId)] = state;

        var orphaned = _channels.Values.Count(c => c.IsDirectMessage
            && (_userService.GetByUsername(c.Participant1 ?? "") is null || _userService.GetByUsername(c.Participant2 ?? "") is null));
        if (orphaned > 0)
            _logger.LogWarning("{Count} direct message channel(s) name a participant that no longer exists", orphaned);

        var lobby = _channels.Values.FirstOrDefault(c => c.Type == ChannelType.Room && c.IsDefault);
        if (lobby is not null)
        {
            _lobbyId = lobby.Id;
        }
        else
        {
            lobby = Channel.CreateRoom("lobby", createdById: null, createdBy: null, isDefault: true);
            _lobbyId = lobby.Id;
            _channels[lobby.Id] = lobby;
            _channelMessages[lobby.Id] = [];
            _writer.Enqueue(Writes.Channel(lobby));
        }

        _logger.LogInformation("Loaded {Channels} channels, {Messages} messages, {ReadStates} read states",
            snapshot.Channels.Count, _channelOfMessage.Count, snapshot.ReadStates.Count);
    }

    public Guid LobbyId => _lobbyId;

    public ChatDiagnostics GetDiagnostics() => new()
    {
        UserSessions = _presence.Stats().Connected,
        UniqueUsers = _presence.ConnectedUserIds().Count,
        Channels = _channels.Count,
        RoomChannels = _channels.Values.Count(c => c.Type == ChannelType.Room),
        DmChannels = _channels.Values.Count(c => c.Type == ChannelType.DirectMessage),
        TotalMessages = _channelOfMessage.Count,
        EventSubscribers = new Dictionary<string, int>
        {
            ["OnMessageReceived"] = OnMessageReceived?.GetInvocationList().Length ?? 0,
            ["OnMessageEdited"] = OnMessageEdited?.GetInvocationList().Length ?? 0,
            ["OnMessageDeleted"] = OnMessageDeleted?.GetInvocationList().Length ?? 0,
            ["OnReactionChanged"] = OnReactionChanged?.GetInvocationList().Length ?? 0,
            ["OnPreviewsReady"] = OnPreviewsReady?.GetInvocationList().Length ?? 0,
            ["OnUnreadChanged"] = OnUnreadChanged?.GetInvocationList().Length ?? 0,
            ["OnDmStarted"] = OnDmStarted?.GetInvocationList().Length ?? 0,
            ["OnRoomsChanged"] = OnRoomsChanged?.GetInvocationList().Length ?? 0,
        },
        AdminUser = _userService.GetAdminUsername(),
    };

    #endregion

    #region Rooms

    public List<Channel> Rooms() =>
        _channels.Values.Where(c => c.Type == ChannelType.Room).OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToList();

    public Channel? GetChannel(Guid channelId) => _channels.GetValueOrDefault(channelId);

    public Channel? GetRoom(Guid channelId) => _channels.TryGetValue(channelId, out var c) && !c.IsDirectMessage ? c : null;

    /// <summary>AD-30 to AD-34. Returns the room, or the reason the admin sees.</summary>
    public (Channel? Room, string? Error) CreateRoom(Guid adminUserId, string name, string? description,
        ChannelPermission writePermission, HistoryLimit historyLimit, bool sinceJoined)
    {
        if (!_userService.IsAdmin(adminUserId)) return (null, "Not authorized.");
        name = name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return (null, "Channel name is required.");
        if (name.Contains(' ')) return (null, "Lowercase, no spaces.");
        if (_channels.Values.Any(c => c.Type == ChannelType.Room && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return (null, "A channel with that name already exists.");

        var bottom = _channels.Values.Where(c => c.Type == ChannelType.Room).Select(c => c.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var admin = _userService.GetById(adminUserId);
        var room = Channel.CreateRoom(name, adminUserId, admin?.Username, description: Clean(description), sortOrder: bottom,
            writePermission: writePermission, historyLimit: historyLimit, sinceJoined: sinceJoined);
        _channels[room.Id] = room;
        _channelMessages[room.Id] = [];

        OnRoomsChanged?.Invoke();
        _writer.Enqueue(Writes.Channel(room));
        return (room, null);
    }

    public string? UpdateRoom(Guid adminUserId, Guid channelId, string name, string? description,
        ChannelPermission writePermission, HistoryLimit historyLimit, bool sinceJoined)
    {
        if (!_userService.IsAdmin(adminUserId)) return "Not authorized.";
        if (GetRoom(channelId) is not { } room) return "Channel not found.";
        name = name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return "Channel name is required.";
        if (name.Contains(' ')) return "Lowercase, no spaces.";
        if (_channels.Values.Any(c => c.Type == ChannelType.Room && c.Id != channelId && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "A channel with that name already exists.";

        room.Name = name;
        room.Description = Clean(description);
        room.WritePermission = writePermission;
        room.HistoryLimit = historyLimit;
        room.SinceJoined = sinceJoined;

        OnRoomsChanged?.Invoke();
        _writer.Enqueue(Writes.Channel(room));
        return null;
    }

    /// <summary>Swaps the sort order with the neighbour (AD-33).</summary>
    public bool MoveRoom(Guid adminUserId, Guid channelId, bool up)
    {
        if (!_userService.IsAdmin(adminUserId)) return false;
        var rooms = Rooms();
        var index = rooms.FindIndex(r => r.Id == channelId);
        var other = up ? index - 1 : index + 1;
        if (index < 0 || other < 0 || other >= rooms.Count) return false;

        (rooms[index].SortOrder, rooms[other].SortOrder) = (rooms[other].SortOrder, rooms[index].SortOrder);
        OnRoomsChanged?.Invoke();
        _writer.Enqueue(Writes.Channel(rooms[index]));
        _writer.Enqueue(Writes.Channel(rooms[other]));
        return true;
    }

    /// <summary>The lobby cannot be deleted (AD-35). The mutes of every person for the room are cleared.</summary>
    public bool DeleteRoom(Guid adminUserId, Guid channelId)
    {
        if (!_userService.IsAdmin(adminUserId)) return false;
        if (GetRoom(channelId) is not { } room || room.IsDefault) return false;

        _channels.TryRemove(channelId, out _);
        if (_channelMessages.TryRemove(channelId, out var messages))
            foreach (var m in messages) _channelOfMessage.TryRemove(m.Id, out _);
        _channelLocks.TryRemove(channelId, out _);
        foreach (var key in _readStates.Keys.Where(k => k.ChannelId == channelId).ToList())
            _readStates.TryRemove(key, out _);
        _presence.ForgetChannel(channelId);
        _notifications.ClearOverridesForChannel(channelId);

        OnRoomsChanged?.Invoke();
        _writer.Enqueue(Writes.DeleteChannel(channelId));
        return true;
    }

    private static string? Clean(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    #endregion

    #region Direct messages

    /// <summary>The direct message between two people, created on first open. Null when the other person is unknown.</summary>
    public Channel? GetOrCreateDm(Guid userId, Guid otherUserId)
    {
        if (userId == otherUserId) return null;
        if (_channels.Values.FirstOrDefault(c => c.IsDMBetween(userId, otherUserId)) is { } existing) return existing;
        if (_userService.GetById(userId) is not { } me || _userService.GetById(otherUserId) is not { } other) return null;

        var channel = Channel.CreateDM(me.Id, me.Username, other.Id, other.Username);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = [];
        _writer.Enqueue(Writes.Channel(channel));
        return channel;
    }

    public Channel? GetOrCreateDm(Guid userId, string otherUsername) =>
        _userService.GetByUsername(otherUsername) is { } other ? GetOrCreateDm(userId, other.Id) : null;

    public List<Channel> DmChannelsOf(Guid userId) =>
        _channels.Values.Where(c => c.IsDirectMessage && c.CanAccess(userId)).ToList();

    /// <summary>The person's direct messages with at least one message, for the sidebar (SH-29).</summary>
    public List<(Channel Channel, string Partner, DateTime LastMessageAt)> DmConversations(Guid userId)
    {
        var me = _userService.GetById(userId);
        if (me is null) return [];
        var result = new List<(Channel, string, DateTime)>();
        foreach (var channel in DmChannelsOf(userId))
        {
            var partner = channel.GetOtherParticipant(me.Username);
            if (string.IsNullOrEmpty(partner)) continue;
            DateTime? last;
            lock (LockOf(channel.Id)) last = _channelMessages.GetValueOrDefault(channel.Id)?.LastOrDefault()?.Timestamp;
            if (last is { } at) result.Add((channel, partner, at));
        }
        return result;
    }

    #endregion

    #region Reading

    /// <summary>The newest messages of a conversation, within the viewer's history cutoff (TL-01, TL-05).</summary>
    public (List<ChatMessage> Messages, bool HasMore, bool Restricted) Tail(Guid channelId, int count, Guid viewerId) =>
        Page(channelId, count, viewerId, before: null);

    /// <summary>The messages before the oldest loaded one (TL-06). The cursor is found by id; when that message
    /// was deleted meanwhile, the timestamp takes over.</summary>
    public (List<ChatMessage> Messages, bool HasMore) Older(Guid channelId, DateTime beforeTimestamp, Guid beforeId, int count, Guid viewerId)
    {
        var (messages, hasMore, _) = Page(channelId, count, viewerId, before: (beforeTimestamp, beforeId));
        return (messages, hasMore);
    }

    private (List<ChatMessage>, bool HasMore, bool Restricted) Page(Guid channelId, int count, Guid viewerId, (DateTime Timestamp, Guid Id)? before)
    {
        if (!_channels.TryGetValue(channelId, out var channel) || !channel.CanAccess(viewerId) || !_channelMessages.TryGetValue(channelId, out var all))
            return ([], false, false);
        var cutoff = Cutoff(channel, viewerId);

        lock (LockOf(channelId))
        {
            var end = all.Count;   // exclusive: the page is all[start..end)
            if (before is { } cursor)
            {
                end = all.FindIndex(m => m.Id == cursor.Id);
                if (end < 0) end = all.FindIndex(m => m.Timestamp >= cursor.Timestamp);
                if (end < 0) end = all.Count;
            }
            var first = cutoff is { } c ? all.FindIndex(m => m.Timestamp >= c) : 0;   // the oldest message the viewer may see
            if (first < 0) first = all.Count;
            var start = Math.Max(first, end - count);
            var page = start < end ? all.GetRange(start, end - start) : [];
            return (page, start > first, first > 0);
        }
    }

    /// <summary>A non-admin sees a room from the later of the room's history window and, when the room says so,
    /// the person's own account creation. Admins and direct messages have no cutoff (TL-05).</summary>
    private DateTime? Cutoff(Channel channel, Guid viewerId)
    {
        if (channel.IsDirectMessage || _userService.IsAdmin(viewerId)) return null;
        var window = channel.GetHistoryCutoff();
        var joined = channel.SinceJoined ? _userService.GetById(viewerId)?.CreatedAt : null;
        if (window is null) return joined;
        if (joined is null) return window;
        return window > joined ? window : joined;
    }

    /// <summary>Whether a room hides part of its past from non-admins (TL-05): a time window, or "since joined".</summary>
    public bool IsRestricted(Channel channel) => !channel.IsDirectMessage && (channel.HistoryLimit != HistoryLimit.Unlimited || channel.SinceJoined);

    /// <summary>Whether the person may read this message: access to the conversation, and the message inside the
    /// person's history cutoff. The one rule for history pages, the quote of a reply and live events (TL-05).</summary>
    public bool MayRead(Guid viewerId, ChatMessage m) =>
        _channels.TryGetValue(m.ChannelId, out var channel) && channel.CanAccess(viewerId) && (Cutoff(channel, viewerId) is not { } c || m.Timestamp >= c);

    public ChatMessage? GetMessage(Guid messageId)
    {
        if (!_channelOfMessage.TryGetValue(messageId, out var channelId) || !_channelMessages.TryGetValue(channelId, out var messages)) return null;
        lock (LockOf(channelId)) return messages.FirstOrDefault(m => m.Id == messageId);
    }

    #endregion

    #region Sending

    /// <summary>
    /// The one write path for messages. Changes memory, raises the events, enqueues the writes, returns.
    /// Nothing here waits for the database (message-path.md).
    /// </summary>
    public SendResult Send(SendMessage request)
    {
        if (!_channels.TryGetValue(request.ChannelId, out var channel) || !channel.CanAccess(request.UserId))
            return SendResult.Refused("Unknown conversation", 404);
        if (!channel.CanWrite(request.UserId, _userService.IsAdmin(request.UserId)))
            return SendResult.Refused("You do not have permission to send messages in this channel", 403);   // IB-62

        var text = request.Text ?? "";
        var media = (request.ImageUrls?.Count ?? 0) + (request.VideoUrls?.Count ?? 0) + (request.GifAttachments?.Count ?? 0);
        if (text.Length > MaxTextLength) return SendResult.Refused($"A message has at most {MaxTextLength} characters.");
        if (media > MaxMediaPerMessage) return SendResult.Refused($"Choose at most {MaxMediaPerMessage} files.");   // IB-32
        if (string.IsNullOrWhiteSpace(text) && media == 0) return SendResult.Refused("The message is empty.");
        // A reply quotes its original to every reader of the conversation. An original from another conversation
        // would carry that conversation's text here, a direct message's included (GEN-04). An original that is gone
        // passes: the quote then says so (MR-35).
        if (request.ReplyToMessageId is { } replyId && _channelOfMessage.TryGetValue(replyId, out var home) && home != channel.Id)
            return SendResult.Refused("The original message is not in this conversation.");

        if (request.ClientId is { } clientId)
        {
            // The key is reserved before the insert, so two parallel posts with one clientId cannot both store.
            if (!_byClientId.TryAdd((request.UserId, clientId), (Guid.Empty, DateTime.UtcNow)))
            {
                if (!_byClientId.TryGetValue((request.UserId, clientId), out var known) || known.MessageId == Guid.Empty)
                    return SendResult.Refused("This message is already being sent.", 409);
                if (GetMessage(known.MessageId) is { } existing)
                    return SendResult.Ok(existing);   // the same send twice: the first result, no duplicate
                // The first copy was deleted since: this send stores a new message under the same key.
            }
            if (_byClientId.Count > 1000) ForgetOldClientIds();
        }

        var message = new ChatMessage(channel.Id, request.UserId, request.Username, text, DateTime.UtcNow,
            request.ImageUrls, request.ReplyToMessageId, request.VideoUrls, request.GifAttachments) { ClientId = request.ClientId };
        _gifService.IncrementReferences(request.GifAttachments);   // an entry in chat history is never evicted

        bool firstInDm;
        lock (LockOf(channel.Id))
        {
            var messages = _channelMessages.GetOrAdd(channel.Id, _ => []);
            firstInDm = channel.IsDirectMessage && messages.Count == 0;
            messages.Add(message);
        }
        _channelOfMessage[message.Id] = channel.Id;
        if (request.ClientId is { } id) _byClientId[(request.UserId, id)] = (message.Id, message.Timestamp);

        var unread = IncrementUnread(channel, request.UserId);
        _presence.StopTyping(channel.Id, request.Username);   // IB-59

        OnMessageReceived?.Invoke(message);
        if (firstInDm) OnDmStarted?.Invoke(channel);
        foreach (var (userId, count) in unread) OnUnreadChanged?.Invoke(userId, channel.Id, count);

        _writer.Enqueue(Writes.Message(message));
        foreach (var (userId, _) in unread) _writer.Enqueue(Writes.ReadState(_readStates[(userId, channel.Id)]));

        QueuePreviews(message);
        DispatchPush(channel, request.Username, message);
        return SendResult.Ok(message);
    }

    private void ForgetOldClientIds()
    {
        var limit = DateTime.UtcNow - ClientIdMemory;
        foreach (var key in _byClientId.Where(e => e.Value.At < limit).Select(e => e.Key).ToList())
            _byClientId.TryRemove(key, out _);
    }

    /// <summary>Link cards for a text message, at most five, in text order (MR-19). Media messages get none.</summary>
    private void QueuePreviews(ChatMessage message)
    {
        if (!_linkPreviewSettings.Enabled || message.HasMedia) return;
        foreach (var url in LinkPreviewService.ExtractUrls(message.Content).Take(5))
        {
            _linkPreviewService.QueueFetch(message.Id, url);
            if (_linkPreviewSettings.MediaCachingEnabled) _mediaCacheService.QueueDownload(message.Id, url);
        }
    }

    private void RaisePreviewsReady(Guid messageId)
    {
        if (GetMessage(messageId) is { } message) OnPreviewsReady?.Invoke(message);
    }

    /// <summary>
    /// Web push for the people who are not looking (NT-25, NT-26). Away does not suppress it: a phone in a pocket
    /// must get it, and an idle but visible desktop still gets it. Muted conversations get nothing.
    /// </summary>
    private List<ChatMessage> RepliesTo(ChatMessage original)
    {
        lock (LockOf(original.ChannelId))
            return _channelMessages.TryGetValue(original.ChannelId, out var list) ? list.Where(m => m.ReplyToMessageId == original.Id).ToList() : [];
    }

    private void DispatchPush(Channel channel, string senderUsername, ChatMessage message)
    {
        var preview = message.HasGifs ? "[GIF]" : message.HasMedia ? "[Attachment]" : message.Content;   // NT-27
        var senderName = _userService.GetByUsername(senderUsername)?.DisplayName ?? senderUsername;        // NT-27: the display name

        foreach (var recipient in PushRecipients(channel, senderUsername))
        {
            var lookingNow = _presence.IsOnlineAndVisible(recipient.Id);
            var sessions = _presence.Describe(recipient.Id);
            var subscriptions = _pushService.GetSubscriptionCount(recipient.Username);
            var totalUnread = TotalUnread(recipient.Id);

            if (lookingNow)
            {
                _audit.RecordPushDecision(senderUsername, recipient.Username, "suppressed: Online + visible", sessions, subscriptions, totalUnread);
                continue;
            }
            _audit.RecordPushDecision(senderUsername, recipient.Username, "push", sessions, subscriptions, totalUnread);

            _ = channel.IsDirectMessage
                ? _pushService.SendDmNotificationAsync(recipient.Username, senderUsername, preview, totalUnread, senderName)
                : _pushService.SendRoomNotificationAsync(recipient.Username, channel.Name, channel.Id, senderUsername, preview, totalUnread, senderName);
        }
    }

    private IEnumerable<User> PushRecipients(Channel channel, string senderUsername)
    {
        if (channel.IsDirectMessage)
        {
            var other = channel.GetOtherParticipant(senderUsername);
            var user = other is null ? null : _userService.GetByUsername(other);
            if (user is not null && !_notifications.IsMuted(user, channel.Id, isDirectMessage: true)) yield return user;
            yield break;
        }
        // Rooms are muted by default, so this normally yields nobody and the scan stays cheap.
        foreach (var user in _userService.GetAllUsers())
            if (!user.Username.Equals(senderUsername, StringComparison.OrdinalIgnoreCase) && !_notifications.IsMuted(user, channel.Id, isDirectMessage: false))
                yield return user;
    }

    #endregion

    #region Edit, delete, react

    /// <summary>MR-57, MR-61. Returns the reason for a refusal, or null.</summary>
    public string? Edit(Guid userId, Guid messageId, string text)
    {
        if (GetMessage(messageId) is not { } message) return "Message not found.";
        if (message.UserId != userId) return "You can edit your own messages only.";
        if (message.HasMedia) return "A media message cannot be edited.";
        if (text.Length > MaxTextLength) return $"A message has at most {MaxTextLength} characters.";
        if (string.IsNullOrWhiteSpace(text) || text == message.Content) return null;   // nothing changes, nothing to say

        message.Content = text;
        message.IsEdited = true;
        OnMessageEdited?.Invoke(message);
        foreach (var reply in RepliesTo(message)) OnMessageEdited?.Invoke(reply);   // their quotes show the new text (MR-32)
        _writer.Enqueue(Writes.MessageEdit(message.Id, text));
        QueuePreviews(message);   // an edit that adds or removes a link adds or removes the card (MR-22)
        return null;
    }

    /// <summary>MR-64, MR-68. The row disappears for everyone; nothing is stored about the deletion.</summary>
    public string? Delete(Guid userId, Guid messageId)
    {
        if (!_channelOfMessage.TryGetValue(messageId, out var channelId) || !_channelMessages.TryGetValue(channelId, out var messages))
            return "Message not found.";
        ChatMessage? message;
        lock (LockOf(channelId))
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
            if (message is null) return "Message not found.";
            if (message.UserId != userId) return "You can delete your own messages only.";
            messages.Remove(message);
        }
        _channelOfMessage.TryRemove(messageId, out _);
        _gifService.DecrementReferences(message.GifAttachments);

        OnMessageDeleted?.Invoke(channelId, messageId);
        _writer.Enqueue(Writes.DeleteMessage(messageId));
        return null;
    }

    /// <summary>MR-40, MR-45. Toggles the person's reaction with that emoji. Anyone who can see the message can react.</summary>
    public string? ToggleReaction(Guid userId, string username, Guid messageId, string emoji)
    {
        if (emoji.Length is 0 or > 64) return "Not an emoji.";
        if (GetMessage(messageId) is not { } message) return "Message not found.";
        if (!_channels.TryGetValue(message.ChannelId, out var channel) || !channel.CanAccess(userId)) return "Message not found.";

        bool added;
        lock (message.Reactions)
        {
            var mine = message.Reactions.FirstOrDefault(r => r.Emoji == emoji && r.UserId == userId);
            if (mine is not null) { message.Reactions.Remove(mine); added = false; }
            else { message.Reactions.Add(new Reaction { MessageId = messageId, UserId = userId, Username = username, Emoji = emoji }); added = true; }
        }

        OnReactionChanged?.Invoke(message);
        _userService.RecordEmojiUse(userId, emoji);   // a reaction, added or removed, moves the emoji to the front of the recent list (MR-44, PK-07)
        _writer.Enqueue(added ? Writes.Reaction(messageId, userId, username, emoji) : Writes.DeleteReaction(messageId, userId, emoji));
        return null;
    }

    private static readonly string[] DefaultQuickReactions = ["❤️", "😂", "👍"];

    /// <summary>The three quick reactions of the action bar: the person's most used, padded with the fixed three in
    /// order and without duplicates (MR-41).</summary>
    public List<string> QuickReactions(Guid userId)
    {
        var top = TopReactionEmojis(userId, 3);
        foreach (var e in DefaultQuickReactions)
        {
            if (top.Count >= 3) break;
            if (!top.Contains(e)) top.Add(e);
        }
        return top;
    }

    /// <summary>The emoji the person reacts with most (MR-41). Reactions only: typed emoji are a different habit.</summary>
    public List<string> TopReactionEmojis(Guid userId, int count)
    {
        var mine = new List<string>();
        foreach (var (channelId, messages) in _channelMessages)
            lock (LockOf(channelId))
                foreach (var message in messages)
                    lock (message.Reactions)
                        mine.AddRange(message.Reactions.Where(r => r.UserId == userId).Select(r => r.Emoji));
        return mine.GroupBy(e => e).OrderByDescending(g => g.Count()).Take(count).Select(g => g.Key).ToList();
    }

    #endregion

    #region Read state

    public int UnreadCount(Guid userId, Guid channelId) =>
        _readStates.TryGetValue((userId, channelId), out var state) ? state.UnreadCount : 0;

    /// <summary>The person's raw counts above zero, for the bootstrap. The client applies the mutes (NT-04, NT-09).</summary>
    public Dictionary<Guid, int> UnreadCounts(Guid userId) =>
        _readStates.Where(e => e.Key.UserId == userId && e.Value.UnreadCount > 0).ToDictionary(e => e.Key.ChannelId, e => e.Value.UnreadCount);

    /// <summary>The sum over unmuted conversations, for the push payload of a closed app (NT-11).</summary>
    public int TotalUnread(Guid userId)
    {
        var user = _userService.GetById(userId);
        if (user is null) return 0;
        return _readStates.Where(e => e.Key.UserId == userId && e.Value.UnreadCount > 0)
            .Where(e => _channels.TryGetValue(e.Key.ChannelId, out var ch) && !_notifications.IsMuted(user, ch.Id, ch.IsDirectMessage))
            .Sum(e => e.Value.UnreadCount);
    }

    /// <summary>
    /// Clears the whole conversation for the person (NT-06, NT-08). The hub calls it on open and when the client
    /// says the person read the newest message. Nothing is raised when nothing changed.
    /// </summary>
    public void MarkRead(Guid userId, Guid channelId, string source = "open", string? callerConnectionId = null)
    {
        if (!_channels.TryGetValue(channelId, out var channel) || !channel.CanAccess(userId)) return;   // a channel id from the client is not an entry ticket
        var state = _readStates.GetOrAdd((userId, channelId), _ => new ChannelReadState { UserId = userId, ChannelId = channelId, UnreadCount = 0 });
        int had;
        lock (state)
        {
            had = state.UnreadCount;
            state.UnreadCount = 0;
            state.LastReadAt = DateTime.UtcNow;
        }
        if (had == 0) return;

        if (channel.IsDirectMessage)   // AD-27: the unread audit covers direct messages only
        {
            var session = callerConnectionId is null ? null : _presence.Get(callerConnectionId);
            _audit.RecordUnreadChange(_userService.GetById(userId)?.Username ?? "?", Describe(channel), "clear", had, source,
                _audit.DescribeCallerSession(session, _presence.EffectiveOf(userId)));
        }
        OnUnreadChanged?.Invoke(userId, channelId, 0);
        _writer.Enqueue(Writes.ReadState(state));
    }

    /// <summary>
    /// NT-02, NT-03: a direct message counts for the other person; a room message counts for everyone who has Yap
    /// open and everyone who has the room unmuted, minus the sender. Returns each affected person's new count.
    /// </summary>
    private List<(Guid UserId, int Count)> IncrementUnread(Channel channel, Guid senderId)
    {
        IEnumerable<Guid> recipients;
        if (channel.IsDirectMessage)
            recipients = channel.GetOtherParticipantId(senderId) is { } other ? [other] : [];
        else
            recipients = _presence.ConnectedUserIds()
                .Concat(_userService.GetAllUsers().Where(u => !_notifications.IsMuted(u, channel.Id, isDirectMessage: false)).Select(u => u.Id))
                .Where(id => id != senderId).Distinct();

        var result = new List<(Guid, int)>();
        foreach (var userId in recipients)
        {
            var state = _readStates.GetOrAdd((userId, channel.Id), _ => new ChannelReadState { UserId = userId, ChannelId = channel.Id, LastReadAt = DateTime.MinValue });
            int count;
            lock (state) count = ++state.UnreadCount;
            result.Add((userId, count));

            if (channel.IsDirectMessage)
                _audit.RecordUnreadChange(_userService.GetById(userId)?.Username ?? "?", Describe(channel), "+1", count,
                    $"msg from {_userService.GetById(senderId)?.Username}", "—");
        }
        return result;
    }

    private static string Describe(Channel channel) =>
        channel.IsDirectMessage ? $"{channel.Participant1} ↔ {channel.Participant2}" : $"#{channel.Name}";

    #endregion

    #region Test data

    /// <summary>AD-08: real messages from the admin and five fictional people, spread over a span in bursts. No unread counts, no push.</summary>
    public int GenerateTestMessages(Guid channelId, Guid userId, string username, int count, TimeSpan span)
    {
        if (!_channels.ContainsKey(channelId) || count <= 0) return 0;
        var speakers = new List<(Guid Id, string Name)> { (userId, username) };
        speakers.AddRange(_fakeUsers);

        var now = DateTime.UtcNow;
        var cursor = now - span;
        var average = span.TotalSeconds / count;
        var rng = Random.Shared;
        var speaker = rng.Next(speakers.Count);
        var burst = rng.Next(1, 6);
        var made = new List<ChatMessage>(count);
        for (var i = 0; i < count; i++)
        {
            cursor = cursor.AddSeconds(average * (0.5 + rng.NextDouble()));
            if (cursor > now) cursor = now;
            var (id, name) = speakers[speaker];
            made.Add(new ChatMessage(channelId, id, name, $"[Test #{i + 1}/{count}] {_testPhrases[rng.Next(_testPhrases.Length)]}", cursor));
            if (--burst <= 0)
            {
                var next = rng.Next(speakers.Count - 1);
                speaker = next >= speaker ? next + 1 : next;
                burst = rng.Next(1, 6);
            }
        }

        lock (LockOf(channelId))
        {
            var messages = _channelMessages.GetOrAdd(channelId, _ => []);
            messages.AddRange(made);
            messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        foreach (var m in made) _channelOfMessage[m.Id] = channelId;

        OnMessageReceived?.Invoke(made[^1]);   // only the last one is announced: the rows are history, seen whole at the next open
        _writer.Enqueue(Writes.Messages(made));
        return count;
    }

    private static readonly string[] _testPhrases =
    [
        "The quick brown fox jumps over the lazy dog",
        "Hello world! This is a test message",
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit",
        "Testing 1, 2, 3... Is this thing on?",
        "All your base are belong to us",
        "I'm sorry Dave, I'm afraid I can't do that",
        "To be or not to be, that is the question",
        "May the force be with you",
        "Here's looking at you, kid",
        "Life is like a box of chocolates",
        "Houston, we have a problem",
        "Elementary, my dear Watson",
        "That's one small step for man, one giant leap for mankind",
        "Winter is coming",
        "I'll be back",
        "Do or do not, there is no try",
        "Just keep swimming",
        "Bazinga!",
        "It's dangerous to go alone! Take this.",
        "The cake is a lie",
    ];

    private static readonly (Guid Id, string Name)[] _fakeUsers =
    [
        (Guid.Parse("aa000000-0000-0000-0000-000000000001"), "Alice"),
        (Guid.Parse("aa000000-0000-0000-0000-000000000002"), "Bob"),
        (Guid.Parse("aa000000-0000-0000-0000-000000000003"), "Charlie"),
        (Guid.Parse("aa000000-0000-0000-0000-000000000004"), "Diana"),
        (Guid.Parse("aa000000-0000-0000-0000-000000000005"), "Eve"),
    ];

    #endregion

    private Lock LockOf(Guid channelId) => _channelLocks.GetOrAdd(channelId, _ => new Lock());
}
