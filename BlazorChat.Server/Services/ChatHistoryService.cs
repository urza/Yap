using System.Collections.Concurrent;

namespace BlazorChat.Server.Services;

public class ChatHistoryService
{
    private readonly ConcurrentQueue<ChatMessage> _messages = new();
    private readonly int _maxMessages = 100; // Keep last 100 messages

    public void AddMessage(string username, string content, bool isImage = false)
    {
        var message = new ChatMessage
        {
            Username = username,
            Content = content,
            Timestamp = DateTime.UtcNow,
            IsImage = isImage
        };

        _messages.Enqueue(message);

        // Remove old messages if we exceed the limit
        while (_messages.Count > _maxMessages)
        {
            _messages.TryDequeue(out _);
        }
    }

    public List<ChatMessage> GetRecentMessages(int count = 50)
    {
        return _messages.TakeLast(Math.Min(count, _messages.Count)).ToList();
    }
}

public class ChatMessage
{
    public string Username { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsImage { get; set; }
}