using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

public class ChatService
{
    private readonly ConcurrentDictionary<string, UserSession> _users = new();
    private readonly int _maxMessages = 100;

    // Rooms
    private readonly ConcurrentDictionary<Guid, Room> _rooms = new();
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _roomMessages = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _roomTypingUsers = new();
    private readonly object _roomLock = new();

    // Admin
    private string? _adminUser;
    private readonly object _adminLock = new();

    // Direct Messages
    private readonly ConcurrentDictionary<string, List<DirectMessage>> _directMessages = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _dmTypingUsers = new();
    private readonly object _dmLock = new();

    // Default room
    public Guid LobbyId { get; }

    // Events for real-time updates
    public event Func<ChatMessage, Task>? OnMessageReceived;
    public event Func<ChatMessage, Task>? OnMessageUpdated;
    public event Func<Guid, Guid, Task>? OnMessageDeleted; // messageId, roomId
    public event Func<ChatMessage, Task>? OnReactionChanged;
    public event Func<string, bool, Task>? OnUserChanged;
    public event Func<Task>? OnUsersListChanged;
    public event Func<Guid, Task>? OnTypingUsersChanged; // roomId

    // Room events
    public event Func<Room, Task>? OnRoomCreated;
    public event Func<Guid, Task>? OnRoomDeleted;

    // DM events
    public event Func<DirectMessage, Task>? OnDirectMessageReceived;
    public event Func<DirectMessage, Task>? OnDirectMessageUpdated;
    public event Func<Guid, string, Task>? OnDirectMessageDeleted; // messageId, conversationKey
    public event Func<string, Task>? OnDMTypingUsersChanged; // conversationKey

    // Admin events
    public event Func<string?, Task>? OnAdminChanged;

    public record UserSession(string Username, string CircuitId);

