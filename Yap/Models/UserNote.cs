namespace Yap.Models;

/// <summary>
/// A private note that one user writes about another user.
/// Only visible to the author (like Discord's "Note" on profiles).
/// </summary>
public class UserNote
{
    public int Id { get; set; }

    /// <summary>
    /// The user who wrote the note.
    /// </summary>
    public Guid AuthorId { get; set; }

    /// <summary>
    /// The user the note is about.
    /// </summary>
    public Guid TargetId { get; set; }

    /// <summary>
    /// The note text (max 256 characters).
    /// </summary>
    public string Text { get; set; } = "";

    private UserNote() { } // EF Core constructor

    public UserNote(Guid authorId, Guid targetId, string text)
    {
        AuthorId = authorId;
        TargetId = targetId;
        Text = text;
    }
}
