using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Models.Accounts;
using Yap.Models.Admin;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Gifs;
using Yap.Services.Media;
using Yap.Services.Notifications;
using Yap.Services.Persistence;
using Yap.Services.Presence;

namespace Yap.Services.Chat;

/// <summary>
/// The chat: direct messages, messages, reactions and read states, in memory, in one instance. This is the
/// source of truth. Every change happens here first, raises one event, and is enqueued on the persistence
/// writer. The database is read once, at startup. See docs/architecture/message-path.md and chat-hub.md.
///
/// This build has no rooms (docs/dmonly.md). Every conversation is between two people, and one of the two is
/// the admin: a person talks to the admin and to nobody else. The bot may write into a person's conversation
/// with the admin as a system sender.
/// </summary>
public class ChatService
{
    private readonly PersistenceWriter _writer;
    private readonly IDbContextFactory<ChatDbContext>? _db;   // startup load only; null without persistence
    private readonly LoadSettings _load;                       // what the startup load reads per conversation
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

    public const int MaxTextLength = 4000;      // IB-14, MR-11
    public const int MaxMediaPerMessage = 10;   // IB-32
    // The waveform of a voice message: one base-36 digit per bar, at most 64 bars (VoiceAttachment).
    private static readonly System.Text.RegularExpressions.Regex VoicePeaks = new("^[0-9a-z]{0,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Every change raises one event. The broadcaster turns each into one hub message; the bot listens too.
    public event Action<ChatMessage>? OnMessageReceived;
    public event Action<ChatMessage>? OnMessageEdited;
    public event Action<Guid, Guid>? OnMessageDeleted;            // channelId, messageId
    public event Action<ChatMessage>? OnReactionChanged;
    public event Action<ChatMessage>? OnPreviewsReady;            // a link card or a cached media player is ready (MR-22, MR-23)
    public event Action<Guid, Guid, int>? OnUnreadChanged;        // userId, channelId, count
    public event Action<Channel>? OnDmStarted;                    // a direct message got its first message (SH-29)
    public event Action<Channel>? OnPinsChanged;                  // a pin or an unpin, or an edit or a delete of a pinned message (SH-48)

    public ChatService(IServiceProvider services, PersistenceWriter writer, PushNotificationService pushService, UserService userService,
        PresenceService presence, LinkPreviewService linkPreviewService, LinkPreviewSettingsService linkPreviewSettings,
        MediaCacheService mediaCacheService, GifService gifService, NotificationAudit audit,
        NotificationSettingsService notifications, IOptions<PersistenceSettings> persistence, ILogger<ChatService> logger)
    {
        _db = services.GetService<IDbContextFactory<ChatDbContext>>();
        _load = persistence.Value.Load;
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
            _linkPreviewService.AttachMedia(url, entry);
            RaisePreviewsReady(messageId);
        };
    }

    #region Startup and diagnostics

    /// <summary>Fills memory from the database. Runs once, after the users are loaded.</summary>
    public async Task InitializeAsync()
    {
        if (_db is null) return;
        var snapshot = await Loads.ChatSnapshotAsync(_db, _load);

        _channels.Clear();
        _channelMessages.Clear();
        // A database from the room build may hold rooms. They are not loaded: this build has no page for them,
        // and a row nobody can open must not count or push (docs/dmonly.md).
        foreach (var channel in snapshot.Channels.Where(c => c.IsDirectMessage))
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
        foreach (var state in snapshot.ReadStates.Where(s => _channels.ContainsKey(s.ChannelId)))
            _readStates[(state.UserId, state.ChannelId)] = state;

        var orphaned = _channels.Values.Count(c =>
            _userService.GetByUsername(c.Participant1 ?? "") is null || _userService.GetByUsername(c.Participant2 ?? "") is null);
        if (orphaned > 0)
            _logger.LogWarning("{Count} direct message channel(s) name a participant that no longer exists", orphaned);

        if (snapshot.MessagesLeft > 0)
            _logger.LogInformation("Loaded {Channels} direct messages, {Messages} messages ({Left} more stay in the database: {Bounds}), {ReadStates} read states",
                _channels.Count, _channelOfMessage.Count, snapshot.MessagesLeft, _load.Describe(), _readStates.Count);
        else
            _logger.LogInformation("Loaded {Channels} direct messages, {Messages} messages, {ReadStates} read states",
                _channels.Count, _channelOfMessage.Count, _readStates.Count);
    }

