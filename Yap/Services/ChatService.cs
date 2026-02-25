using System.Collections.Concurrent;
using System.Diagnostics;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Singleton service that holds all chat state and broadcasts changes via events.
/// Components subscribe to events and call StateHasChanged() to update their UI.
/// State is kept in-memory for fast access. When persistence is enabled, changes are
/// written through to the database and loaded on startup.
/// </summary>
public class ChatService
{
    private readonly ConcurrentDictionary<string, UserSession> _users = new();
    private readonly PushNotificationService _pushService;
    private readonly ChatPersistenceService _persistence;
    private readonly UserService _userService;
    private readonly ILogger<ChatService> _logger;

    // Channels (rooms and DMs)
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _channelMessages = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _channelTypingUsers = new();
    private readonly ConcurrentDictionary<Guid, object> _channelLocks = new();

    // Unread tracking: (UserId, ChannelId) -> ChannelReadState
    private readonly ConcurrentDictionary<(Guid UserId, Guid ChannelId), ChannelReadState> _readStates = new();

    // Default lobby channel ID (updated in InitializeAsync if loaded from DB)
    private Guid _lobbyId;

    // Events for real-time updates (unified for all channel types)
    // All events are synchronous (Action) - handlers use fire-and-forget internally
    public event Action<ChatMessage>? OnMessageReceived;
    public event Action<ChatMessage>? OnMessageUpdated;
    public event Action<Guid, Guid>? OnMessageDeleted; // messageId, channelId
    public event Action<ChatMessage>? OnReactionChanged;
    public event Action<string, bool>? OnUserChanged;
    public event Action? OnUsersListChanged;
    public event Action<Guid>? OnTypingUsersChanged; // channelId

    // Channel events
    public event Action<Channel>? OnChannelCreated;
    public event Action<Channel>? OnChannelUpdated;
    public event Action<Guid>? OnChannelDeleted;

    // User status events
    public event Action<string, UserStatus>? OnUserStatusChanged; // username, newStatus

    // Unread state events
    public event Action<Guid, Guid>? OnUnreadChanged; // userId, channelId

    public record UserSession(Guid UserId, string Username, string SessionId, UserStatus Status = UserStatus.Online, bool? IsMobile = null);

    public ChatService(PushNotificationService pushService, ChatPersistenceService persistence, UserService userService, ILogger<ChatService> logger)
    {
        _pushService = pushService;
        _persistence = persistence;
        _userService = userService;
        _logger = logger;

        // Create default lobby channel (will be replaced if loading from DB)
        var lobby = Channel.CreateRoom("lobby", createdById: null, createdBy: null, isDefault: true);
        _lobbyId = lobby.Id;
        _channels[lobby.Id] = lobby;
        _channelMessages[lobby.Id] = new List<ChatMessage>();
        _channelTypingUsers[lobby.Id] = new ConcurrentDictionary<string, DateTime>();
    }


    #region Diagnostics

    /// <summary>
    /// Gets diagnostic information about event subscribers and system state.
    /// </summary>
    public ChatDiagnostics GetDiagnostics()
    {
        return new ChatDiagnostics
        {
            UserSessions = _users.Count,
            UniqueUsers = _users.Values.Select(u => u.Username).Distinct().Count(),
            Channels = _channels.Count,
            RoomChannels = _channels.Values.Count(c => c.Type == ChannelType.Room),
            DmChannels = _channels.Values.Count(c => c.Type == ChannelType.DirectMessage),
            TotalMessages = _channelMessages.Values.Sum(m => m.Count),
            EventSubscribers = new Dictionary<string, int>
            {
                ["OnMessageReceived"] = OnMessageReceived?.GetInvocationList().Length ?? 0,
                ["OnMessageUpdated"] = OnMessageUpdated?.GetInvocationList().Length ?? 0,
                ["OnMessageDeleted"] = OnMessageDeleted?.GetInvocationList().Length ?? 0,
                ["OnReactionChanged"] = OnReactionChanged?.GetInvocationList().Length ?? 0,
                ["OnUserChanged"] = OnUserChanged?.GetInvocationList().Length ?? 0,
                ["OnUsersListChanged"] = OnUsersListChanged?.GetInvocationList().Length ?? 0,
                ["OnTypingUsersChanged"] = OnTypingUsersChanged?.GetInvocationList().Length ?? 0,
                ["OnChannelCreated"] = OnChannelCreated?.GetInvocationList().Length ?? 0,
                ["OnChannelUpdated"] = OnChannelUpdated?.GetInvocationList().Length ?? 0,
                ["OnChannelDeleted"] = OnChannelDeleted?.GetInvocationList().Length ?? 0,
                ["OnUserStatusChanged"] = OnUserStatusChanged?.GetInvocationList().Length ?? 0,
                ["OnUnreadChanged"] = OnUnreadChanged?.GetInvocationList().Length ?? 0
            },
            AdminUser = _userService.GetAdminUsername()
        };
    }

