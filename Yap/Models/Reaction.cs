namespace Yap.Models;

public class Reaction
{
    public int Id { get; set; }
    public Guid MessageId { get; set; }
    public string Emoji { get; set; } = "";
    public string Username { get; set; } = "";

    // Navigation property
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
