namespace Yap.Models;

public class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Username { get; init; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; init; }
    public bool IsImage { get; init; }
    public bool IsEdited { get; set; }

    // Key = emoji, Value = set of usernames who reacted
    public Dictionary<string, HashSet<string>> Reactions { get; } = new();

    public ChatMessage(string username, string content, DateTime timestamp, bool isImage = false)
    {
        Username = username;
        Content = content;
        Timestamp = timestamp;
        IsImage = isImage;
    }
}
