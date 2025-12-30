namespace Yap.Models;

public class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RoomId { get; init; }
    public string Username { get; init; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; init; }
    public List<string> ImageUrls { get; init; } = new();
    public bool IsEdited { get; set; }

    // Key = emoji, Value = set of usernames who reacted
    public Dictionary<string, HashSet<string>> Reactions { get; } = new();

    public bool HasImages => ImageUrls.Count > 0;

    public ChatMessage(Guid roomId, string username, string content, DateTime timestamp, List<string>? imageUrls = null)
    {
        RoomId = roomId;
        Username = username;
        Content = content;
        Timestamp = timestamp;
        ImageUrls = imageUrls ?? new();
    }
}
