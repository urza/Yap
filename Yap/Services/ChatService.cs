using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
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
    private readonly int _maxMessagesPerChannel;
    private readonly PushNotificationService _pushService;
    private readonly ChatPersistenceService _persistence;

    // Channels (rooms and DMs)
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _channelMessages = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _channelTypingUsers = new();
    private readonly object _channelLock = new();

    // Admin
    private string? _adminUser;
    private readonly object _adminLock = new();

    // Default lobby channel
    public Guid LobbyId { get; }

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

    public record UserSession(string Username, string SessionId, UserStatus Status = UserStatus.Online);

    public ChatService(IConfiguration configuration, PushNotificationService pushService, ChatPersistenceService persistence)
    {
        _maxMessagesPerChannel = configuration.GetValue("ChatSettings:MaxMessagesPerChannel", 100);
        _pushService = pushService;
        _persistence = persistence;

        // Create default lobby channel (will be replaced if loading from DB)
        var lobby = Channel.CreateRoom("lobby", createdBy: null, isDefault: true);
        LobbyId = lobby.Id;
        _channels[lobby.Id] = lobby;
        _channelMessages[lobby.Id] = new List<ChatMessage>();
        _channelTypingUsers[lobby.Id] = new ConcurrentDictionary<string, DateTime>();
    }

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

        // Ensure lobby exists
        var existingLobby = _channels.Values.FirstOrDefault(c => c.Type == ChannelType.Room && c.IsDefault);
        if (existingLobby != null)
        {
            // Update LobbyId to match existing lobby
            // Note: LobbyId is read-only, but we set it in constructor. Since we're loading from DB,
            // we need to find the lobby dynamically
        }
        else
        {
            // Create lobby if it doesn't exist in DB
            var lobby = Channel.CreateRoom("lobby", createdBy: null, isDefault: true);
            _channels[lobby.Id] = lobby;
            _channelMessages[lobby.Id] = new List<ChatMessage>();
            _channelTypingUsers[lobby.Id] = new ConcurrentDictionary<string, DateTime>();
            await _persistence.PersistChannelAsync(lobby);
        }
    }

    /// <summary>
    /// Gets the lobby channel ID. May change after InitializeAsync if loaded from DB.
    /// </summary>
    public Guid GetLobbyId() =>
        _channels.Values.FirstOrDefault(c => c.Type == ChannelType.Room && c.IsDefault)?.Id ?? LobbyId;

    #region Admin

    public string? GetAdmin() => _adminUser;

    public bool IsAdmin(string username) =>
        _adminUser != null && _adminUser.Equals(username, StringComparison.OrdinalIgnoreCase);

    private async Task TrySetFirstAdmin(string username)
    {
        bool becameAdmin = false;
        lock (_adminLock)
        {
            if (_adminUser == null)
            {
                _adminUser = username;
                becameAdmin = true;
            }
        }

        if (becameAdmin && OnAdminChanged != null)
            await OnAdminChanged.Invoke(_adminUser);
    }

    #endregion

    #region Channel Management

    public List<Channel> GetRooms() =>
        _channels.Values
            .Where(c => c.Type == ChannelType.Room)
            .OrderBy(c => c.IsDefault ? 0 : 1)
            .ThenBy(c => c.CreatedAt)
            .ToList();

    public Channel? GetChannel(Guid channelId) =>
        _channels.TryGetValue(channelId, out var channel) ? channel : null;

    public async Task<Channel?> CreateRoomAsync(string adminUsername, string roomName)
    {
        if (!IsAdmin(adminUsername))
            return null;

        // Normalize room name
        roomName = roomName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(roomName))
            return null;

        // Check if room already exists
        if (_channels.Values.Any(c => c.Type == ChannelType.Room &&
            c.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var channel = Channel.CreateRoom(roomName, adminUsername);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = new List<ChatMessage>();
        _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();

        // Persist to database
        await _persistence.PersistChannelAsync(channel);

        if (OnChannelCreated != null)
            await OnChannelCreated.Invoke(channel);

        return channel;
    }

    public async Task<bool> DeleteRoomAsync(string adminUsername, Guid channelId)
    {
        if (!IsAdmin(adminUsername))
            return false;

        if (!_channels.TryGetValue(channelId, out var channel))
            return false;

        // Cannot delete default lobby or DM channels
        if (channel.IsDefault || channel.IsDirectMessage)
            return false;

        _channels.TryRemove(channelId, out _);
        _channelMessages.TryRemove(channelId, out _);
        _channelTypingUsers.TryRemove(channelId, out _);

        // Delete from database
        await _persistence.DeleteChannelAsync(channelId);

        if (OnChannelDeleted != null)
            await OnChannelDeleted.Invoke(channelId);

        return true;
    }

    /// <summary>
    /// Gets or creates a DM channel between two users
    /// </summary>
    public async Task<Channel> GetOrCreateDMChannelAsync(string user1, string user2)
    {
        // Check if DM channel already exists
        var existing = _channels.Values.FirstOrDefault(c => c.IsDMBetween(user1, user2));
        if (existing != null)
            return existing;

        // Create new DM channel
        var channel = Channel.CreateDM(user1, user2);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = new List<ChatMessage>();
        _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();

        // Persist to database (DMs now persist permanently)
        await _persistence.PersistChannelAsync(channel);

        return channel;
    }

    /// <summary>
    /// Gets or creates a DM channel between two users (sync version for compatibility)
    /// </summary>
    public Channel GetOrCreateDMChannel(string user1, string user2)
    {
        // Check if DM channel already exists
        var existing = _channels.Values.FirstOrDefault(c => c.IsDMBetween(user1, user2));
        if (existing != null)
            return existing;

        // Create new DM channel
        var channel = Channel.CreateDM(user1, user2);
        _channels[channel.Id] = channel;
        _channelMessages[channel.Id] = new List<ChatMessage>();
        _channelTypingUsers[channel.Id] = new ConcurrentDictionary<string, DateTime>();

        // Persist to database (fire and forget)
        _ = _persistence.PersistChannelAsync(channel);

        return channel;
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

    public async Task AddUserAsync(string sessionId, string username, UserStatus status = UserStatus.Online)
    {
        _users[sessionId] = new UserSession(username, sessionId, status);

        // First user becomes admin
        await TrySetFirstAdmin(username);

        if (OnUserChanged != null)
            await OnUserChanged.Invoke(username, true);
        if (OnUsersListChanged != null)
            await OnUsersListChanged.Invoke();
    }

    public async Task SetUserStatusAsync(string sessionId, UserStatus status)
    {
        if (!_users.TryGetValue(sessionId, out var session))
            return;

        // Update with new status
        _users[sessionId] = session with { Status = status };

        if (OnUserStatusChanged != null)
            await OnUserStatusChanged.Invoke(session.Username, status);
        if (OnUsersListChanged != null)
            await OnUsersListChanged.Invoke();
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

            if (OnUserChanged != null)
                await OnUserChanged.Invoke(session.Username, false);
            if (OnUsersListChanged != null)
                await OnUsersListChanged.Invoke();
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
    public List<(string Username, UserStatus Status)> GetAllUsersWithStatus() =>
        _users.Values
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().Status))
            .ToList();

    public bool IsUsernameTaken(string username) =>
        _users.Values.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region Messaging

    public async Task SendMessageAsync(Guid channelId, string username, string content, List<string>? imageUrls = null)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
            return;

        var message = new ChatMessage(channelId, username, content, DateTime.UtcNow, imageUrls);

        lock (_channelLock)
        {
            if (!_channelMessages.TryGetValue(channelId, out var messages))
                return;

            messages.Add(message);

            // Remove old messages if we exceed the limit
            while (messages.Count > _maxMessagesPerChannel)
            {
                messages.RemoveAt(0);
            }
        }

        // Persist message and trim old ones in database
        await _persistence.PersistMessageAsync(message);
        await _persistence.TrimMessagesAsync(channelId, _maxMessagesPerChannel);

        // Stop typing when message is sent
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
            typingUsers.TryRemove(username, out _);

        if (OnMessageReceived != null)
            await OnMessageReceived.Invoke(message);

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

        lock (_channelLock)
        {
            return messages.TakeLast(Math.Min(count, messages.Count)).ToList();
        }
    }

    public async Task<bool> EditMessageAsync(Guid messageId, Guid channelId, string username, string newContent)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return false;

        ChatMessage? message;
        lock (_channelLock)
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

        if (OnMessageUpdated != null)
            await OnMessageUpdated.Invoke(message);

        return true;
    }

    public async Task<bool> DeleteMessageAsync(Guid messageId, Guid channelId, string username)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return false;

        ChatMessage? message;
        lock (_channelLock)
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
            if (message == null || message.Username != username)
                return false;

            messages.Remove(message);
        }

        // Delete from database
        await _persistence.DeleteMessageAsync(messageId);

        if (OnMessageDeleted != null)
            await OnMessageDeleted.Invoke(messageId, channelId);

        return true;
    }

    public async Task ToggleReactionAsync(Guid messageId, Guid channelId, string username, string emoji)
    {
        if (!_channelMessages.TryGetValue(channelId, out var messages))
            return;

        ChatMessage? message;
        lock (_channelLock)
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
        }

        if (message == null)
            return;

        bool added;
        lock (message.Reactions)
        {
            var existingReaction = message.Reactions.FirstOrDefault(r =>
                r.Emoji == emoji && r.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

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
                    Emoji = emoji,
                    Username = username
                });
                added = true;
            }
        }

        // Persist reaction change
        if (added)
            await _persistence.AddReactionAsync(messageId, emoji, username);
        else
            await _persistence.RemoveReactionAsync(messageId, emoji, username);

        if (OnReactionChanged != null)
            await OnReactionChanged.Invoke(message);
    }

    #endregion

    #region DM-specific helpers

    // Note: Read tracking was removed (IsRead property).
    // These methods are kept for API compatibility but always return 0.
    // TODO: Implement proper read tracking with a separate ReadReceipt table if needed.

    /// <summary>
    /// Gets unread message count for a user in a DM channel.
    /// Currently always returns 0 (read tracking not implemented).
    /// </summary>
    public int GetUnreadDMCount(string forUser, string fromUser) => 0;

    /// <summary>
    /// Marks all messages in a DM channel as read for a user.
    /// Currently a no-op (read tracking not implemented).
    /// </summary>
    public void MarkDMsAsRead(string forUser, string otherUser) { }

    /// <summary>
    /// Gets the timestamp of the last message in a DM channel
    /// </summary>
    public DateTime? GetLastDMTimestamp(string user1, string user2)
    {
        var channel = _channels.Values.FirstOrDefault(c => c.IsDMBetween(user1, user2));
        if (channel == null || !_channelMessages.TryGetValue(channel.Id, out var messages) || messages.Count == 0)
            return null;

        lock (_channelLock)
        {
            return messages.LastOrDefault()?.Timestamp;
        }
    }

    /// <summary>
    /// Gets total unread DM count for a user across all DM channels.
    /// Currently always returns 0 (read tracking not implemented).
    /// </summary>
    public int GetTotalUnreadDMCount(string forUser) => 0;

    #endregion

    #region Typing Indicators

    public async Task StartTypingAsync(Guid channelId, string username)
    {
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
        {
            typingUsers[username] = DateTime.UtcNow;
            if (OnTypingUsersChanged != null)
                await OnTypingUsersChanged.Invoke(channelId);
        }
    }

    public async Task StopTypingAsync(Guid channelId, string username)
    {
        if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers))
        {
            typingUsers.TryRemove(username, out _);
            if (OnTypingUsersChanged != null)
                await OnTypingUsersChanged.Invoke(channelId);
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
