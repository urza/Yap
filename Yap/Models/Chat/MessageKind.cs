namespace Yap.Models.Chat;

/// <summary>
/// What a row of a conversation is. A system line is stored like a message, so it stays in the history and loads
/// with it, but it counts as unread for nobody and notifies nobody (MR-80, NT-02). Stored as an int: a row from
/// before the column is <see cref="Normal"/>.
/// </summary>
public enum MessageKind
{
    /// <summary>A message a person or a bot sent.</summary>
    Normal = 0,

    /// <summary>"{name} pinned a message" (MR-80). The author is the pinner; <see cref="ChatMessage.ReplyToMessageId"/>
    /// names the pinned message; the content is empty, the client writes the text.</summary>
    Pin = 1,
}
