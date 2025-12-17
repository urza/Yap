using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

public class ChatService
{
    private readonly ConcurrentDictionary<string, UserSession> _users = new();
    private readonly ConcurrentQueue<ChatMessage> _messages = new();
    private readonly ConcurrentDictionary<string, DateTime> _typingUsers = new();
    private readonly int _maxMessages = 100;

    // Events for real-time updates
    public event Func<ChatMessage, Task>? OnMessageReceived;
    public event Func<string, bool, Task>? OnUserChanged;  // username, isJoining
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
        _messages.Enqueue(message);

        while (_messages.Count > _maxMessages)
            _messages.TryDequeue(out _);

        if (OnMessageReceived != null)
            await OnMessageReceived.Invoke(message);
    }

    public List<ChatMessage> GetRecentMessages(int count = 50) =>
        _messages.TakeLast(Math.Min(count, _messages.Count)).ToList();

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
