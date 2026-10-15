namespace Yap.Models.Accounts;

/// <summary>An account as everyone sees it: the users directory of the bootstrap and the <c>profile</c> event (SH-35, GEN-19).</summary>
public sealed record UserDto(string Username, string? DisplayName, string? AvatarUrl, string? Bio, string? Location, DateTime CreatedAt, bool IsAdmin, bool IsBot)
{
    public static UserDto From(User u, bool isAdmin, bool isBot) =>
        new(u.Username, u.DisplayName, u.ProfilePictureUrl, u.Bio, u.Country, u.CreatedAt, isAdmin, isBot);
}

/// <summary>The signed-in account, for the bootstrap's <c>user</c>. Everything Settings edits is here (ST-04 to ST-20).</summary>
public sealed record MeDto(
    Guid Id,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string? Bio,
    string? Location,
    DateTime CreatedAt,
    bool IsAdmin,
    string? Theme,
    int? FontSize,
    string? TimeZone,
    string? Locale,
    string? DateFormat,
    UserStatus Status,
    bool SmartLoginOptOut,
    bool HasPassphrase,
    bool HasLink)
{
    public static MeDto From(User u, bool isAdmin, bool hasLink) =>
        new(u.Id, u.Username, u.DisplayName, u.ProfilePictureUrl, u.Bio, u.Country, u.CreatedAt, isAdmin,
            u.Theme, u.FontSize, u.TimeZone, u.Locale, u.DateFormat, u.Status, u.SmartLoginOptOut,
            HasPassphrase: !string.IsNullOrEmpty(u.Password), HasLink: hasLink);
}
