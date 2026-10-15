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
public sealed class MessageMapper(ChatService chat, GifService gifs, UserService users, LinkPreviewService previews, LinkPreviewSettingsService previewSettings, MediaCacheService mediaCache)
{
    /// <summary>The wire shape for one reader. The reader matters for the quote of a reply: a room with a history
    /// cutoff hides the original from some readers (TL-05), and their quote says "unavailable" instead of carrying
    /// the text. Null stands for a reader who may read the whole conversation: a direct message or an unrestricted
    /// room, where one shape serves everyone.</summary>
    public MessageDto From(ChatMessage m, Guid? viewerId) => new(
        m.Id, m.ChannelId, m.ClientId, m.Username, m.Content, m.Timestamp, m.IsEdited,
        ReplyTo(m, viewerId),
        m.ImageUrls.Select(url => new ImageDto(ImageService.GetMediumUrl(url), ImageService.GetLargeUrl(url))).ToList(),
        m.VideoUrls.Select(url => new VideoDto(VideoService.GetPosterUrl(url), VideoService.GetCompressedUrl(url))).ToList(),
        m.GifAttachments.Select(Gif).ToList(),
        Reactions(m),
        Previews(m));

    /// <summary>Reactions grouped by emoji, in the order each emoji was first used (MR-38); users in reaction order (MR-39).</summary>
    public IReadOnlyList<ReactionGroupDto> Reactions(ChatMessage m)
    {
        lock (m.Reactions)
            return m.Reactions.GroupBy(r => r.Emoji).Select(g => new ReactionGroupDto(g.Key, g.Select(r => r.Username).ToList())).ToList();
    }

    /// <summary>The cards of a text message: one per link with a title or a description, at most five (MR-19, MR-20).</summary>
    public IReadOnlyList<PreviewDto> Previews(ChatMessage m)
    {
        if (!previewSettings.Enabled || m.HasMedia) return [];
        var result = new List<PreviewDto>();
        foreach (var url in LinkPreviewService.ExtractUrls(m.Content).Take(LinkPreviewService.MaxLinksPerMessage))
        {
            var p = previews.GetCachedPreview(url);
            // The file outlives the scrape: it is on disk across restarts, the scrape is in memory for an hour.
            if (p?.CachedMediaUrl is null && mediaCache.GetOrLoadCachedMedia(url) is { } file) p = previews.AttachMedia(url, file);
            if (p is null || (!p.HasContent && p.CachedMediaUrl is null)) continue;
            var media = p.CachedMediaUrl is null ? null
                : new PreviewMediaDto(p.MediaType?.ToString().ToLowerInvariant() ?? "video", p.CachedMediaUrl, p.CachedPosterUrl, p.MediaWidth, p.MediaHeight, p.MediaDurationSeconds);
            result.Add(new PreviewDto(url, p.SiteName, p.Title, Cut(p.Description, 300), p.ImageUrl, media));
        }
        return result;
    }

    private ReplyToDto? ReplyTo(ChatMessage m, Guid? viewerId)
    {
        if (m.ReplyToMessageId is not { } id) return null;
        var original = chat.GetMessage(id);
        // Gone, not in this conversation, or before this reader's history cutoff: no text and no name. The send
        // path refuses the second case; stored rows from before that rule are mapped here too, and a quote must
        // never carry another conversation's text (GEN-04) or a past the reader may not see (TL-05).
        if (original is null || original.ChannelId != m.ChannelId || (viewerId is { } viewer && !chat.MayRead(viewer, original)))
            return new ReplyToDto(id, null, null, false, Unavailable: true);   // MR-35
        return new ReplyToDto(id, original.Username, original.HasMedia ? null : original.Content, original.HasMedia, Unavailable: false);
    }

    // A GIF file lives in one of six places. The row prefers an image file, then the local videos, then the
    // provider's copies. The attachment keeps its own size for rows that were sent before the entry knew it.
    private GifDto Gif(GifAttachment a)
    {
        var e = gifs.GetEntry(a.GifEntryId);
        if (e is null || e.DeletedAt is not null) return new GifDto(a.GifEntryId, null, false, a.Width, a.Height, null);
        var src = e.GifUrl ?? e.RemoteGifUrl ?? e.Mp4Url ?? e.WebmUrl ?? e.RemoteMp4Url ?? e.RemoteWebmUrl;
        var isImage = src is not null && (src == e.GifUrl || src == e.RemoteGifUrl);
        var uploader = e.UploadedByUserId is { } uid ? users.GetById(uid)?.Username : null;
        var (w, h) = a.Width > 0 && a.Height > 0 ? (a.Width, a.Height) : (e.Width, e.Height);
        return new GifDto(e.Id, src, isImage, w, h, uploader);
    }

    private static string? Cut(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
