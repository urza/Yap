namespace Yap.Models.Chat;

/// <summary>
/// A pinned message, as the channel stores it and as the client receives it (MR-77, SH-48). The pin carries a copy of
/// what the panel shows (author, text, time, the media under the text, whether there is media), so the panel works for
/// a message that the startup load left in the database. Edit keeps the copy current; Delete removes the pin.
/// </summary>
public sealed record Pin(Guid MessageId, string Username, string? Text, DateTime Timestamp, bool HasMedia, PinMedia? Media, string PinnedBy, DateTime PinnedAt)
{
    public static Pin Of(ChatMessage m, string pinnedBy, PinMedia? media) =>
        new(m.Id, m.Username, string.IsNullOrEmpty(m.Content) ? null : m.Content, m.Timestamp, m.HasMedia, media, pinnedBy, DateTime.UtcNow);
}

/// <summary>
/// What a pin entry shows under its text (SH-48), resolved when the pin is made: a GIF file can move or go, and a link
/// card lives in memory for an hour, so the pin keeps its own copy of the URLs. Kind decides the shape:
/// image (Src: a picture, or a GIF that is an image file), gif (Src: a video file the panel loops; no Src when the file
/// is gone), video (Poster with a play mark: an uploaded video, or a cached link video with its Title), link (Poster and
/// Title of a link card), voice (a label). Width and Height keep a GIF's shape before it loads.
/// </summary>
public sealed record PinMedia(string Kind, string? Src = null, string? Poster = null, int? Width = null, int? Height = null, string? Title = null);
