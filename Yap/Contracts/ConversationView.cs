namespace Yap.Contracts;

/// <summary>
/// An authorized recent message window with navigation, permission, history and observed-read
/// metadata. Messages is empty when only the metadata travels.
/// </summary>
public record ConversationView(Guid Id, string Kind, string Name, string? Description, bool IsDefault, string Path,
    bool CanWrite, bool HasMore, int Unread, bool Muted, MessageView[] Messages, long Received, long ReadThrough, bool MuteBell, long ContentVersion = 0, bool HistoryLimited = false, long HistoryVersion = 0);