    public ChatDiagnostics GetDiagnostics() => new()
    {
        UserSessions = _presence.Stats().Connected,
        UniqueUsers = _presence.ConnectedUserIds().Count,
        Channels = _channels.Count,
        DmChannels = _channels.Count,
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
        },
        AdminUser = _userService.GetAdminUsername(),
    };

    #endregion

    #region Direct messages

    public Channel? GetChannel(Guid channelId) => _channels.GetValueOrDefault(channelId);

    // A conversation leaves memory with everything keyed on it; the database cascade follows the one write.
    private void Purge(Guid channelId)
    {
        _channels.TryRemove(channelId, out _);
        if (_channelMessages.TryRemove(channelId, out var messages))
            foreach (var m in messages) _channelOfMessage.TryRemove(m.Id, out _);
        _channelLocks.TryRemove(channelId, out _);
        foreach (var key in _readStates.Keys.Where(k => k.ChannelId == channelId).ToList())
            _readStates.TryRemove(key, out _);
        _presence.ForgetChannel(channelId);
        _notifications.ClearOverridesForChannel(channelId);
        _writer.Enqueue(Writes.DeleteChannel(channelId));
    }

    /// <summary>
    /// An account is deleted (AD-39). Its direct messages go on both sides: a conversation with nobody could not be
    /// opened or answered, and nothing lets the other person dismiss it. Every conversation is a direct message in this
    /// build, so no message of the person stays in memory; the database-wide relabel to <paramref name="ghostName"/>
    /// still runs for rows a startup did not load (a room build's database). Returns the deleted direct messages.
    /// </summary>
    public List<Guid> RemoveAccount(Guid userId, string ghostName)
    {
        var dms = DmChannelsOf(userId).Select(c => c.Id).ToList();
        foreach (var id in dms) Purge(id);
        foreach (var key in _readStates.Keys.Where(k => k.UserId == userId).ToList()) _readStates.TryRemove(key, out _);
        foreach (var key in _byClientId.Keys.Where(k => k.UserId == userId).ToList()) _byClientId.TryRemove(key, out _);
        _writer.Enqueue(Writes.RelabelAuthor(userId, ghostName));
        return dms;
    }

    /// <summary>
    /// The direct message between two people, created on first open. Null when the other person is unknown, or
    /// when neither of the two is the admin: a person talks to the admin only (docs/dmonly.md). The bot reaches
    /// the admin through this rule too (the admin is one side), and reaches a person through the person's
    /// conversation with the admin, never through one of its own.
    /// </summary>
    public Channel? GetOrCreateDm(Guid userId, Guid otherUserId)
    {
        if (userId == otherUserId) return null;
        if (!_userService.IsAdmin(userId) && !_userService.IsAdmin(otherUserId)) return null;
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
        _channels.Values.Where(c => c.CanAccess(userId)).ToList();

    /// <summary>The person's direct messages with at least one message (SH-29): the admin's people list orders by them.</summary>
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

    /// <summary>Whether the conversation holds a message from this sender. The bot's welcome guard across restarts.</summary>
    public bool HasMessageFrom(Guid channelId, Guid userId)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages)) return false;
        lock (LockOf(channelId)) return messages.Any(m => m.UserId == userId);
    }

    #endregion

    #region Reading

    /// <summary>The newest messages of a conversation (TL-01).</summary>
    public (List<ChatMessage> Messages, bool HasMore) Tail(Guid channelId, int count, Guid viewerId) =>
        Page(channelId, count, viewerId, before: null);

    /// <summary>The messages before the oldest loaded one (TL-06). The cursor is found by id; when that message
    /// was deleted meanwhile, the timestamp takes over.</summary>
    public (List<ChatMessage> Messages, bool HasMore) Older(Guid channelId, DateTime beforeTimestamp, Guid beforeId, int count, Guid viewerId) =>
        Page(channelId, count, viewerId, before: (beforeTimestamp, beforeId));

    private (List<ChatMessage>, bool HasMore) Page(Guid channelId, int count, Guid viewerId, (DateTime Timestamp, Guid Id)? before)
    {
        if (!_channels.TryGetValue(channelId, out var channel) || !channel.CanAccess(viewerId) || !_channelMessages.TryGetValue(channelId, out var all))
            return ([], false);

        lock (LockOf(channelId))
        {
            var end = all.Count;   // exclusive: the page is all[start..end)
            if (before is { } cursor)
            {
                end = all.FindIndex(m => m.Id == cursor.Id);
                if (end < 0) end = all.FindIndex(m => m.Timestamp >= cursor.Timestamp);
                if (end < 0) end = all.Count;
            }
            var start = Math.Max(0, end - count);
            var page = start < end ? all.GetRange(start, end - start) : [];
            return (page, start > 0);
        }
    }

    /// <summary>Whether the person may read this message: the person is one of the two in its conversation. The one
    /// rule for history pages, the quote of a reply and live events.</summary>
    public bool MayRead(Guid viewerId, ChatMessage m) =>
        _channels.TryGetValue(m.ChannelId, out var channel) && channel.CanAccess(viewerId);

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
        if (!_channels.TryGetValue(request.ChannelId, out var channel) || !(request.System || channel.CanAccess(request.UserId)))
            return SendResult.Refused("Unknown conversation", 404);

        var text = request.Text ?? "";
        var media = (request.ImageUrls?.Count ?? 0) + (request.VideoUrls?.Count ?? 0) + (request.GifAttachments?.Count ?? 0);
        if (request.Voice is { } voice)
        {
            // A voice message carries nothing else (IB-70): the row has no caption and no gallery to draw with it.
            if (media > 0 || !string.IsNullOrWhiteSpace(text)) return SendResult.Refused("A voice message is sent alone.");
            if (voice.DurationMs <= 0 || voice.DurationMs > (VoiceService.MaxSeconds + 10) * 1000) return SendResult.Refused("The voice message is too long.");
            if (!VoicePeaks.IsMatch(voice.Peaks ?? "")) return SendResult.Refused("The voice message is malformed.");
            media = 1;
        }
        if (text.Length > MaxTextLength) return SendResult.Refused($"A message has at most {MaxTextLength} characters.");
        if (media > MaxMediaPerMessage) return SendResult.Refused($"Choose at most {MaxMediaPerMessage} files.");   // IB-32
        if (string.IsNullOrWhiteSpace(text) && media == 0) return SendResult.Refused("The message is empty.");
        // A reply quotes its original to every reader of the conversation. An original from another conversation
        // would carry that conversation's text here (GEN-04). An original that is gone passes: the quote then says so (MR-35).
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
            request.ImageUrls, request.ReplyToMessageId, request.VideoUrls, request.GifAttachments, request.Voice) { ClientId = request.ClientId };
        _gifService.IncrementReferences(request.GifAttachments);   // an entry in chat history is never evicted

        bool first;
        lock (LockOf(channel.Id))
        {
            var messages = _channelMessages.GetOrAdd(channel.Id, _ => []);
            first = messages.Count == 0;
            messages.Add(message);
        }
        _channelOfMessage[message.Id] = channel.Id;
        if (request.ClientId is { } id) _byClientId[(request.UserId, id)] = (message.Id, message.Timestamp);

        var unread = IncrementUnread(channel, request.UserId);
        _presence.StopTyping(channel.Id, request.Username);   // IB-59

        OnMessageReceived?.Invoke(message);
        if (first) OnDmStarted?.Invoke(channel);
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

    /// <summary>Link cards for a text message, in text order (MR-19). Media messages get none.</summary>
    private void QueuePreviews(ChatMessage message) => EnsurePreviews([message]);

    /// <summary>
    /// The cards of a message or of a served page. Previews live in memory for an hour and not across a restart, so
    /// a page of history has none until they are fetched again: the hub calls this for every page it serves, the
    /// fetch runs in the background, and the "previews" message fills the rows in while the reader looks at them
    /// (MR-22). A preview still in memory is not fetched again: the mapper already put its card into the page or
    /// the broadcast. A media file already on disk is attached by the mapper.
    /// </summary>
    public void EnsurePreviews(IEnumerable<ChatMessage> messages)
    {
        if (!_linkPreviewSettings.Enabled) return;
        foreach (var message in messages)
        {
            if (message.HasMedia) continue;
            foreach (var url in LinkPreviewService.ExtractUrls(message.Content).Take(LinkPreviewService.MaxLinksPerMessage))
            {
                if (_linkPreviewService.GetCachedPreview(url) is null) _linkPreviewService.QueueFetch(message.Id, url);
                if (_linkPreviewSettings.MediaCachingEnabled) _mediaCacheService.QueueDownload(message.Id, url);   // a no-op for a file on disk
            }
        }
    }

    private void RaisePreviewsReady(Guid messageId)
    {
        if (GetMessage(messageId) is { } message) OnPreviewsReady?.Invoke(message);
    }

    private List<ChatMessage> RepliesTo(ChatMessage original)
    {
        lock (LockOf(original.ChannelId))
            return _channelMessages.TryGetValue(original.ChannelId, out var list) ? list.Where(m => m.ReplyToMessageId == original.Id).ToList() : [];
    }

    /// <summary>
    /// Web push for the people who are not looking (NT-25, NT-26). Away does not suppress it: a phone in a pocket
    /// must get it, and an idle but visible desktop still gets it. Muted conversations get nothing.
    /// </summary>
    private void DispatchPush(Channel channel, string senderUsername, ChatMessage message)
    {
        var preview = !string.IsNullOrWhiteSpace(message.Content) ? message.Content   // NT-27: a caption is the text
            : message.Voice is { } voice ? $"[Voice message {VoiceService.FormatDuration(voice.DurationMs)}]"
            : message.HasGifs ? "[GIF]" : "[Attachment]";
        var senderName = _userService.GetByUsername(senderUsername)?.DisplayName ?? senderUsername;        // NT-27: the display name

        foreach (var recipient in PushRecipients(channel, message.UserId))
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

            // The notification opens the conversation the message is in: for a person that is the admin's, whoever
            // wrote it (the bot writes into it as a system sender).
            var partner = channel.GetOtherParticipant(recipient.Username) ?? senderUsername;
            _ = _pushService.SendDmNotificationAsync(recipient.Username, partner, senderUsername, preview, totalUnread, senderName);
        }
    }

    // The participants other than the sender: the one other person, or both when the bot wrote into their conversation.
    private IEnumerable<User> PushRecipients(Channel channel, Guid senderId)
    {
        foreach (var id in channel.ParticipantIds.Where(id => id != senderId))
            if (_userService.GetById(id) is { } user && !_notifications.IsMuted(user, channel.Id, isDirectMessage: true)) yield return user;
    }

    #endregion

    #region Edit, delete, react

    /// <summary>MR-57, MR-61. Returns the reason for a refusal, or null.</summary>
    public string? Edit(Guid userId, Guid messageId, string text)
    {
        if (GetMessage(messageId) is not { } message) return "Message not found.";
        if (message.UserId != userId) return "You can edit your own messages only.";
        if (message.HasMedia && string.IsNullOrWhiteSpace(message.Content)) return "A media message without a caption cannot be edited.";   // the caption is the part that an edit changes (MR-57)
        if (text.Length > MaxTextLength) return $"A message has at most {MaxTextLength} characters.";
        if (string.IsNullOrWhiteSpace(text) || text == message.Content) return null;   // nothing changes, nothing to say

        message.Content = text;
        message.IsEdited = true;
        OnMessageEdited?.Invoke(message);
        foreach (var reply in RepliesTo(message)) OnMessageEdited?.Invoke(reply);   // their quotes show the new text (MR-32)
        _writer.Enqueue(Writes.MessageEdit(message.Id, text));
        QueuePreviews(message);   // an edit that adds or removes a link adds or removes the card (MR-22)
        if (_channels.TryGetValue(message.ChannelId, out var channel) && channel.Pins.Any(p => p.MessageId == message.Id))
        {
            lock (LockOf(channel.Id)) channel.Pins = channel.Pins.Select(p => p.MessageId == message.Id ? p with { Text = text } : p).ToList();   // the panel shows the new text (SH-48)
            PinsChanged(channel);
        }
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
        if (_channels.TryGetValue(channelId, out var channel) && channel.Pins.Any(p => p.MessageId == messageId))
        {
            lock (LockOf(channelId)) channel.Pins = channel.Pins.Where(p => p.MessageId != messageId).ToList();   // a deleted message leaves the panel (SH-48)
            PinsChanged(channel);
        }
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

    #endregion

    #region Pins

    /// <summary>MR-77, MR-78. Pins a message of the conversation for everyone in it: at most <see cref="Channel.MaxPins"/>, the newest pin first.</summary>
    public string? Pin(Guid userId, string username, Guid channelId, Guid messageId)
    {
        if (!_channels.TryGetValue(channelId, out var channel)) return "Conversation not found.";
        if (GetMessage(messageId) is not { } message || message.ChannelId != channelId || !MayRead(userId, message)) return "Message not found.";
        if (!channel.CanPin(userId)) return "You cannot pin messages here.";
        lock (LockOf(channelId))
        {
            if (channel.Pins.Any(p => p.MessageId == messageId)) return null;   // someone else pinned it a moment ago: nothing to say
            if (channel.Pins.Count >= Channel.MaxPins) return $"Up to {Channel.MaxPins} pinned messages. Unpin one first.";
            channel.Pins = [Models.Chat.Pin.Of(message, username, Thumb(message)), .. channel.Pins];   // qualified: Pin is also this method
        }
        PinsChanged(channel);
        return null;
    }

    /// <summary>MR-77. The pin goes; the message stays. Named by conversation and message, because a pinned message may be
    /// older than the messages in memory.</summary>
    public string? Unpin(Guid userId, string username, Guid channelId, Guid messageId)
    {
        if (!_channels.TryGetValue(channelId, out var channel) || !channel.CanAccess(userId)) return "Conversation not found.";
        if (!channel.CanPin(userId)) return "You cannot unpin messages here.";
        lock (LockOf(channelId))
        {
            if (channel.Pins.All(p => p.MessageId != messageId)) return null;
            channel.Pins = channel.Pins.Where(p => p.MessageId != messageId).ToList();
        }
        PinsChanged(channel);
        return null;
    }

    /// <summary>The pins of a conversation (SH-48). Both people see all of them: a direct message has no history cutoff.
    /// The list is replaced, never edited in place, so the reference is a snapshot.</summary>
    public List<Pin> PinsFor(Guid channelId) => _channels.TryGetValue(channelId, out var channel) ? channel.Pins : [];

    // After a new pin list was assigned: the event, then the row write. The write reads the channel when it runs.
    private void PinsChanged(Channel channel)
    {
        OnPinsChanged?.Invoke(channel);
        _writer.Enqueue(Writes.Channel(channel));
    }

    // The picture the panel shows for a media message: the first image, else the first video's poster (SH-48). The same
    // URLs the row uses (MessageMapper). A GIF has none: its file can move or go, and the panel shows a label instead.
    private static string? Thumb(ChatMessage m) =>
        m.ImageUrls.FirstOrDefault() is { } image ? Media.ImageService.GetMediumUrl(image)
        : m.VideoUrls.FirstOrDefault() is { } video ? Media.VideoService.GetPosterUrl(video)
        : null;

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

    /// <summary>Every image, video and voice URL a message in memory carries. The admin's media clean-up matches uploads against it.</summary>
    public List<string> MediaUrls()
    {
        var urls = new List<string>();
        foreach (var (channelId, messages) in _channelMessages)
            lock (LockOf(channelId))
                foreach (var message in messages)
                {
                    urls.AddRange(message.ImageUrls);
                    urls.AddRange(message.VideoUrls);
                    if (message.Voice is { } voice) urls.Add(voice.Url);
                }
        return urls;
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
            .Where(e => _channels.ContainsKey(e.Key.ChannelId) && !_notifications.IsMuted(user, e.Key.ChannelId, isDirectMessage: true))
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

        var session = callerConnectionId is null ? null : _presence.Get(callerConnectionId);   // AD-27: the unread audit
        _audit.RecordUnreadChange(_userService.GetById(userId)?.Username ?? "?", Describe(channel), "clear", had, source,
            _audit.DescribeCallerSession(session, _presence.EffectiveOf(userId)));
        OnUnreadChanged?.Invoke(userId, channelId, 0);
        _writer.Enqueue(Writes.ReadState(state));
    }

    /// <summary>
    /// NT-02: a message counts for the participants other than the sender: the one other person, or both when the
    /// bot wrote into their conversation. Returns each affected person's new count.
    /// </summary>
    private List<(Guid UserId, int Count)> IncrementUnread(Channel channel, Guid senderId)
    {
        var result = new List<(Guid, int)>();
        foreach (var userId in channel.ParticipantIds.Where(id => id != senderId))
        {
            var state = _readStates.GetOrAdd((userId, channel.Id), _ => new ChannelReadState { UserId = userId, ChannelId = channel.Id, LastReadAt = DateTime.MinValue });
            int count;
            lock (state) count = ++state.UnreadCount;
            result.Add((userId, count));
            _audit.RecordUnreadChange(_userService.GetById(userId)?.Username ?? "?", Describe(channel), "+1", count,
                $"msg from {_userService.GetById(senderId)?.Username}", "—");
        }
        return result;
    }

    private static string Describe(Channel channel) => $"{channel.Participant1} ↔ {channel.Participant2}";

    #endregion

    #region Test data

    /// <summary>AD-08: test messages from the two people of the conversation, spread over a span in bursts. No unread counts, no push.</summary>
    public int GenerateTestMessages(Guid channelId, int count, TimeSpan span)
    {
        if (!_channels.TryGetValue(channelId, out var channel) || count <= 0) return 0;
        var speakers = channel.ParticipantIds.Select(id => _userService.GetById(id)).OfType<User>().Select(u => (u.Id, u.Username)).ToList();
        if (speakers.Count == 0) return 0;

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
                speaker = (speaker + 1) % speakers.Count;
                burst = rng.Next(1, 6);
            }
        }

        bool first;
        lock (LockOf(channelId))
        {
            var messages = _channelMessages.GetOrAdd(channelId, _ => []);
            first = messages.Count == 0;
            messages.AddRange(made);
            messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        foreach (var m in made) _channelOfMessage[m.Id] = channelId;

        OnMessageReceived?.Invoke(made[^1]);   // only the last one is announced: the rows are history, seen whole at the next open
        if (first) OnDmStarted?.Invoke(channel);
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

    #endregion

    private Lock LockOf(Guid channelId) => _channelLocks.GetOrAdd(channelId, _ => new Lock());
}
