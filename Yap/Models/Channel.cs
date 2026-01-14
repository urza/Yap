namespace Yap.Models;

public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChannelType Type { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public bool IsDefault { get; set; }

    // DM-specific: the two participants
    public string? Participant1 { get; set; }
    public string? Participant2 { get; set; }

    // Navigation property (not loaded by default)
    public List<ChatMessage> Messages { get; set; } = new();

    public bool IsDirectMessage => Type == ChannelType.DirectMessage;

    /// <summary>
    /// Private constructor - use factory methods CreateRoom() or CreateDM()
    /// </summary>
    private Channel() { }

    /// <summary>
    /// Factory method to create a room channel
    /// </summary>
    public static Channel CreateRoom(string name, string? createdBy = null, bool isDefault = false)
        => new Channel
        {
            Type = ChannelType.Room,
            Name = name,
            CreatedBy = createdBy,
            IsDefault = isDefault
        };

    /// <summary>
    /// Factory method to create a DM channel between two users
    /// </summary>
    public static Channel CreateDM(string participant1, string participant2)
        => new Channel
        {
            Type = ChannelType.DirectMessage,
            Participant1 = participant1,
            Participant2 = participant2,
            Name = ""
        };

    /// <summary>
    /// Checks if a user can access this channel
    /// </summary>
    public bool CanAccess(string username) =>
        Type == ChannelType.Room ||
        Participant1?.Equals(username, StringComparison.OrdinalIgnoreCase) == true ||
        Participant2?.Equals(username, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// For DMs: get the other participant
    /// </summary>
    public string? GetOtherParticipant(string username) =>
        Participant1?.Equals(username, StringComparison.OrdinalIgnoreCase) == true
            ? Participant2
            : Participant1;

    /// <summary>
    /// For DMs: check if this channel is between these two users
    /// </summary>
    public bool IsDMBetween(string user1, string user2) =>
        Type == ChannelType.DirectMessage &&
        ((Participant1?.Equals(user1, StringComparison.OrdinalIgnoreCase) == true &&
          Participant2?.Equals(user2, StringComparison.OrdinalIgnoreCase) == true) ||
         (Participant1?.Equals(user2, StringComparison.OrdinalIgnoreCase) == true &&
          Participant2?.Equals(user1, StringComparison.OrdinalIgnoreCase) == true));
}
