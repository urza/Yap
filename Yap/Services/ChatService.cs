using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

public class ChatService
{
    private readonly ConcurrentDictionary<string, UserSession> _users = new();
    private readonly ConcurrentDictionary<Guid, ChatMessage> _messages = new();
    private readonly List<Guid> _messageOrder = new(); // Maintains insertion order
    private readonly object _orderLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _typingUsers = new();
    private readonly int _maxMessages = 100;

    // Events for real-time updates
    public event Func<ChatMessage, Task>? OnMessageReceived;
    public event Func<ChatMessage, Task>? OnMessageUpdated;
    public event Func<Guid, Task>? OnMessageDeleted;
    public event Func<ChatMessage, Task>? OnReactionChanged;
    public event Func<string, bool, Task>? OnUserChanged;
    public event Func<Task>? OnUsersListChanged;
    public event Func<Task>? OnTypingUsersChanged;

    public record UserSession(string Username, string CircuitId);

    // User management
    public async Task AddUserAsync(string circuitId, string username)
    {
        _users[circuitId] = new UserSession(username, circuitId);
        if (OnUserChanged != null)
            await OnUserChanged.Invoke(username, true);
        if (OnUsersListChanged != null)
            await OnUsersListChanged.Invoke();
    }

    public async Task RemoveUserAsync(string circuitId)
    {
        if (_users.TryRemove(circuitId, out var session))
        {
            _typingUsers.TryRemove(session.Username, out _);
            if (OnUserChanged != null)
                await OnUserChanged.Invoke(session.Username, false);
            if (OnUsersListChanged != null)
                await OnUsersListChanged.Invoke();
            if (OnTypingUsersChanged != null)
                await OnTypingUsersChanged.Invoke();
        }
    }

    public List<string> GetOnlineUsers() =>
        _users.Values.Select(u => u.Username).Distinct().ToList();

    // Messaging
    public async Task SendMessageAsync(string username, string content, bool isImage = false)
    {
        var message = new ChatMessage(username, content, DateTime.UtcNow, isImage);
        _messages[message.Id] = message;

        lock (_orderLock)
        {
            _messageOrder.Add(message.Id);

            // Remove old messages if we exceed the limit
            while (_messageOrder.Count > _maxMessages)
            {
                var oldestId = _messageOrder[0];
                _messageOrder.RemoveAt(0);
                _messages.TryRemove(oldestId, out _);
            }
        }

        if (OnMessageReceived != null)
            await OnMessageReceived.Invoke(message);
    }

    public List<ChatMessage> GetRecentMessages(int count = 50)
    {
        lock (_orderLock)
        {
            return _messageOrder
                .TakeLast(Math.Min(count, _messageOrder.Count))
                .Select(id => _messages.TryGetValue(id, out var msg) ? msg : null)
                .Where(msg => msg != null)
                .Cast<ChatMessage>()
                .ToList();
        }
    }

    // Edit message (only by owner)
    public async Task<bool> EditMessageAsync(Guid messageId, string username, string newContent)
    {
        if (!_messages.TryGetValue(messageId, out var message))
            return false;

        if (message.Username != username)
            return false;

        if (message.IsImage)
            return false; // Can't edit image messages

        message.Content = newContent;
        message.IsEdited = true;

        if (OnMessageUpdated != null)
            await OnMessageUpdated.Invoke(message);

        return true;
    }

    // Delete message (only by owner)
    public async Task<bool> DeleteMessageAsync(Guid messageId, string username)
    {
        if (!_messages.TryGetValue(messageId, out var message))
            return false;

        if (message.Username != username)
            return false;

        if (_messages.TryRemove(messageId, out _))
        {
            lock (_orderLock)
            {
                _messageOrder.Remove(messageId);
            }

            if (OnMessageDeleted != null)
                await OnMessageDeleted.Invoke(messageId);

            return true;
        }

        return false;
    }

    // Toggle reaction
    public async Task ToggleReactionAsync(Guid messageId, string username, string emoji)
    {
        if (!_messages.TryGetValue(messageId, out var message))
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

    // Typing indicators
    public async Task StartTypingAsync(string username)
    {
        _typingUsers[username] = DateTime.UtcNow;
        if (OnTypingUsersChanged != null)
            await OnTypingUsersChanged.Invoke();
    }

    public async Task StopTypingAsync(string username)
    {
        _typingUsers.TryRemove(username, out _);
        if (OnTypingUsersChanged != null)
            await OnTypingUsersChanged.Invoke();
    }

    public List<string> GetTypingUsers()
    {
        // Clean up stale typing indicators (> 3 seconds)
        var stale = _typingUsers
            .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > 3)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var user in stale)
            _typingUsers.TryRemove(user, out _);

        return _typingUsers.Keys.ToList();
    }
}
