namespace Yap.Models;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }

    /// <summary>
    /// The user who sent this message (foreign key to User).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Denormalized username for display (avoids joins for common reads).
    /// </summary>
    public string Username { get; set; } = "";

    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public List<string> ImageUrls { get; set; } = new();
    public bool IsEdited { get; set; }

    // Navigation properties
    public Channel Channel { get; set; } = null!;
    public User User { get; set; } = null!;
    public List<Reaction> Reactions { get; set; } = new();

    public bool HasImages => ImageUrls.Count > 0;

    private ChatMessage() { } // EF Core constructor

    public ChatMessage(Guid channelId, Guid userId, string username, string content, DateTime timestamp, List<string>? imageUrls = null)
    {
        ChannelId = channelId;
        UserId = userId;
        Username = username;
        Content = content;
        Timestamp = timestamp;
        ImageUrls = imageUrls ?? new();
        Reactions = new();
    }
}
