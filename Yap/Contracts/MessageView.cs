using System.Text.Json.Serialization;
using Yap.Models;

namespace Yap.Contracts;

/// <summary>
/// Client-facing message content, media and reactions, including the operation ID used to reconcile
/// accepted sends. Never a serialized ChatMessage: that entity carries navigation fields.
/// </summary>
public record MessageView(Guid Id, Guid? OperationId, [property: JsonIgnore] UserView Author, string Content, DateTime Timestamp,
    bool IsEdited, Guid? ReplyToMessageId, ImageView[] Images, string[] Videos, int GifCount, ReactionView[] Reactions, object[]? Gifs, LinkPreview[]? Previews, ReplyView? Reply)
{
    // Authors travel once per packet in ChatUpdate.Authors; the message carries only the id.
    public Guid AuthorId => Author.Id;
}

/// <summary>
/// Minimal author and text preview of a reply target the recipient may read.
/// </summary>
public record ReplyView(Guid Id, UserView Author, string Content);

/// <summary>
/// Image variant URLs used by the timeline and full-size gallery.
/// </summary>
public record ImageView(string Medium, string Large);

/// <summary>
/// A reaction emoji and the usernames currently associated with it.
/// </summary>
public record ReactionView(string Emoji, string[] Users);
