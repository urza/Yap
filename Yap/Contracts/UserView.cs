namespace Yap.Contracts;

/// <summary>
/// Public user and profile fields exposed to chat clients without authentication credentials.
/// </summary>
public record UserView(Guid Id, string Username, string DisplayName, string? Picture, string Gradient, bool IsAdmin = false, bool IsBot = false, string? Bio = null, string? Country = null, DateTime? CreatedAt = null);
