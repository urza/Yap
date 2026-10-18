namespace Yap.Models.Chat;

public class Reaction
{
    public int Id { get; set; }
    public Guid MessageId { get; set; }

    /// <summary>
    /// The reacting user's id. No foreign key, as on <see cref="ChatMessage.UserId"/>: the reaction outlives the account (AD-39).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Denormalized username for display (avoids joins for common reads).
    /// </summary>
    public string Username { get; set; } = "";

    public string Emoji { get; set; } = "";

    // Navigation properties
    public ChatMessage Message { get; set; } = null!;
}

public static class ReactionExtensions
{
    /// <summary>
    /// Groups reactions by emoji for display purposes.
    /// Returns a dictionary where key is emoji and value is list of usernames who reacted.
    /// </summary>
    public static Dictionary<string, List<string>> GroupByEmoji(this IEnumerable<Reaction> reactions) =>
        reactions.GroupBy(r => r.Emoji)
                 .ToDictionary(g => g.Key, g => g.Select(r => r.Username).ToList());
}
