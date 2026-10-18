namespace Yap.Models.Chat;

/// <summary>
/// A pinned message, as the channel stores it and as the client receives it (MR-76, TL-36). The pin carries a copy of
/// what the bar shows (author, text, time, whether there is media), so the bar works for a message that the startup
/// load left in the database. Edit keeps the copy current; Delete removes the pin.
/// </summary>
public sealed record Pin(Guid MessageId, string Username, string? Text, DateTime Timestamp, bool HasMedia, string PinnedBy, DateTime PinnedAt)
{
    public static Pin Of(ChatMessage m, string pinnedBy) =>
        new(m.Id, m.Username, string.IsNullOrEmpty(m.Content) ? null : m.Content, m.Timestamp, m.HasMedia, pinnedBy, DateTime.UtcNow);
}
