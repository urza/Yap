using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Singleton service that holds all chat state and broadcasts changes via events.
/// Components subscribe to events and call StateHasChanged() to update their UI.
/// All state is in-memory - lost on server restart.
/// </summary>
public class ChatService
{
    private readonly ConcurrentDictionary<string, UserSession> _users = new();
    private readonly int _maxMessagesPerChannel; // Each channel (room or DM) keeps only the last X messages in memory. Older ones are discarded.
    private readonly PushNotificationService _pushService;

    // Channels (rooms and DMs)
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _channelMessages = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _channelTypingUsers = new();
    // Protects List<ChatMessage> operations (List is not thread-safe).
    // Fine for small scale; for larger scale consider per-channel locking or ConcurrentBag.
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

    public ChatService(IConfiguration configuration, PushNotificationService pushService)
    {
        _maxMessagesPerChannel = configuration.GetValue("ChatSettings:MaxMessagesPerChannel", 100);
        _pushService = pushService;

        // Create default lobby channel
        var lobby = Channel.CreateRoom("lobby", createdBy: null, isDefault: true);
        LobbyId = lobby.Id;
        _channels[lobby.Id] = lobby;
        _channelMessages[lobby.Id] = new List<ChatMessage>();
        _channelTypingUsers[lobby.Id] = new ConcurrentDictionary<string, DateTime>();
    }

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

        if (OnChannelDeleted != null)
            await OnChannelDeleted.Invoke(channelId);

        return true;
    }

    /// <summary>
    /// Gets or creates a DM channel between two users
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

            // Clear all DM channels involving this user (ephemeral DMs)
            ClearUserDMChannels(session.Username);

            if (OnUserChanged != null)
                await OnUserChanged.Invoke(session.Username, false);
            if (OnUsersListChanged != null)
                await OnUsersListChanged.Invoke();
        }
    }

    /// <summary>
    /// Clears all DM channels involving a user (called when user leaves)
    /// </summary>
    private void ClearUserDMChannels(string username)
    {
        var dmChannels = _channels.Values
            .Where(c => c.IsDirectMessage && c.CanAccess(username))
            .Select(c => c.Id)
            .ToList();

        foreach (var channelId in dmChannels)
        {
            _channels.TryRemove(channelId, out _);
            _channelMessages.TryRemove(channelId, out _);
            _channelTypingUsers.TryRemove(channelId, out _);
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
                var unreadCount = GetTotalUnreadDMCount(recipient);
                var preview = imageUrls?.Count > 0 ? "[Image]" : content;
                _ = _pushService.SendDmNotificationAsync(recipient, username, preview, unreadCount);
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

        lock (message.Reactions)
        {
            if (!message.Reactions.ContainsKey(emoji))
                message.Reactions[emoji] = new HashSet<string>();

            if (message.Reactions[emoji].Contains(username))
                message.Reactions[emoji].Remove(username);
            else
                message.Reactions[emoji].Add(username);

            // Clean up empty reaction sets
            if (message.Reactions[emoji].Count == 0)
                message.Reactions.Remove(emoji);
        }

        if (OnReactionChanged != null)
            await OnReactionChanged.Invoke(message);
    }

    #endregion

    #region DM-specific helpers

    /// <summary>
    /// Gets unread message count for a user in a DM channel
    /// </summary>
    public int GetUnreadDMCount(string forUser, string fromUser)
    {
        var channel = _channels.Values.FirstOrDefault(c => c.IsDMBetween(forUser, fromUser));
        if (channel == null || !_channelMessages.TryGetValue(channel.Id, out var messages))
            return 0;

        lock (_channelLock)
        {
            return messages.Count(m =>
                !m.Username.Equals(forUser, StringComparison.OrdinalIgnoreCase) &&
                !m.IsRead);
        }
    }

    /// <summary>
    /// Marks all messages in a DM channel as read for a user
    /// </summary>
    public void MarkDMsAsRead(string forUser, string otherUser)
    {
        var channel = _channels.Values.FirstOrDefault(c => c.IsDMBetween(forUser, otherUser));
        if (channel == null || !_channelMessages.TryGetValue(channel.Id, out var messages))
            return;

        lock (_channelLock)
        {
            foreach (var msg in messages.Where(m =>
                !m.Username.Equals(forUser, StringComparison.OrdinalIgnoreCase) &&
                !m.IsRead))
            {
                msg.IsRead = true;
            }
        }
    }

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
    /// Gets total unread DM count for a user across all DM channels
    /// </summary>
    public int GetTotalUnreadDMCount(string forUser)
    {
        var total = 0;

        foreach (var channel in _channels.Values.Where(c => c.IsDirectMessage && c.CanAccess(forUser)))
        {
            if (_channelMessages.TryGetValue(channel.Id, out var messages))
            {
                lock (_channelLock)
                {
                    total += messages.Count(m =>
                        !m.Username.Equals(forUser, StringComparison.OrdinalIgnoreCase) &&
                        !m.IsRead);
                }
            }
        }

        return total;
    }

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
