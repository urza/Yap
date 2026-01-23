namespace Yap.Models;

/// <summary>
/// Represents a persistent user with GUID-based identification.
/// Users are authenticated via a secret token stored in localStorage.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Unique, URL-safe username (used in DM routes like /dm/{username}).
    /// Lowercase a-z, 0-9, underscore, period only.
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Optional display name with relaxed rules.
    /// If null, Username is used for display.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Secret token for localStorage authentication.
    /// Never exposed to other users.
    /// </summary>
    public string Token { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time the user was active (updated on disconnect).
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// Whether this user is the admin (first user or explicitly set).
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Gets the name to display in the UI.
    /// Returns DisplayName if set, otherwise Username.
    /// </summary>
    public string EffectiveDisplayName => DisplayName ?? Username;

    private User() { } // EF Core constructor

    public User(string username, string token)
    {
        Username = username;
        Token = token;
    }
}