    #endregion

    /// <summary>
    /// Initializes chat data from the database if persistence is enabled.
    /// Called on application startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        var snapshot = await _persistence.LoadSnapshotAsync();
        if (snapshot == null)
            return;

        // Clear default lobby (will be replaced from DB or recreated)
        _channels.Clear();
        _channelMessages.Clear();
        _channelTypingUsers.Clear();

        // Load channels from database
        foreach (var channel in snapshot.Channels)
        {
            _channels[channel.Id] = channel;
            _channelMessages[channel.Id] = snapshot.MessagesByChannel.GetValueOrDefault(channel.Id, new List<ChatMessage>());
            _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();
        }

        // Check for orphaned DM channels (participants that no longer exist as users)
        var orphanedDMs = _channels.Values
            .Where(c => c.Type == ChannelType.DirectMessage &&
                       (_userService.GetByUsername(c.Participant1 ?? "") == null ||
                        _userService.GetByUsername(c.Participant2 ?? "") == null))
            .ToList();

        if (orphanedDMs.Count > 0)
        {
            _logger.LogWarning("Found {Count} orphaned DM channel(s) with non-existent participants:", orphanedDMs.Count);
            foreach (var dm in orphanedDMs)
            {
                var p1Exists = _userService.GetByUsername(dm.Participant1 ?? "") != null;
                var p2Exists = _userService.GetByUsername(dm.Participant2 ?? "") != null;
                _logger.LogWarning("  - Channel {Id}: Participant1='{P1}' ({P1Status}), Participant2='{P2}' ({P2Status})",
                    dm.Id,
                    dm.Participant1 ?? "(null)", p1Exists ? "exists" : "MISSING",
                    dm.Participant2 ?? "(null)", p2Exists ? "exists" : "MISSING");
            }
        }

        // Load read states
        foreach (var readState in snapshot.ReadStates)
        {
            _readStates[(readState.UserId, readState.ChannelId)] = readState;
        }

