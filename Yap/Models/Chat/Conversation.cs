namespace Yap.Models.Chat;

/// <summary>What <c>openDm</c> returns: the tail of the conversation and who types in it (chat-hub.md).</summary>
public sealed record Conversation(Guid ChannelId, IReadOnlyList<MessageDto> Messages, bool HasMore, IReadOnlyList<string> Typists);

/// <summary>What <c>older</c> returns (TL-06).</summary>
public sealed record Page(IReadOnlyList<MessageDto> Messages, bool HasMore);

/// <summary>A refusal from a hub method, with the reason the user reads (GEN-02). A hub method returns null on success.</summary>
public sealed record HubError(string Error);

/// <summary>A direct message with at least one message: the partner and when it last moved (SH-29, SH-30).</summary>
public sealed record DmDto(Guid ChannelId, string Partner, DateTime LastMessageAt);
