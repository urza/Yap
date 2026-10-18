namespace Yap.Models.Chat;

/// <summary>What <c>openRoom</c> and <c>openDm</c> return: the tail of the conversation, who types in it, and its pinned messages (chat-hub.md, TL-36).</summary>
public sealed record Conversation(Guid ChannelId, IReadOnlyList<MessageDto> Messages, bool HasMore, bool Restricted, IReadOnlyList<string> Typists, IReadOnlyList<Pin> Pins);

/// <summary>What <c>older</c> returns (TL-06).</summary>
public sealed record Page(IReadOnlyList<MessageDto> Messages, bool HasMore);

/// <summary>A refusal from a hub method, with the reason the user reads (GEN-02). A hub method returns null on success.</summary>
public sealed record HubError(string Error);

/// <summary>A room as the client sees it (SH-22, AD-30 to AD-32). Read-only is derived on the client from AdminOnly and the person's admin flag.
/// IsLobby names the room that /lobby opens (SH-43); the sidebar order is the admin's and says nothing about identity (AD-33).
/// PinPermission and Pinners let the client decide whether to offer Pin (MR-76, AD-37); the server decides again on the call.</summary>
public sealed record RoomDto(Guid Id, string Name, string? Description, int SortOrder, bool AdminOnly, HistoryLimit HistoryLimit, bool SinceJoined, bool IsLobby,
    PinPermission PinPermission, IReadOnlyList<string> Pinners)
{
    public static RoomDto From(Channel room) =>
        new(room.Id, room.Name, room.Description, room.SortOrder, room.WritePermission == ChannelPermission.AdminOnly, room.HistoryLimit, room.SinceJoined, room.IsDefault,
            room.PinPermission, room.Pinners);
}

/// <summary>A direct message in the sidebar: the partner and when it last moved (SH-29, SH-30).</summary>
public sealed record DmDto(Guid ChannelId, string Partner, DateTime LastMessageAt);
