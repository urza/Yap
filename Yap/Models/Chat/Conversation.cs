namespace Yap.Models.Chat;

/// <summary>What <c>openDm</c> returns: the tail of the conversation, who types in it, and its pinned messages (chat-hub.md, SH-48).</summary>
public sealed record Conversation(Guid ChannelId, IReadOnlyList<MessageDto> Messages, bool HasMore, IReadOnlyList<string> Typists, IReadOnlyList<Pin> Pins);

/// <summary>What <c>older</c> returns (TL-06).</summary>
public sealed record Page(IReadOnlyList<MessageDto> Messages, bool HasMore);

/// <summary>What <c>around</c> returns (SH-49, MR-34, TL-41): the messages around the target, whether older ones exist
/// (HasMore) and whether newer ones exist (HasNewer). Error is the refusal the reader sees (GEN-02); the list is empty then.
/// The error travels in the body because hub.invoke turns every exception into "Something went wrong".</summary>
public sealed record Window(IReadOnlyList<MessageDto> Messages, bool HasMore, bool HasNewer, string? Error = null);

/// <summary>A refusal from a hub method, with the reason the user reads (GEN-02). A hub method returns null on success.</summary>
public sealed record HubError(string Error);

/// <summary>A direct message with at least one message: the partner and when it last moved (SH-29, SH-30).</summary>
public sealed record DmDto(Guid ChannelId, string Partner, DateTime LastMessageAt);
