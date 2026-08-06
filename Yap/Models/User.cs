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
    /// URL to the user's profile picture (stored in /uploads/profiles/).
    /// Null if no profile picture set.
    /// </summary>
    public string? ProfilePictureUrl { get; set; }

    /// <summary>
    /// User's bio/about text (max 150 characters).
    /// </summary>
    public string? Bio { get; set; }

    /// <summary>
    /// User's location (free text, e.g. "Prague, CZ" or "United States").
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Client's IANA timezone (e.g. "Europe/Prague"). Updated on each connect.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Client's browser locale (e.g. "cs-CZ"). Updated on each connect.
    /// </summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Date/time format preset ID (e.g. "czech", "us", "european", "iso").
    /// </summary>
    public string? DateFormat { get; set; }

    /// <summary>
    /// Selected color theme ID (e.g. "ocean", "sunset"). Null = default Discord Dark.
    /// Themes are defined in wwwroot/themes.css and registered in ThemeRegistry.
    /// </summary>
    public string? Theme { get; set; }

    /// <summary>
    /// Optional passphrase for multi-device login.
    /// Stored as plaintext so the user can view/copy it in Settings.
    /// When set, the username can be claimed from any device by entering this passphrase.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Whether push notifications are muted for this user.
    /// When true, PushNotificationService skips sending (subscription stays intact).
    /// </summary>
    public bool PushMuted { get; set; }

    /// <summary>
    /// Last time the user was seen running Yap as an installed PWA (display-mode: standalone).
    /// Null if we've never detected a standalone session. Updated on connect when the client
    /// reports it is running as an installed app.
    /// </summary>
    public DateTime? PwaInstalledAt { get; set; }

    /// <summary>
    /// Whether this user has opted out of smart login (IP-based auto-login).
    /// Default false = smart login allowed. When true, passphrase is always required.
    /// </summary>
    public bool SmartLoginOptOut { get; set; }

    /// <summary>
    /// JSON-serialized list of {Ip, LastSeenUtc} this user was recently seen from
    /// (newest first, max 5). Smart login accepts an exact match within a 14-day TTL,
    /// so "log in from a network you've used recently" survives server restarts and
    /// closed browsers instead of depending on a live circuit.
    /// </summary>
    public string? KnownIps { get; set; }

    /// <summary>
    /// JSON-serialized List&lt;string&gt; of recently used emojis (most recent first, max 20).
    /// </summary>
    public string? RecentEmojis { get; set; }

    /// <summary>
    /// JSON-serialized Dictionary&lt;string, int&gt; of emoji usage counts (for quick reaction buttons).
    /// </summary>
    public string? EmojiCounts { get; set; }

    /// <summary>
    /// JSON-serialized List&lt;Guid&gt; of recently used GIF entry IDs (most recent first, max 30).
    /// </summary>
    public string? RecentGifs { get; set; }

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