        // Ensure lobby exists and update _lobbyId
        var existingLobby = _channels.Values.FirstOrDefault(c => c.Type == ChannelType.Room && c.IsDefault);
        if (existingLobby != null)
        {
            // Use lobby from database
            _lobbyId = existingLobby.Id;
        }
        else
        {
            // Create lobby if it doesn't exist in DB
            var lobby = Channel.CreateRoom("lobby", createdById: null, createdBy: null, isDefault: true);
            _lobbyId = lobby.Id;
            _channels[lobby.Id] = lobby;
            _channelMessages[lobby.Id] = new List<ChatMessage>();
            _channelTypingUsers[lobby.Id] = new ConcurrentDictionary<string, DateTime>();
            await _persistence.PersistChannelAsync(lobby);
        }
    }

    /// <summary>
    /// Gets the lobby channel ID.
    /// </summary>
    public Guid GetLobbyId() => _lobbyId;

    #region Admin

    public string? GetAdmin() => _userService.GetAdminUsername();

    public bool IsAdmin(string username) => _userService.IsAdmin(username);

    public bool IsAdmin(Guid userId) => _userService.IsAdmin(userId);

    #endregion

    #region Channel Management

    /// <summary>
    /// Gets or creates a lock object for a specific channel.
    /// This allows concurrent operations on different channels.
    /// </summary>
    private object GetChannelLock(Guid channelId) =>
        _channelLocks.GetOrAdd(channelId, _ => new object());

    public List<Channel> GetRooms() =>
        _channels.Values
            .Where(c => c.Type == ChannelType.Room)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.CreatedAt)
            .ToList();

    public Channel? GetChannel(Guid channelId) =>
        _channels.TryGetValue(channelId, out var channel) ? channel : null;

    public async Task<Channel?> CreateRoomAsync(Guid adminUserId, string adminUsername, string roomName,
        string? description = null, ChannelPermission writePermission = ChannelPermission.Everyone)
    {
        if (!IsAdmin(adminUserId))
            return null;

        // Normalize room name
        roomName = roomName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(roomName))
            return null;

        // Check if room already exists
        if (_channels.Values.Any(c => c.Type == ChannelType.Room &&
            c.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase)))
            return null;

        // Auto-assign sort order: new rooms go to the bottom
        var maxSortOrder = _channels.Values
            .Where(c => c.Type == ChannelType.Room)
            .Select(c => c.SortOrder)
            .DefaultIfEmpty(-1)
            .Max();

        var channel = Channel.CreateRoom(roomName, adminUserId, adminUsername,
            description: description, sortOrder: maxSortOrder + 1, writePermission: writePermission);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = new List<ChatMessage>();
        _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();

        // Persist to database
        var sw = Stopwatch.StartNew();
        await _persistence.PersistChannelAsync(channel);

        _logger.LogDebug("CreateRoom '{RoomName}' by {User}: persist={ElapsedMs}ms", roomName, adminUsername, sw.ElapsedMilliseconds);

        OnChannelCreated?.Invoke(channel);

        return channel;
    }

    public async Task<bool> DeleteRoomAsync(Guid adminUserId, Guid channelId)
    {
        if (!IsAdmin(adminUserId))
            return false;

        if (!_channels.TryGetValue(channelId, out var channel))
            return false;

        // Cannot delete default lobby or DM channels
        if (channel.IsDefault || channel.IsDirectMessage)
            return false;

        _channels.TryRemove(channelId, out _);
        _channelMessages.TryRemove(channelId, out _);
        _channelTypingUsers.TryRemove(channelId, out _);
        _channelLocks.TryRemove(channelId, out _);

        // Delete from database
        var sw = Stopwatch.StartNew();
        await _persistence.DeleteChannelAsync(channelId);

        _logger.LogDebug("DeleteRoom '{RoomName}' channel={ChannelId}: persist={ElapsedMs}ms", channel.Name, channelId, sw.ElapsedMilliseconds);

        OnChannelDeleted?.Invoke(channelId);

        return true;
    }

    /// <summary>
    /// Updates a channel's name, description and write permission. Admin only.
    /// Returns null on success, or an error message string on failure.
    /// </summary>
    public async Task<string?> UpdateChannelAsync(Guid adminUserId, Guid channelId, string name, string? description, ChannelPermission writePermission)
    {
        if (!IsAdmin(adminUserId))
            return "Not authorized.";

        if (!_channels.TryGetValue(channelId, out var channel))
            return "Channel not found.";

        if (channel.IsDirectMessage)
            return "Cannot edit DM channels.";

        // Normalize name
        name = name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
            return "Channel name is required.";

        // Check for duplicate name (excluding self)
        if (_channels.Values.Any(c => c.Type == ChannelType.Room &&
            c.Id != channelId &&
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "A channel with that name already exists.";

        channel.Name = name;
        channel.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        channel.WritePermission = writePermission;

        var sw = Stopwatch.StartNew();
        await _persistence.PersistChannelAsync(channel);

        _logger.LogDebug("UpdateChannel '{ChannelName}' channel={ChannelId}: persist={ElapsedMs}ms",
            channel.Name, channelId, sw.ElapsedMilliseconds);

        OnChannelUpdated?.Invoke(channel);
        return null;
    }

    /// <summary>
    /// Moves a channel up or down in the sort order by swapping with the adjacent room.
    /// </summary>
    public async Task<bool> ReorderChannelAsync(Guid adminUserId, Guid channelId, bool moveUp)
    {
        if (!IsAdmin(adminUserId))
            return false;

        var rooms = GetRooms();
        var index = rooms.FindIndex(r => r.Id == channelId);
        if (index < 0)
            return false;

        var swapIndex = moveUp ? index - 1 : index + 1;
        if (swapIndex < 0 || swapIndex >= rooms.Count)
            return false;

        // Swap sort orders
        var currentChannel = rooms[index];
        var adjacentChannel = rooms[swapIndex];
        (currentChannel.SortOrder, adjacentChannel.SortOrder) = (adjacentChannel.SortOrder, currentChannel.SortOrder);

        var sw = Stopwatch.StartNew();
        await _persistence.PersistChannelAsync(currentChannel);
        await _persistence.PersistChannelAsync(adjacentChannel);

        _logger.LogDebug("ReorderChannel '{ChannelName}' {Direction}: persist={ElapsedMs}ms",
            currentChannel.Name, moveUp ? "up" : "down", sw.ElapsedMilliseconds);

        OnChannelUpdated?.Invoke(currentChannel);
        return true;
    }

    /// <summary>
    /// Checks if a user can write messages in a channel.
    /// </summary>
    public bool CanUserWrite(Guid channelId, Guid userId)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
            return false;

        return channel.CanWrite(userId, IsAdmin(userId));
    }

    /// <summary>
    /// Gets or creates a DM channel between two users (by UserId)
    /// </summary>
    public Channel GetOrCreateDMChannel(Guid userId1, string username1, Guid userId2, string username2)
    {
        // Check if DM channel already exists
        var existing = _channels.Values.FirstOrDefault(c => c.IsDMBetween(userId1, userId2));
        if (existing != null)
            return existing;

        // Create new DM channel
        var channel = Channel.CreateDM(userId1, username1, userId2, username2);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = new List<ChatMessage>();
        _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();

        // Persist to database (fire and forget)
        _ = _persistence.PersistChannelAsync(channel);

        return channel;
    }

    /// <summary>
    /// Gets or creates a DM channel between two users (legacy - by username)
    /// </summary>
    public Channel? GetOrCreateDMChannelByUsername(string username1, string username2)
    {
        var user1 = _userService.GetByUsername(username1);
        var user2 = _userService.GetByUsername(username2);

        if (user1 == null || user2 == null)
            return null;

        return GetOrCreateDMChannel(user1.Id, user1.Username, user2.Id, user2.Username);
    }

    /// <summary>
    /// Gets all DM channels for a user
    /// </summary>
    public List<Channel> GetDMChannels(string username) =>
        _channels.Values
            .Where(c => c.IsDirectMessage && c.CanAccess(username))
            .ToList();

    /// <summary>
    /// Gets all users that have DM history with the specified user
    /// </summary>
    public List<string> GetDMConversations(string username) =>
        GetDMChannels(username)
            .Select(c => c.GetOtherParticipant(username)!)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

    #endregion

    #region User Management

    public Task AddUserAsync(string sessionId, Guid userId, string username, UserStatus status = UserStatus.Online, bool? isMobile = null)
    {
        // Remove any existing sessions for this username (stale circuits from page refresh, etc.)
        var staleSessionIds = _users
            .Where(kvp => kvp.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var staleId in staleSessionIds)
        {
            _users.TryRemove(staleId, out _);
        }

        _users[sessionId] = new UserSession(userId, username, sessionId, status, isMobile);

        _logger.LogDebug("AddUser {User} session={SessionId} status={Status} staleRemoved={StaleCount} totalSessions={TotalSessions}",
            username, sessionId, status, staleSessionIds.Count, _users.Count);

        OnUserChanged?.Invoke(username, true);
        OnUsersListChanged?.Invoke();

        return Task.CompletedTask;
    }

    public Task SetUserStatusAsync(string sessionId, UserStatus status)
    {
        if (!_users.TryGetValue(sessionId, out var session))
            return Task.CompletedTask;

        var oldStatus = session.Status;
        // Update with new status
        _users[sessionId] = session with { Status = status };

        _logger.LogDebug("SetUserStatus {User}: {OldStatus} -> {NewStatus}", session.Username, oldStatus, status);

        OnUserStatusChanged?.Invoke(session.Username, status);
        OnUsersListChanged?.Invoke();

        return Task.CompletedTask;
    }

    public UserStatus? GetUserStatus(string username)
    {
        var session = _users.Values.FirstOrDefault(u =>
            u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        return session?.Status;
    }

    public Task RemoveUserAsync(string circuitId)
    {
        if (_users.TryRemove(circuitId, out var session))
        {
            // Remove from all typing indicators
            foreach (var typingUsers in _channelTypingUsers.Values)
            {
                typingUsers.TryRemove(session.Username, out _);
            }

            _logger.LogDebug("RemoveUser {User} circuit={CircuitId} remainingSessions={TotalSessions}",
                session.Username, circuitId, _users.Count);

            // Note: DM channels now persist permanently (like Discord)
            // They are NOT deleted when user disconnects

            OnUserChanged?.Invoke(session.Username, false);
            OnUsersListChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all connected users (including invisible). For internal use.
    /// </summary>
    public List<string> GetOnlineUsers() =>
        _users.Values.Select(u => u.Username).Distinct().ToList();

    /// <summary>
    /// Gets all connected users with their status for UI display.
    /// Invisible users appear with gray dot (like "appears offline").
    /// </summary>
    public List<(string Username, UserStatus Status, bool? IsMobile)> GetAllUsersWithStatus() =>
        _users.Values
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().Status, g.First().IsMobile))
            .ToList();

    public bool IsUsernameTaken(string username) =>
        _users.Values.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks if a session ID exists in the active users list.
    /// </summary>
    public bool HasSession(string sessionId) =>
        _users.ContainsKey(sessionId);

    #endregion

    #region Messaging

    public async Task SendMessageAsync(Guid channelId, Guid userId, string username, string content, List<string>? imageUrls = null, Guid? replyToMessageId = null)
    {
        var totalSw = Stopwatch.StartNew();
        if (!_channels.TryGetValue(channelId, out var channel))
            return;

        // Check write permission
        if (!channel.CanWrite(userId, IsAdmin(userId)))
            return;

        var message = new ChatMessage(channelId, userId, username, content, DateTime.UtcNow, imageUrls, replyToMessageId);

        lock (GetChannelLock(channelId))
        {
            if (!_channelMessages.TryGetValue(channelId, out var messages))
                return;

            messages.Add(message);
        }

        // Persist message (no SELECT needed - always new)
        var persistSw = Stopwatch.StartNew();
        await _persistence.PersistNewMessageAsync(message);
        var persistMs = persistSw.ElapsedMilliseconds;

        // Update unread counts in memory + DB (awaited — fast, no events)
        var unreadSw = Stopwatch.StartNew();
        var affectedUserIds = await IncrementUnreadCountsAsync(channelId, userId);
        var unreadMs = unreadSw.ElapsedMilliseconds;

        // Clear typing state in memory (fast, no event dispatch)
        var wasTyping = _channelTypingUsers.TryGetValue(channelId, out var typingUsers) && typingUsers.TryRemove(username, out _);

        _logger.LogDebug("SendMessage by {User} to channel {ChannelId}: persist={PersistMs}ms unread={UnreadMs}ms ({AffectedUsers} users) callerTotal={TotalMs}ms images={HasImages}",
            username, channelId, persistMs, unreadMs, affectedUserIds.Count, totalSw.ElapsedMilliseconds, imageUrls?.Count > 0);

        // Notify all subscribers
        if (wasTyping)
            OnTypingUsersChanged?.Invoke(channelId);

        OnMessageReceived?.Invoke(message);
        NotifyUnreadChanged(channelId, affectedUserIds);

        // Send push notification for DMs (fire-and-forget, doesn't block)
        // Skip if recipient has a live circuit (Online/Away) — they already see messages in real-time
        if (channel.IsDirectMessage)
        {
            var recipient = channel.GetOtherParticipant(username);
            if (recipient != null)
            {
                var recipientStatus = GetUserStatus(recipient);
                if (recipientStatus is UserStatus.Online or UserStatus.Away)
                {
                    _logger.LogDebug("Push DM skipped: {Recipient} is {Status}, has live circuit",
                        recipient, recipientStatus);
                }
                else
                {
                    var recipientUser = _userService.GetByUsername(recipient);
                    var totalUnread = recipientUser != null
                        ? GetTotalUnreadDMCount(recipientUser.Id)
                        : 1;
                    var preview = imageUrls?.Count > 0 ? "[Image]" : content;

                    _logger.LogDebug("Push DM: from={From} to={To} totalUnread={UnreadCount} status={Status}",
                        username, recipient, totalUnread, recipientStatus);

                    _ = _pushService.SendDmNotificationAsync(recipient, username, preview, totalUnread);
                }
            }
        }
    }

    public List<ChatMessage> GetMessages(Guid channelId, int count = 50)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return new List<ChatMessage>();

        lock (GetChannelLock(channelId))
        {
            return messages.TakeLast(Math.Min(count, messages.Count)).ToList();
        }
    }

    /// <summary>
    /// Gets messages with pagination support for infinite scroll.
    /// Returns messages in chronological order (oldest first).
    /// </summary>
    /// <param name="channelId">The channel ID</param>
    /// <param name="count">Number of messages to return</param>
    /// <param name="beforeTimestamp">Return messages older than this timestamp. Null = most recent.</param>
    /// <returns>Messages and whether there are more older messages available</returns>
    public (List<ChatMessage> Messages, bool HasMore) GetMessagesPaginated(
        Guid channelId,
        int count = 20,
        DateTime? beforeTimestamp = null)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return (new List<ChatMessage>(), false);

        lock (GetChannelLock(channelId))
        {
            IEnumerable<ChatMessage> filtered = messages;

            if (beforeTimestamp.HasValue)
            {
                filtered = messages.Where(m => m.Timestamp < beforeTimestamp.Value);
            }

            var result = filtered
                .OrderByDescending(m => m.Timestamp)
                .Take(count)
                .OrderBy(m => m.Timestamp)
                .ToList();

            // Check if there are older messages
            var oldestReturned = result.FirstOrDefault()?.Timestamp;
            var hasMore = oldestReturned.HasValue &&
                          messages.Any(m => m.Timestamp < oldestReturned.Value);

            return (result, hasMore);
        }
    }

    public ChatMessage? GetMessageById(Guid channelId, Guid messageId)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages)) return null;
        lock (GetChannelLock(channelId))
        {
            return messages.FirstOrDefault(m => m.Id == messageId);
        }
    }

    public async Task<bool> EditMessageAsync(Guid messageId, Guid channelId, string username, string newContent)
    {
        var sw = Stopwatch.StartNew();
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return false;

        ChatMessage? message;
        lock (GetChannelLock(channelId))
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
        }

        if (message == null || message.Username != username)
            return false;

        if (message.HasImages)
            return false; // Can't edit image messages

        message.Content = newContent;
        message.IsEdited = true;

        // Persist the edit (single UPDATE, no SELECT)
        await _persistence.PersistMessageEditAsync(messageId, newContent);

        _logger.LogDebug("EditMessage {MessageId} by {User}: persist={ElapsedMs}ms", messageId, username, sw.ElapsedMilliseconds);

        OnMessageUpdated?.Invoke(message);

        return true;
    }

    public async Task<bool> DeleteMessageAsync(Guid messageId, Guid channelId, string username)
    {
        var sw = Stopwatch.StartNew();
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return false;

        ChatMessage? message;
        lock (GetChannelLock(channelId))
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
            if (message == null || message.Username != username)
                return false;

            messages.Remove(message);
        }

        // Delete from database
        await _persistence.DeleteMessageAsync(messageId);

        _logger.LogDebug("DeleteMessage {MessageId} by {User}: persist={ElapsedMs}ms", messageId, username, sw.ElapsedMilliseconds);

        OnMessageDeleted?.Invoke(messageId, channelId);

        return true;
    }

    public async Task ToggleReactionAsync(Guid messageId, Guid channelId, Guid userId, string username, string emoji)
    {
        var sw = Stopwatch.StartNew();
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return;

        ChatMessage? message;
        lock (GetChannelLock(channelId))
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
        }

        if (message == null)
            return;

        bool added;
        lock (message.Reactions)
        {
            var existingReaction = message.Reactions.FirstOrDefault(r =>
                r.Emoji == emoji && r.UserId == userId);

            if (existingReaction != null)
            {
                message.Reactions.Remove(existingReaction);
                added = false;
            }
            else
            {
                message.Reactions.Add(new Reaction
                {
                    MessageId = messageId,
                    UserId = userId,
                    Username = username,
                    Emoji = emoji
                });
                added = true;
            }
        }

        // Persist reaction change
        if (added)
            await _persistence.AddReactionAsync(messageId, userId, username, emoji);
        else
            await _persistence.RemoveReactionAsync(messageId, userId, emoji);

        _logger.LogDebug("ToggleReaction {Emoji} on {MessageId} by {User}: action={Action} persist={ElapsedMs}ms",
            emoji, messageId, username, added ? "add" : "remove", sw.ElapsedMilliseconds);

        OnReactionChanged?.Invoke(message);
    }

    #endregion

    #region Unread Tracking

    /// <summary>
    /// Gets unread count for a user in a specific channel.
    /// </summary>
    public int GetUnreadCount(Guid userId, Guid channelId)
    {
        if (_readStates.TryGetValue((userId, channelId), out var state))
        {
            return state.UnreadCount;
        }
        return 0;
    }

    /// <summary>
    /// Marks a channel as read for a user (resets unread count to 0).
    /// Use silent: true when called from event handlers to avoid nested event cascades.
    /// </summary>
    public async Task MarkChannelAsReadAsync(Guid userId, Guid channelId, bool silent = false)
    {
        var sw = Stopwatch.StartNew();
        var key = (userId, channelId);
        var now = DateTime.UtcNow;
        var hadUnread = false;
        var previousCount = 0;

        if (_readStates.TryGetValue(key, out var state))
        {
            hadUnread = state.UnreadCount > 0;
            previousCount = state.UnreadCount;
            state.LastReadAt = now;
            state.UnreadCount = 0;
        }
        else
        {
            state = new ChannelReadState
            {
                UserId = userId,
                ChannelId = channelId,
                LastReadAt = now,
                UnreadCount = 0
            };
            _readStates[key] = state;
        }

        await _persistence.PersistReadStateAsync(state);

        _logger.LogDebug("MarkChannelAsRead user={UserId} channel={ChannelId}: cleared={ClearedCount} silent={Silent} persist={ElapsedMs}ms",
            userId, channelId, previousCount, silent, sw.ElapsedMilliseconds);

        // Notify if there were unread messages that are now cleared
        if (hadUnread && !silent)
        {
            OnUnreadChanged?.Invoke(userId, channelId);
        }
    }

    /// <summary>
    /// Increments unread count for all participants except the sender (memory + DB only).
    /// Returns the list of affected user IDs for notification.
    /// </summary>
    private async Task<List<Guid>> IncrementUnreadCountsAsync(Guid channelId, Guid senderUserId)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
            return new List<Guid>();

        // Collect user IDs to update
        var userIdsToIncrement = new List<Guid>();

        if (channel.IsDirectMessage)
        {
            var otherUserId = channel.GetOtherParticipantId(senderUserId);
            if (otherUserId.HasValue)
                userIdsToIncrement.Add(otherUserId.Value);
        }
        else
        {
            userIdsToIncrement = _users.Values
                .Where(s => s.UserId != senderUserId)
                .Select(s => s.UserId)
                .ToList();
        }

        if (userIdsToIncrement.Count == 0) return userIdsToIncrement;

        // Update in-memory state (fast)
        foreach (var userId in userIdsToIncrement)
        {
            var key = (userId, channelId);
            if (_readStates.TryGetValue(key, out var state))
            {
                state.UnreadCount++;
            }
            else
            {
                _readStates[key] = new ChannelReadState
                {
                    UserId = userId,
                    ChannelId = channelId,
                    LastReadAt = DateTime.MinValue,
                    UnreadCount = 1
                };
            }
        }

        // Single DB call for all users
        var sw = Stopwatch.StartNew();
        await _persistence.IncrementUnreadForUsersAsync(channelId, userIdsToIncrement);

        _logger.LogDebug("IncrementUnreadCounts channel={ChannelId}: {UserCount} users, persist={ElapsedMs}ms",
            channelId, userIdsToIncrement.Count, sw.ElapsedMilliseconds);

        return userIdsToIncrement;
    }

    /// <summary>
    /// Fires OnUnreadChanged for all affected users.
    /// </summary>
    private void NotifyUnreadChanged(Guid channelId, List<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            OnUnreadChanged?.Invoke(userId, channelId);
        }
    }

    /// <summary>
    /// Gets total unread DM count for a user across all DM channels.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="excludeChannelId">Optional channel ID to exclude (e.g., currently viewed channel)</param>
    public int GetTotalUnreadDMCount(Guid userId, Guid? excludeChannelId = null)
    {
        return _readStates
            .Where(kvp => kvp.Key.UserId == userId && kvp.Value.UnreadCount > 0)
            .Where(kvp => excludeChannelId == null || kvp.Key.ChannelId != excludeChannelId)
            .Where(kvp => _channels.TryGetValue(kvp.Key.ChannelId, out var ch) && ch.IsDirectMessage)
            .Sum(kvp => kvp.Value.UnreadCount);
    }

    /// <summary>
    /// Checks if a room has unread messages for a user.
    /// </summary>
    public bool HasUnreadInRoom(Guid userId, Guid channelId)
    {
        return GetUnreadCount(userId, channelId) > 0;
    }

    /// <summary>
    /// Clears all read states for a user (used when user is deleted).
    /// </summary>
    public void ClearReadStatesForUser(Guid userId)
    {
        var keysToRemove = _readStates.Keys.Where(k => k.UserId == userId).ToList();
        foreach (var key in keysToRemove)
        {
            _readStates.TryRemove(key, out _);
        }
    }

    #endregion

    #region DM-specific helpers

    /// <summary>
    /// Gets the timestamp of the last message in a DM channel
    /// </summary>
    public DateTime? GetLastDMTimestamp(string user1, string user2)
    {
        var channel = _channels.Values.FirstOrDefault(c => c.IsDMBetween(user1, user2));
        if (channel == null || !_channelMessages.TryGetValue(channel.Id, out var messages) || messages.Count == 0)
            return null;

        lock (GetChannelLock(channel.Id))
        {
            return messages.LastOrDefault()?.Timestamp;
        }
    }

    #endregion

    #region Typing Indicators

    public Task StartTypingAsync(Guid channelId, string username)
    {
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
        {
            typingUsers[username] = DateTime.UtcNow;
            _logger.LogDebug("StartTyping {User} in channel {ChannelId}", username, channelId);
            OnTypingUsersChanged?.Invoke(channelId);
        }
        return Task.CompletedTask;
    }

    public Task StopTypingAsync(Guid channelId, string username)
    {
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
        {
            typingUsers.TryRemove(username, out _);
            _logger.LogDebug("StopTyping {User} in channel {ChannelId}", username, channelId);
            OnTypingUsersChanged?.Invoke(channelId);
        }
        return Task.CompletedTask;
    }

    public List<string> GetTypingUsers(Guid channelId)
    {
        if (!_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
            return new List<string>();

        // Take a snapshot first (ToArray on ConcurrentDictionary is more atomic than enumeration)
        var snapshot = typingUsers.ToArray();
        var now = DateTime.UtcNow;

        // Separate active from stale based on the snapshot
        var active = new List<string>();
        var stale = new List<string>();

        foreach (var kvp in snapshot)
        {
            if ((now - kvp.Value).TotalSeconds > 3)
                stale.Add(kvp.Key);
            else
                active.Add(kvp.Key);
        }

        // Clean up stale entries
        foreach (var user in stale)
            typingUsers.TryRemove(user, out _);

        return active;
    }

    #endregion
}
