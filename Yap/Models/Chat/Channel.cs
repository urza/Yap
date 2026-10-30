using Yap.Models.Accounts;

namespace Yap.Models.Chat;

public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChannelType Type { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created this channel (for rooms).
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// Denormalized username of creator (for display).
    /// </summary>
    public string? CreatedBy { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>
    /// Optional description shown at the beginning of channel history.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Sort order for rooms in the sidebar. Lower values appear first.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Who can write messages in this channel.
    /// </summary>
    public ChannelPermission WritePermission { get; set; } = ChannelPermission.Everyone;

    /// <summary>
    /// How far back non-admin users can scroll through message history.
    /// Admin always sees full history. Value in hours.
    /// </summary>
    public HistoryLimit HistoryLimit { get; set; } = HistoryLimit.Unlimited;

    /// <summary>Who may pin and unpin messages in this room (AD-37). A direct message ignores it: both people may.</summary>
    public PinPermission PinPermission { get; set; } = PinPermission.AdminOnly;

    /// <summary>
    /// The usernames that may pin when <see cref="PinPermission"/> is Selected. Usernames, like the participants of a
    /// direct message: a username never changes, and the client knows people by username only.
    /// </summary>
    public List<string> Pinners { get; set; } = new();

    /// <summary>
    /// The pinned messages, newest pin first, at most <see cref="MaxPins"/> (MR-77). A change assigns a new list and
    /// never edits this one in place: the persistence writer may be serializing the old list on its own thread.
    /// </summary>
    public List<Pin> Pins { get; set; } = new();

    public const int MaxPins = 6;

    // DM-specific: the two participants (by UserId)
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

    /// <summary>
    /// Private constructor - use factory methods CreateRoom() or CreateDM()
    /// </summary>
    private Channel() { }

    /// <summary>
    /// Factory method to create a room channel
    /// </summary>
    public static Channel CreateRoom(string name, Guid? createdById = null, string? createdBy = null, bool isDefault = false,
        string? description = null, int sortOrder = 0, ChannelPermission writePermission = ChannelPermission.Everyone,
        HistoryLimit historyLimit = HistoryLimit.Unlimited, bool sinceJoined = false,
        PinPermission pinPermission = PinPermission.AdminOnly, List<string>? pinners = null)
        => new Channel
        {
            Type = ChannelType.Room,
            Name = name,
            CreatedById = createdById,
            CreatedBy = createdBy,
            IsDefault = isDefault,
            Description = description,
            SortOrder = sortOrder,
            WritePermission = writePermission,
            HistoryLimit = historyLimit,
            SinceJoined = sinceJoined,
            PinPermission = pinPermission,
            Pinners = pinners ?? new()
        };

    /// <summary>
    /// Factory method to create a DM channel between two users
    /// </summary>
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

    /// <summary>
    /// Checks if a user can access this channel (by username for compatibility)
    /// </summary>
    public bool CanAccess(string username) =>
        Type == ChannelType.Room ||
        Participant1?.Equals(username, StringComparison.OrdinalIgnoreCase) == true ||
        Participant2?.Equals(username, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Checks if a user can access this channel (by UserId)
    /// </summary>
    public bool CanAccess(Guid userId) =>
        Type == ChannelType.Room ||
        Participant1Id == userId ||
        Participant2Id == userId;

    /// <summary>
    /// For DMs: get the other participant's username
    /// </summary>
    public string? GetOtherParticipant(string username) =>
        Participant1?.Equals(username, StringComparison.OrdinalIgnoreCase) == true
            ? Participant2
            : Participant1;

    /// <summary>
    /// For DMs: get the other participant's UserId
    /// </summary>
    public Guid? GetOtherParticipantId(Guid userId) =>
        Participant1Id == userId ? Participant2Id : Participant1Id;

    /// <summary>
    /// For DMs: check if this channel is between these two users (by UserId)
    /// </summary>
    public bool IsDMBetween(Guid userId1, Guid userId2) =>
        Type == ChannelType.DirectMessage &&
        ((Participant1Id == userId1 && Participant2Id == userId2) ||
         (Participant1Id == userId2 && Participant2Id == userId1));

    /// <summary>
    /// Checks if a user can write messages in this channel.
    /// DMs are always writable. Rooms check WritePermission.
    /// </summary>
    public bool CanWrite(Guid userId, bool isAdmin) =>
        Type == ChannelType.DirectMessage ||
        WritePermission == ChannelPermission.Everyone ||
        isAdmin;

    /// <summary>
    /// Whether a person may pin and unpin in this conversation (MR-77). Both people of a direct message may. In a room,
    /// the admin always may; the others follow <see cref="PinPermission"/>.
    /// </summary>
    public bool CanPin(Guid userId, string username, bool isAdmin) => Type switch
    {
        ChannelType.DirectMessage => CanAccess(userId),
        _ => isAdmin || PinPermission switch
        {
            PinPermission.Everyone => true,
            PinPermission.Selected => Pinners.Contains(username, StringComparer.OrdinalIgnoreCase),
            _ => false,
        },
    };

    /// <summary>
    /// Returns the UTC cutoff timestamp for history visibility, or null if unlimited.
    /// </summary>
    /// <summary>
    /// Whether non-admin users can only see messages posted after they joined.
    /// Combined with HistoryLimit — the more restrictive cutoff wins.
    /// </summary>
    public bool SinceJoined { get; set; }

    public DateTime? GetHistoryCutoff() =>
        HistoryLimit == HistoryLimit.Unlimited ? null : DateTime.UtcNow.AddHours(-(int)HistoryLimit);
}
