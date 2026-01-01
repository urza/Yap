namespace Yap.Models;

public class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ChannelId { get; init; }
    public string Username { get; init; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; init; }
    public List<string> ImageUrls { get; init; } = new();
    public bool IsEdited { get; set; }
    public bool IsRead { get; set; } // For DM unread tracking

    // Key = emoji, Value = set of usernames who reacted
    public Dictionary<string, HashSet<string>> Reactions { get; } = new();

    public bool HasImages => ImageUrls.Count > 0;

    public ChatMessage(Guid channelId, string username, string content, DateTime timestamp, List<string>? imageUrls = null)
    {
        ChannelId = channelId;
        Username = username;
        Content = content;
        Timestamp = timestamp;
        ImageUrls = imageUrls ?? new();
    }
}
