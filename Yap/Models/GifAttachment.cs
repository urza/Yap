namespace Yap.Models;

/// <summary>
/// Embedded in ChatMessage.GifAttachments JSON column. Carries just enough to render the GIF
/// in chat history independent of the live GifEntry. Width/Height are duplicated for layout stability
/// even if the underlying GifEntry is missing (defensive fallback).
///
/// The actual playable URLs are looked up at render time via the GifService by GifEntryId — this
/// lets background normalization (Tenor URL → local file, MP4 → WebM transcode) update what gets
/// served without rewriting historical messages.
/// </summary>
public class GifAttachment
{
    public Guid GifEntryId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public GifAttachment() { }

    public GifAttachment(Guid gifEntryId, int width, int height)
    {
        GifEntryId = gifEntryId;
        Width = width;
        Height = height;
    }
}
