namespace Yap.Helpers;

/// <summary>
/// Deterministic gradient colors derived from a username.
/// Shared by <c>Avatar</c> (initials background) and <c>UserProfileCard</c> (banner)
/// so a user's color identity stays consistent across the UI.
/// </summary>
public static class AvatarColor
{
    /// <summary>
    /// Curated gradient pairs — visually pleasing combos that look good behind white text.
    /// </summary>
    private static readonly (string From, string To)[] Gradients =
    [
        ("#667eea", "#764ba2"), // indigo → purple
        ("#f093fb", "#f5576c"), // pink → coral
        ("#4facfe", "#00f2fe"), // sky → cyan
        ("#43e97b", "#38f9d7"), // green → mint
        ("#fa709a", "#fee140"), // rose → gold
        ("#a18cd1", "#fbc2eb"), // lavender → blush
        ("#fccb90", "#d57eeb"), // peach → violet
        ("#e0c3fc", "#8ec5fc"), // lilac → blue
        ("#f6d365", "#fda085"), // sunshine → salmon
        ("#96fbc4", "#f9f586"), // mint → lemon
        ("#ff9a9e", "#fecfef"), // blush → pink
        ("#a1c4fd", "#c2e9fb"), // periwinkle → ice
        ("#d4fc79", "#96e6a1"), // lime → sage
        ("#84fab0", "#8fd3f4"), // aqua → sky
        ("#fbc2eb", "#a6c1ee"), // cotton candy → steel blue
        ("#ff6a88", "#ff99ac"), // strawberry → light pink
        ("#ffd89b", "#19547b"), // gold → deep teal
        ("#c471f5", "#fa71cd"), // purple → magenta
        ("#48c6ef", "#6f86d6"), // ocean → slate
        ("#feada6", "#f5efef"), // nude → snow
    ];

    /// <summary>
    /// Gets the deterministic (from, to) gradient pair for a username.
    /// </summary>
    public static (string From, string To) GetGradient(string? username)
    {
        if (string.IsNullOrEmpty(username))
            return Gradients[0];

        var hash = 0;
        foreach (var c in username)
            hash = c + ((hash << 5) - hash);

        var index = (int)(((uint)hash) % Gradients.Length);
        return Gradients[index];
    }

    /// <summary>
    /// Gets a ready-to-use CSS <c>linear-gradient(...)</c> value for a username.
    /// </summary>
    public static string GetGradientCss(string? username, int angleDeg = 135)
    {
        var (from, to) = GetGradient(username);
        return $"linear-gradient({angleDeg}deg, {from}, {to})";
    }
}
