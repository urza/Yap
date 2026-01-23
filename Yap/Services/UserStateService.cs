using Microsoft.AspNetCore.Components;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Scoped service that holds the current user's identity and session state.
///
/// .NET 10 PERSISTENT STATE:
/// Properties marked with [PersistentState] are automatically serialized when
/// the circuit is evicted (user disconnected too long) and restored when the
/// user reconnects via Blazor.resumeCircuit().
///
/// This means:
/// - User closes laptop for 2 hours → circuit evicted
/// - User opens laptop → Blazor.resumeCircuit() called
/// - Username and SessionId are automatically restored
/// - User is still "logged in" without re-entering credentials
///
/// Requirements:
/// - Service must be registered with RegisterPersistentService<T>() in Program.cs
/// - Properties must be JSON-serializable
/// - Only works with InteractiveServer render mode
/// </summary>
public class UserStateService
{
    /// <summary>
    /// The user's unique ID (primary identifier). Persisted across circuit evictions.
    /// </summary>
    [PersistentState]
    public Guid? UserId { get; set; }

    /// <summary>
    /// The user's username (URL-safe, unique). Persisted across circuit evictions.
    /// </summary>
    [PersistentState]
    public string? Username { get; set; }

    /// <summary>
    /// Optional display name with relaxed rules. Persisted across circuit evictions.
    /// </summary>
    [PersistentState]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Unique session identifier. Used by ChatService to track online users
    /// and clean up when user disconnects. Persisted across circuit evictions.
    /// Note: This is NOT the Blazor circuit ID - it's a custom GUID we generate.
    /// </summary>
    [PersistentState]
    public string? SessionId { get; set; }

    /// <summary>
    /// User's presence status (Online, Away, Invisible). Persisted across circuit evictions.
    /// </summary>
    [PersistentState]
    public UserStatus Status { get; set; } = UserStatus.Online;

    /// <summary>
    /// Whether the user is on a mobile device (detected from User-Agent).
    /// Set once during initial HTTP request, then persisted for the session.
    /// </summary>
    [PersistentState]
    public bool? IsMobile { get; set; }

    /// <summary>
    /// Gets the name to display in the UI.
    /// Returns DisplayName if set, otherwise Username.
    /// </summary>
    public string EffectiveDisplayName => DisplayName ?? Username ?? "";

    /// <summary>
    /// True if the user has a valid UserId (logged in).
    /// </summary>
    public bool IsLoggedIn => UserId.HasValue;

    /// <summary>
    /// True if the user has been added to the chat (joined).
    /// </summary>
    public bool IsJoinedChat => !string.IsNullOrEmpty(SessionId);
}
