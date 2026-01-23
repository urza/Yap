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
    public event Func<ChatMessage, Task>? OnMessageReceived;
    public event Func<ChatMessage, Task>? OnMessageUpdated;
    public event Func<Guid, Guid, Task>? OnMessageDeleted; // messageId, channelId
    public event Func<ChatMessage, Task>? OnReactionChanged;
    public event Func<string, bool, Task>? OnUserChanged;
    public event Func<Task>? OnUsersListChanged;
    public event Func<Guid, Task>? OnTypingUsersChanged; // channelId

    // Channel events
    public event Func<Channel, Task>? OnChannelCreated;
    public event Func<Guid, Task>? OnChannelDeleted;

    // Admin events
    public event Func<string?, Task>? OnAdminChanged;

    // User status events
    public event Func<string, UserStatus, Task>? OnUserStatusChanged; // username, newStatus

    // Unread state events
    public event Func<Guid, Guid, Task>? OnUnreadChanged; // userId, channelId

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

    #region Parallel Event Invocation

    /// <summary>
    /// Invokes all event handlers in parallel instead of sequentially.
    /// This dramatically improves performance when there are multiple subscribers (circuits).
    /// </summary>
    private async Task InvokeParallelAsync<T>(Func<T, Task>? eventDelegate, T arg, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (eventDelegate == null) return;

        var handlers = eventDelegate.GetInvocationList().Cast<Func<T, Task>>().ToList();
        if (handlers.Count == 0) return;

        var sw = Stopwatch.StartNew();

        var tasks = handlers.Select(async handler =>
        {
            try
            {
                await handler(arg);
            }
            catch (Exception ex)
            {
                // Log but don't rethrow - one handler failing shouldn't break others
                _logger.LogWarning(ex, "Event handler failed in {Caller}", caller);
            }
        });

        await Task.WhenAll(tasks);

        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
        {
            _logger.LogWarning("Slow event dispatch: {Caller} took {ElapsedMs}ms for {HandlerCount} handlers",
                caller, sw.ElapsedMilliseconds, handlers.Count);
        }
    }

    /// <summary>
    /// Invokes all event handlers (no arguments) in parallel.
    /// </summary>
    private async Task InvokeParallelAsync(Func<Task>? eventDelegate, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (eventDelegate == null) return;

        var handlers = eventDelegate.GetInvocationList().Cast<Func<Task>>().ToList();
        if (handlers.Count == 0) return;

        var sw = Stopwatch.StartNew();

        var tasks = handlers.Select(async handler =>
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event handler failed in {Caller}", caller);
            }
        });

        await Task.WhenAll(tasks);

        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
        {
            _logger.LogWarning("Slow event dispatch: {Caller} took {ElapsedMs}ms for {HandlerCount} handlers",
                caller, sw.ElapsedMilliseconds, handlers.Count);
        }
    }

    /// <summary>
    /// Invokes all event handlers (two arguments) in parallel.
    /// </summary>
    private async Task InvokeParallelAsync<T1, T2>(Func<T1, T2, Task>? eventDelegate, T1 arg1, T2 arg2, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (eventDelegate == null) return;

        var handlers = eventDelegate.GetInvocationList().Cast<Func<T1, T2, Task>>().ToList();
        if (handlers.Count == 0) return;

        var sw = Stopwatch.StartNew();

        var tasks = handlers.Select(async handler =>
        {
            try
            {
                await handler(arg1, arg2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event handler failed in {Caller}", caller);
            }
        });

        await Task.WhenAll(tasks);

        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
        {
            _logger.LogWarning("Slow event dispatch: {Caller} took {ElapsedMs}ms for {HandlerCount} handlers",
                caller, sw.ElapsedMilliseconds, handlers.Count);
        }
    }

    #endregion

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
                ["OnChannelDeleted"] = OnChannelDeleted?.GetInvocationList().Length ?? 0,
                ["OnAdminChanged"] = OnAdminChanged?.GetInvocationList().Length ?? 0,
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
            .OrderBy(c => c.IsDefault ? 0 : 1)
            .ThenBy(c => c.CreatedAt)
            .ToList();

    public Channel? GetChannel(Guid channelId) =>
        _channels.TryGetValue(channelId, out var channel) ? channel : null;

    public async Task<Channel?> CreateRoomAsync(Guid adminUserId, string adminUsername, string roomName)
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

        var channel = Channel.CreateRoom(roomName, adminUserId, adminUsername);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = new List<ChatMessage>();
        _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();

        // Persist to database
        await _persistence.PersistChannelAsync(channel);

        await InvokeParallelAsync(OnChannelCreated, channel);

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
        await _persistence.DeleteChannelAsync(channelId);

        await InvokeParallelAsync(OnChannelDeleted, channelId);

        return true;
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
            .Where(u => u != null)
            .ToList();

    #endregion

    #region User Management

    public async Task AddUserAsync(string sessionId, Guid userId, string username, UserStatus status = UserStatus.Online, bool? isMobile = null)
    {
        _users[sessionId] = new UserSession(userId, username, sessionId, status, isMobile);

        await InvokeParallelAsync(OnUserChanged, username, true);
        await InvokeParallelAsync(OnUsersListChanged);
    }

    public async Task SetUserStatusAsync(string sessionId, UserStatus status)
    {
        if (!_users.TryGetValue(sessionId, out var session))
            return;

        // Update with new status
        _users[sessionId] = session with { Status = status };

        await InvokeParallelAsync(OnUserStatusChanged, session.Username, status);
        await InvokeParallelAsync(OnUsersListChanged);
    }

    public UserStatus? GetUserStatus(string username)
    {
        var session = _users.Values.FirstOrDefault(u =>
            u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        return session?.Status;
    }

    public async Task RemoveUserAsync(string circuitId)
    {
        if (_users.TryRemove(circuitId, out var session))
        {
            // Remove from all typing indicators
            foreach (var typingUsers in _channelTypingUsers.Values)
            {
                typingUsers.TryRemove(session.Username, out _);
            }

            // Note: DM channels now persist permanently (like Discord)
            // They are NOT deleted when user disconnects

            await InvokeParallelAsync(OnUserChanged, session.Username, false);
            await InvokeParallelAsync(OnUsersListChanged);
        }
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

    #endregion

    #region Messaging

    public async Task SendMessageAsync(Guid channelId, Guid userId, string username, string content, List<string>? imageUrls = null)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
            return;

        var message = new ChatMessage(channelId, userId, username, content, DateTime.UtcNow, imageUrls);

        lock (GetChannelLock(channelId))
        {
            if (!_channelMessages.TryGetValue(channelId, out var messages))
                return;

            messages.Add(message);
        }

        // Persist message
        await _persistence.PersistMessageAsync(message);

        // Stop typing when message is sent
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
            typingUsers.TryRemove(username, out _);

        await InvokeParallelAsync(OnMessageReceived, message);

        // Increment unread counts for other participants
        await IncrementUnreadAsync(channelId, userId);

        // Send push notification for DMs
        if (channel.IsDirectMessage)
        {
            var recipient = channel.GetOtherParticipant(username);
            if (recipient != null)
            {
                var preview = imageUrls?.Count > 0 ? "[Image]" : content;
                _ = _pushService.SendDmNotificationAsync(recipient, username, preview, 1);
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

    public async Task<bool> EditMessageAsync(Guid messageId, Guid channelId, string username, string newContent)
    {
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

        // Persist the edit
        await _persistence.PersistMessageAsync(message);

        await InvokeParallelAsync(OnMessageUpdated, message);

        return true;
    }

    public async Task<bool> DeleteMessageAsync(Guid messageId, Guid channelId, string username)
    {
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

        await InvokeParallelAsync(OnMessageDeleted, messageId, channelId);

        return true;
    }

    public async Task ToggleReactionAsync(Guid messageId, Guid channelId, Guid userId, string username, string emoji)
    {
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

        await InvokeParallelAsync(OnReactionChanged, message);
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
    /// </summary>
    public async Task MarkChannelAsReadAsync(Guid userId, Guid channelId)
    {
        var key = (userId, channelId);
        var now = DateTime.UtcNow;
        var hadUnread = false;

        if (_readStates.TryGetValue(key, out var state))
        {
            hadUnread = state.UnreadCount > 0;
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

        // Notify if there were unread messages that are now cleared
        if (hadUnread)
        {
            await InvokeParallelAsync(OnUnreadChanged, userId, channelId);
        }
    }

    /// <summary>
    /// Increments unread count for all participants except the sender.
    /// </summary>
    private async Task IncrementUnreadAsync(Guid channelId, Guid senderUserId)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
            return;

        if (channel.IsDirectMessage)
        {
            // For DMs, increment for the other participant
            var otherUserId = channel.GetOtherParticipantId(senderUserId);
            if (otherUserId.HasValue)
            {
                await IncrementUnreadForUserAsync(otherUserId.Value, channelId);
            }
        }
        else
        {
            // For rooms, increment for all online users except sender
            foreach (var session in _users.Values)
            {
                if (session.UserId != senderUserId)
                {
                    await IncrementUnreadForUserAsync(session.UserId, channelId);
                }
            }
        }
    }

    private async Task IncrementUnreadForUserAsync(Guid userId, Guid channelId)
    {
        var key = (userId, channelId);

        if (_readStates.TryGetValue(key, out var state))
        {
            state.UnreadCount++;
        }
        else
        {
            state = new ChannelReadState
            {
                UserId = userId,
                ChannelId = channelId,
                LastReadAt = DateTime.MinValue,
                UnreadCount = 1
            };
            _readStates[key] = state;
        }

        await _persistence.PersistReadStateAsync(state);

        // Notify about the unread change
        await InvokeParallelAsync(OnUnreadChanged, userId, channelId);
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

    public async Task StartTypingAsync(Guid channelId, string username)
    {
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
        {
            typingUsers[username] = DateTime.UtcNow;
            await InvokeParallelAsync(OnTypingUsersChanged, channelId);
        }
    }

    public async Task StopTypingAsync(Guid channelId, string username)
    {
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
        {
            typingUsers.TryRemove(username, out _);
            await InvokeParallelAsync(OnTypingUsersChanged, channelId);
        }
    }

    public List<string> GetTypingUsers(Guid channelId)
    {
        if (!_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
            return new List<string>();

        // Clean up stale typing indicators (> 3 seconds)
        var stale = typingUsers
            .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > 3)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var user in stale)
            typingUsers.TryRemove(user, out _);

        return typingUsers.Keys.ToList();
    }

    #endregion
}
