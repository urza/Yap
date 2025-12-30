namespace Yap.Models;

public class DirectMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FromUser { get; init; } = "";
    public string ToUser { get; init; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; init; }
    public List<string> ImageUrls { get; init; } = new();
    public bool IsEdited { get; set; }
    public bool IsRead { get; set; }

    public bool HasImages => ImageUrls.Count > 0;

    public DirectMessage(string fromUser, string toUser, string content, DateTime timestamp, List<string>? imageUrls = null)
    {
        FromUser = fromUser;
        ToUser = toUser;
        Content = content;
        Timestamp = timestamp;
        ImageUrls = imageUrls ?? new();
    }

    /// <summary>
    /// Gets the canonical conversation key for two users (sorted alphabetically)
    /// </summary>
    public static string GetConversationKey(string user1, string user2)
    {
        var users = new[] { user1.ToLowerInvariant(), user2.ToLowerInvariant() };
        Array.Sort(users);
        return $"{users[0]}|{users[1]}";
    }
}