    public ChatService()
    {
        // Create default lobby room
        var lobby = new Room("lobby", null, isDefault: true);
        LobbyId = lobby.Id;
        _rooms[lobby.Id] = lobby;
        _roomMessages[lobby.Id] = new List<ChatMessage>();
        _roomTypingUsers[lobby.Id] = new ConcurrentDictionary<string, DateTime>();
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

    #region Room Management

    public List<Room> GetRooms() => _rooms.Values.OrderBy(r => r.IsDefault ? 0 : 1).ThenBy(r => r.CreatedAt).ToList();

    public Room? GetRoom(Guid roomId) => _rooms.TryGetValue(roomId, out var room) ? room : null;

    public async Task<Room?> CreateRoomAsync(string adminUsername, string roomName)
    {
        if (!IsAdmin(adminUsername))
            return null;

        // Normalize room name
        roomName = roomName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(roomName))
            return null;

        // Check if room already exists
        if (_rooms.Values.Any(r => r.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var room = new Room(roomName, adminUsername);
        _rooms[room.Id] = room;
        _roomMessages[room.Id] = new List<ChatMessage>();
        _roomTypingUsers[room.Id] = new ConcurrentDictionary<string, DateTime>();

        if (OnRoomCreated != null)
            await OnRoomCreated.Invoke(room);

        return room;
    }

    public async Task<bool> DeleteRoomAsync(string adminUsername, Guid roomId)
    {
        if (!IsAdmin(adminUsername))
            return false;

        if (!_rooms.TryGetValue(roomId, out var room))
            return false;

        // Cannot delete default lobby
        if (room.IsDefault)
            return false;

        _rooms.TryRemove(roomId, out _);
        _roomMessages.TryRemove(roomId, out _);
        _roomTypingUsers.TryRemove(roomId, out _);

        if (OnRoomDeleted != null)
            await OnRoomDeleted.Invoke(roomId);

        return true;
    }

    #endregion

    #region User Management

    public async Task AddUserAsync(string circuitId, string username)
    {
        _users[circuitId] = new UserSession(username, circuitId);

        // First user becomes admin
        await TrySetFirstAdmin(username);

        if (OnUserChanged != null)
            await OnUserChanged.Invoke(username, true);
        if (OnUsersListChanged != null)
            await OnUsersListChanged.Invoke();
    }

    public async Task RemoveUserAsync(string circuitId)
    {
        if (_users.TryRemove(circuitId, out var session))
        {
            // Remove from all room typing indicators
            foreach (var roomTyping in _roomTypingUsers.Values)
            {
                roomTyping.TryRemove(session.Username, out _);
            }

            // Remove from all DM typing indicators
            foreach (var dmTyping in _dmTypingUsers.Values)
            {
                dmTyping.TryRemove(session.Username, out _);
            }

            // Clear all DMs involving this user (ephemeral DMs)
            ClearUserDMs(session.Username);

            if (OnUserChanged != null)
                await OnUserChanged.Invoke(session.Username, false);
            if (OnUsersListChanged != null)
                await OnUsersListChanged.Invoke();
        }
    }

    /// <summary>
    /// Clears all DM conversations involving a user (called when user leaves)
    /// </summary>
    private void ClearUserDMs(string username)
    {
        var userLower = username.ToLowerInvariant();
        var keysToRemove = _directMessages.Keys
            .Where(k => k.Split('|').Contains(userLower))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _directMessages.TryRemove(key, out _);
            _dmTypingUsers.TryRemove(key, out _);
        }
    }

    public List<string> GetOnlineUsers() =>
        _users.Values.Select(u => u.Username).Distinct().ToList();

    #endregion

    #region Room Messaging

    public async Task SendMessageAsync(Guid roomId, string username, string content, List<string>? imageUrls = null)
    {
        if (!_rooms.ContainsKey(roomId))
            return;

        var message = new ChatMessage(roomId, username, content, DateTime.UtcNow, imageUrls);

        lock (_roomLock)
        {
            if (!_roomMessages.TryGetValue(roomId, out var messages))
                return;

            messages.Add(message);

            // Remove old messages if we exceed the limit
            while (messages.Count > _maxMessages)
            {
                messages.RemoveAt(0);
            }
        }

        // Stop typing when message is sent
        if (_roomTypingUsers.TryGetValue(roomId, out var typingUsers))
            typingUsers.TryRemove(username, out _);

        if (OnMessageReceived != null)
            await OnMessageReceived.Invoke(message);
    }

    public List<ChatMessage> GetRoomMessages(Guid roomId, int count = 50)
    {
        if (!_roomMessages.TryGetValue(roomId, out var messages))
            return new List<ChatMessage>();

        lock (_roomLock)
        {
            return messages.TakeLast(Math.Min(count, messages.Count)).ToList();
        }
    }

    public async Task<bool> EditMessageAsync(Guid messageId, Guid roomId, string username, string newContent)
    {
        if (!_roomMessages.TryGetValue(roomId, out var messages))
            return false;

        ChatMessage? message;
        lock (_roomLock)
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

    public async Task<bool> DeleteMessageAsync(Guid messageId, Guid roomId, string username)
    {
        if (!_roomMessages.TryGetValue(roomId, out var messages))
            return false;

        ChatMessage? message;
        lock (_roomLock)
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
            if (message == null || message.Username != username)
                return false;

            messages.Remove(message);
        }

        if (OnMessageDeleted != null)
            await OnMessageDeleted.Invoke(messageId, roomId);

        return true;
    }

    public async Task ToggleReactionAsync(Guid messageId, Guid roomId, string username, string emoji)
    {
        if (!_roomMessages.TryGetValue(roomId, out var messages))
            return;

        ChatMessage? message;
        lock (_roomLock)
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

    #region Room Typing Indicators

    public async Task StartTypingAsync(Guid roomId, string username)
    {
        if (_roomTypingUsers.TryGetValue(roomId, out var typingUsers))
        {
            typingUsers[username] = DateTime.UtcNow;
            if (OnTypingUsersChanged != null)
                await OnTypingUsersChanged.Invoke(roomId);
        }
    }

    public async Task StopTypingAsync(Guid roomId, string username)
    {
        if (_roomTypingUsers.TryGetValue(roomId, out var typingUsers))
        {
            typingUsers.TryRemove(username, out _);
            if (OnTypingUsersChanged != null)
                await OnTypingUsersChanged.Invoke(roomId);
        }
    }

    public List<string> GetTypingUsers(Guid roomId)
    {
        if (!_roomTypingUsers.TryGetValue(roomId, out var typingUsers))
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

    #region Direct Messages

    public async Task SendDirectMessageAsync(string fromUser, string toUser, string content, List<string>? imageUrls = null)
    {
        var key = DirectMessage.GetConversationKey(fromUser, toUser);
        var message = new DirectMessage(fromUser, toUser, content, DateTime.UtcNow, imageUrls);

        lock (_dmLock)
        {
            if (!_directMessages.TryGetValue(key, out var messages))
            {
                messages = new List<DirectMessage>();
                _directMessages[key] = messages;
            }

            messages.Add(message);

            // Remove old messages if we exceed the limit
            while (messages.Count > _maxMessages)
            {
                messages.RemoveAt(0);
            }
        }

        // Stop typing when message is sent
        if (_dmTypingUsers.TryGetValue(key, out var typingUsers))
            typingUsers.TryRemove(fromUser, out _);

        if (OnDirectMessageReceived != null)
            await OnDirectMessageReceived.Invoke(message);
    }

    public List<DirectMessage> GetDirectMessages(string user1, string user2, int count = 50)
    {
        var key = DirectMessage.GetConversationKey(user1, user2);
        if (!_directMessages.TryGetValue(key, out var messages))
            return new List<DirectMessage>();

        lock (_dmLock)
        {
            return messages.TakeLast(Math.Min(count, messages.Count)).ToList();
        }
    }

    /// <summary>
    /// Gets all users that have DM history with the specified user
    /// </summary>
    public List<string> GetDMConversations(string username)
    {
        var userLower = username.ToLowerInvariant();
        var conversations = new List<string>();

        foreach (var key in _directMessages.Keys)
        {
            var parts = key.Split('|');
            if (parts.Length == 2)
            {
                if (parts[0] == userLower)
                    conversations.Add(parts[1]);
                else if (parts[1] == userLower)
                    conversations.Add(parts[0]);
            }
        }

        return conversations;
    }

    /// <summary>
    /// Gets unread DM count for a user from a specific sender
    /// </summary>
    public int GetUnreadDMCount(string forUser, string fromUser)
    {
        var key = DirectMessage.GetConversationKey(forUser, fromUser);
        if (!_directMessages.TryGetValue(key, out var messages))
            return 0;

        lock (_dmLock)
        {
            return messages.Count(m =>
                m.ToUser.Equals(forUser, StringComparison.OrdinalIgnoreCase) &&
                !m.IsRead);
        }
    }

    /// <summary>
    /// Marks all DMs in a conversation as read for a user
    /// </summary>
    public void MarkDMsAsRead(string forUser, string fromUser)
    {
        var key = DirectMessage.GetConversationKey(forUser, fromUser);
        if (!_directMessages.TryGetValue(key, out var messages))
            return;

        lock (_dmLock)
        {
            foreach (var msg in messages.Where(m =>
                m.ToUser.Equals(forUser, StringComparison.OrdinalIgnoreCase) &&
                !m.IsRead))
            {
                msg.IsRead = true;
            }
        }
    }

    public async Task<bool> EditDirectMessageAsync(Guid messageId, string fromUser, string toUser, string newContent)
    {
        var key = DirectMessage.GetConversationKey(fromUser, toUser);
        if (!_directMessages.TryGetValue(key, out var messages))
            return false;

        DirectMessage? message;
        lock (_dmLock)
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
        }

        if (message == null || !message.FromUser.Equals(fromUser, StringComparison.OrdinalIgnoreCase))
            return false;

        if (message.HasImages)
            return false;

        message.Content = newContent;
        message.IsEdited = true;

        if (OnDirectMessageUpdated != null)
            await OnDirectMessageUpdated.Invoke(message);

        return true;
    }

    public async Task<bool> DeleteDirectMessageAsync(Guid messageId, string fromUser, string toUser)
    {
        var key = DirectMessage.GetConversationKey(fromUser, toUser);
        if (!_directMessages.TryGetValue(key, out var messages))
            return false;

        DirectMessage? message;
        lock (_dmLock)
        {
            message = messages.FirstOrDefault(m => m.Id == messageId);
            if (message == null || !message.FromUser.Equals(fromUser, StringComparison.OrdinalIgnoreCase))
                return false;

            messages.Remove(message);
        }

        if (OnDirectMessageDeleted != null)
            await OnDirectMessageDeleted.Invoke(messageId, key);

        return true;
    }

    /// <summary>
    /// Gets the timestamp of the last DM in a conversation
    /// </summary>
    public DateTime? GetLastDMTimestamp(string user1, string user2)
    {
        var key = DirectMessage.GetConversationKey(user1, user2);
        if (!_directMessages.TryGetValue(key, out var messages) || messages.Count == 0)
            return null;

        lock (_dmLock)
        {
            return messages.LastOrDefault()?.Timestamp;
        }
    }

    /// <summary>
    /// Gets total unread DM count for a user across all conversations
    /// </summary>
    public int GetTotalUnreadDMCount(string forUser)
    {
        var total = 0;
        var userLower = forUser.ToLowerInvariant();

        foreach (var kvp in _directMessages)
        {
            var parts = kvp.Key.Split('|');
            if (parts.Length == 2 && (parts[0] == userLower || parts[1] == userLower))
            {
                lock (_dmLock)
                {
                    total += kvp.Value.Count(m =>
                        m.ToUser.Equals(forUser, StringComparison.OrdinalIgnoreCase) &&
                        !m.IsRead);
                }
            }
        }

        return total;
    }

    #endregion

    #region DM Typing Indicators

    public async Task StartDMTypingAsync(string fromUser, string toUser)
    {
        var key = DirectMessage.GetConversationKey(fromUser, toUser);

        if (!_dmTypingUsers.TryGetValue(key, out var typingUsers))
        {
            typingUsers = new ConcurrentDictionary<string, DateTime>();
            _dmTypingUsers[key] = typingUsers;
        }

        typingUsers[fromUser] = DateTime.UtcNow;

        if (OnDMTypingUsersChanged != null)
            await OnDMTypingUsersChanged.Invoke(key);
    }

    public async Task StopDMTypingAsync(string fromUser, string toUser)
    {
        var key = DirectMessage.GetConversationKey(fromUser, toUser);

        if (_dmTypingUsers.TryGetValue(key, out var typingUsers))
        {
            typingUsers.TryRemove(fromUser, out _);
            if (OnDMTypingUsersChanged != null)
                await OnDMTypingUsersChanged.Invoke(key);
        }
    }

    public List<string> GetDMTypingUsers(string user1, string user2)
    {
        var key = DirectMessage.GetConversationKey(user1, user2);
        if (!_dmTypingUsers.TryGetValue(key, out var typingUsers))
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
