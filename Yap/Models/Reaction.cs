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

    /// <summary>
    /// Checks if a user has reacted with a specific emoji.
    /// </summary>
    public static bool HasUserReacted(this IEnumerable<Reaction> reactions, string emoji, string username) =>
        reactions.Any(r => r.Emoji == emoji && r.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets all usernames who reacted with a specific emoji.
    /// </summary>
    public static IEnumerable<string> GetUsersForEmoji(this IEnumerable<Reaction> reactions, string emoji) =>
        reactions.Where(r => r.Emoji == emoji).Select(r => r.Username);
}
