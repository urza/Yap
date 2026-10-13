namespace Yap.Contracts;

/// <summary>
/// The account header of a bootstrap: identity, preferences, limits and the people list. Travels as
/// ChatUpdate.State; the client calls it ChatSnapshot.
/// </summary>
public record SnapshotView(int Protocol, string Revision, string ServerEpoch, long Sequence, UserView User, bool IsAdmin, string Theme,
    int? FontSize, string? TimeZone, string? DateFormat, string ProjectName, int RecentMessageLimit,
    bool CanSend, int MaxTextLength, UserView[] People, ConversationView[] Conversations, int MaxOperationsPerBatch, int MaxBatchBytes,
    int MaxFilesPerMessage, string[] AllowedExtensions, long MaxUploadBytes, int HistoryPageSize,
    int HistoryMaxMessages, int ReadBatch, int TypingTimeoutMs, int AwayAfterMs, object? DateSettings = null);
