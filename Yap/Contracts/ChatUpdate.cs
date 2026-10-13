namespace Yap.Contracts;

// Records in this folder are serialized to the browser. Property names are the wire contract
// (mirrored by wwwroot/chat-client/contracts.js); type names never reach the wire.

/// <summary>Changes to one conversation. Window is present only for a complete recent-window replacement.</summary>
public record ConversationUpdate(Guid Id, ConversationView State, MessageView[] Messages, Guid[] Removed,
    Guid[]? Window, string? BaseRevision, string Revision, bool Invalidate = false);

/// <summary>Account-bound incremental authority shared by HTTP results and the live stream.</summary>
public record ChatUpdate(int Protocol, Guid UserId, string ServerEpoch, long Sequence, SnapshotView? State,
    ConversationUpdate[] Conversations, Guid[] RemovedConversations, UserView[] Authors, string? StateRevision = null, bool Reset = false);
