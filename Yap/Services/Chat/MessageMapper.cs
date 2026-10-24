using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Gifs;
using Yap.Services.Media;

namespace Yap.Services.Chat;

/// <summary>
/// The one mapping from a stored message to its wire shape. The broadcaster, the hub and the send route all
/// use it, so every reader sees the same message. It resolves what the row needs from other services: image
/// sizes, video posters, GIF files and uploaders, the quoted original of a reply, and the link cards.
/// </summary>
public sealed class MessageMapper(ChatService chat, GifService gifs, UserService users, MediaSizes sizes)
{
    /// <summary>The wire shape for one reader. The reader matters for the quote of a reply: a room with a history
    /// cutoff hides the original from some readers (TL-05), and their quote says "unavailable" instead of carrying
    /// the text. Null stands for a reader who may read the whole conversation: a direct message or an unrestricted
    /// room, where one shape serves everyone.</summary>
    public MessageDto From(ChatMessage m, Guid? viewerId) => new(
        m.Id, m.ChannelId, m.ClientId, m.Username, m.Content, m.Timestamp, m.IsEdited,
        Kind(m.Kind),
        ReplyTo(m, viewerId),
        m.ImageUrls.Select(Image).ToList(),
        m.VideoUrls.Select(Video).ToList(),
        m.GifAttachments.Select(Gif).ToList(),
        m.Voice is { } voice ? new VoiceDto(voice.Url, voice.DurationMs, voice.Peaks) : null,
        Reactions(m),
        chat.Previews(m));

    /// <summary>The wire name of a system line; null for a message (MR-80, <see cref="MessageDto.Kind"/>).</summary>
    private static string? Kind(MessageKind kind) => kind switch { MessageKind.Pin => "pin", _ => null };

    /// <summary>The row's picture with the size of the file it shows, so the row has its box before the file loads.</summary>
    private ImageDto Image(string url)
    {
        var medium = ImageService.GetMediumUrl(url);
        var size = sizes.Of(medium);
        return new ImageDto(url, medium, ImageService.GetLargeUrl(url), size?.Width, size?.Height);
    }

    private VideoDto Video(string url)
    {
        var poster = VideoService.GetPosterUrl(url);
        var size = sizes.Of(poster);
        return new VideoDto(poster, VideoService.GetCompressedUrl(url), size?.Width, size?.Height);
    }

    /// <summary>Reactions grouped by emoji, in the order each emoji was first used (MR-38); users in reaction order (MR-39).</summary>
    public IReadOnlyList<ReactionGroupDto> Reactions(ChatMessage m)
    {
        lock (m.Reactions)
            return m.Reactions.GroupBy(r => r.Emoji).Select(g => new ReactionGroupDto(g.Key, g.Select(r => r.Username).ToList())).ToList();
    }

    private ReplyToDto? ReplyTo(ChatMessage m, Guid? viewerId)
    {
        if (m.ReplyToMessageId is not { } id) return null;
        var original = chat.GetMessage(id);
        // Gone, not in this conversation, or before this reader's history cutoff: no text and no name. The send
        // path refuses the second case; stored rows from before that rule are mapped here too, and a quote must
        // never carry another conversation's text (GEN-04) or a past the reader may not see (TL-05).
        if (original is null || original.ChannelId != m.ChannelId || (viewerId is { } viewer && !chat.MayRead(viewer, original)))
            return new ReplyToDto(id, null, null, false, false, Unavailable: true);   // MR-35
        return new ReplyToDto(id, original.Username, string.IsNullOrEmpty(original.Content) ? null : original.Content, original.HasMedia, original.HasVoice, Unavailable: false);   // a caption quotes as text (MR-32)
    }

    // GifEntry.Src picks the file (one of six places). The attachment keeps its own size for rows that were sent
    // before the entry knew it.
    private GifDto Gif(GifAttachment a)
    {
        var e = gifs.GetEntry(a.GifEntryId);
        if (e is null || e.DeletedAt is not null) return new GifDto(a.GifEntryId, null, false, a.Width, a.Height, null);
        var src = e.Src;
        var isImage = e.SrcIsImage;
        var uploader = e.UploadedByUserId is { } uid ? users.GetById(uid)?.Username : null;
        var (w, h) = a.Width > 0 && a.Height > 0 ? (a.Width, a.Height) : (e.Width, e.Height);
        return new GifDto(e.Id, src, isImage, w, h, uploader);
    }
}
