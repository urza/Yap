namespace Yap.Models;

/// <summary>
/// Per-user favorited GIF. Composite primary key on (UserId, GifEntryId) mirroring ChannelReadState.
/// </summary>
public class FavoriteGif
{
    public Guid UserId { get; set; }
    public Guid GifEntryId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional 1-level folder this favorite is filed under. A property of the favorite
    /// relationship, not the GIF — the same entry can live in different folders for different
    /// users. Null = unsorted.
    /// </summary>
    public string? Folder { get; set; }

    public User? User { get; set; }
    public GifEntry? GifEntry { get; set; }

    private FavoriteGif() { } // EF Core

    public FavoriteGif(Guid userId, Guid gifEntryId)
    {
        UserId = userId;
        GifEntryId = gifEntryId;
    }
}
