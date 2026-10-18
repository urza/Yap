namespace Yap.Models.Chat;

/// <summary>
/// A pinned message, as the channel stores it and as the client receives it (MR-77, SH-48). The pin carries a copy of
/// what the panel shows (author, text, time, the first picture, whether there is media), so the panel works for a
/// message that the startup load left in the database. Edit keeps the copy current; Delete removes the pin.
/// <see cref="Thumb"/> is the first image's medium URL or the first video's poster; null for text and for GIFs.
/// </summary>
public sealed record Pin(Guid MessageId, string Username, string? Text, DateTime Timestamp, bool HasMedia, string? Thumb, string PinnedBy, DateTime PinnedAt)
{
    public static Pin Of(ChatMessage m, string pinnedBy, string? thumb) =>
        new(m.Id, m.Username, string.IsNullOrEmpty(m.Content) ? null : m.Content, m.Timestamp, m.HasMedia, thumb, pinnedBy, DateTime.UtcNow);
}
