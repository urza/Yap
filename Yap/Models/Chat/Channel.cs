using Yap.Models.Accounts;

namespace Yap.Models.Chat;

/// <summary>
/// A conversation between two people. Every channel is a direct message in this build (docs/dmonly.md).
/// The room columns (Name, IsDefault, Description, SortOrder, WritePermission, HistoryLimit, SinceJoined, the
/// creator) stay on the entity so the database schema and its migrations are unchanged; nothing writes them.
/// </summary>
public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChannelType Type { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid? CreatedById { get; set; }
    public string? CreatedBy { get; set; }
    public bool IsDefault { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public ChannelPermission WritePermission { get; set; } = ChannelPermission.Everyone;
    public HistoryLimit HistoryLimit { get; set; } = HistoryLimit.Unlimited;
    public bool SinceJoined { get; set; }

    /// <summary>Room columns of the pins migration (AD-37 on main). A direct message ignores them: both people may pin.</summary>
    public PinPermission PinPermission { get; set; } = PinPermission.AdminOnly;
    public List<string> Pinners { get; set; } = new();

    /// <summary>
    /// The pinned messages, newest pin first, at most <see cref="MaxPins"/> (MR-77). A change assigns a new list and
    /// never edits this one in place: the persistence writer may be serializing the old list on its own thread.
    /// </summary>
    public List<Pin> Pins { get; set; } = new();

    public const int MaxPins = 6;

    // The two participants (by UserId)
    public Guid? Participant1Id { get; set; }
    public Guid? Participant2Id { get; set; }

    // Denormalized usernames for display and URL routing
    public string? Participant1 { get; set; }
    public string? Participant2 { get; set; }

    // Navigation properties
    public List<ChatMessage> Messages { get; set; } = new();
    public User? Creator { get; set; }
    public User? Participant1User { get; set; }
    public User? Participant2User { get; set; }
    public List<ChannelReadState> ReadStates { get; set; } = new();

    public bool IsDirectMessage => Type == ChannelType.DirectMessage;

    /// <summary>Private constructor - use the factory method CreateDM()</summary>
    private Channel() { }

    /// <summary>Factory method to create a DM channel between two users</summary>
    public static Channel CreateDM(Guid participant1Id, string participant1, Guid participant2Id, string participant2)
        => new Channel
        {
            Type = ChannelType.DirectMessage,
            Participant1Id = participant1Id,
            Participant1 = participant1,
            Participant2Id = participant2Id,
            Participant2 = participant2,
            Name = ""
        };

    /// <summary>The two people of the conversation.</summary>
    public IEnumerable<Guid> ParticipantIds => new[] { Participant1Id, Participant2Id }.OfType<Guid>();

    /// <summary>Whether a user is one of the two participants (by username).</summary>
    public bool CanAccess(string username) =>
        Participant1?.Equals(username, StringComparison.OrdinalIgnoreCase) == true ||
        Participant2?.Equals(username, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether a user is one of the two participants (by UserId).</summary>
    public bool CanAccess(Guid userId) => Participant1Id == userId || Participant2Id == userId;

    /// <summary>The other participant's username.</summary>
    public string? GetOtherParticipant(string username) =>
        Participant1?.Equals(username, StringComparison.OrdinalIgnoreCase) == true
            ? Participant2
            : Participant1;

    /// <summary>The other participant's UserId.</summary>
    public Guid? GetOtherParticipantId(Guid userId) =>
        Participant1Id == userId ? Participant2Id : Participant1Id;

    /// <summary>Whether this channel is between these two users (by UserId).</summary>
    public bool IsDMBetween(Guid userId1, Guid userId2) =>
        Type == ChannelType.DirectMessage &&
        ((Participant1Id == userId1 && Participant2Id == userId2) ||
         (Participant1Id == userId2 && Participant2Id == userId1));

    /// <summary>Whether a person may pin and unpin here (MR-77): both people of the conversation may, and the admin is
    /// always one of them in this build (docs/dmonly.md).</summary>
    public bool CanPin(Guid userId) => CanAccess(userId);
}
