namespace Yap.Contracts;

/// <summary>
/// Transient presence and selected-conversation typing state for one connected client. The hub
/// diffs consecutive views and sends only the changed fields.
/// </summary>
public record PresenceView(string Status, string ChosenStatus, int OnlineCount, PresencePerson[] Users, Guid? ChannelId, string[] Typing);

/// <summary>
/// A username and its current effective presence status for the live user list.
/// </summary>
public record PresencePerson(string Username, string Status);
