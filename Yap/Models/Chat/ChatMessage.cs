using Yap.Models.Accounts;

namespace Yap.Models.Chat;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }

    /// <summary>
    /// The user who sent this message (foreign key to User).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Denormalized username for display (avoids joins for common reads).
    /// </summary>
    public string Username { get; set; } = "";

    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public List<string> ImageUrls { get; set; } = new();
    public List<string> VideoUrls { get; set; } = new();
    public List<GifAttachment> GifAttachments { get; set; } = new();
    /// <summary>A voice message. Set, the message carries nothing else (IB-70).</summary>
    public VoiceAttachment? Voice { get; set; }
    public bool IsEdited { get; set; }
    public Guid? ReplyToMessageId { get; set; }

    /// <summary>
    /// The id the client made at send time. The server answers a repeated send with the same ClientId with the
    /// existing message, so a retry after a lost response never duplicates (offline design notes, "Server side").
    /// Null for messages from before the rewrite and for messages the server creates itself.
    /// </summary>
    public Guid? ClientId { get; set; }

    // Navigation properties
    public Channel Channel { get; set; } = null!;
    public User User { get; set; } = null!;
    public List<Reaction> Reactions { get; set; } = new();

    public bool HasImages => ImageUrls.Count > 0;
    public bool HasVideos => VideoUrls.Count > 0;
    public bool HasGifs => GifAttachments.Count > 0;
    public bool HasVoice => Voice is not null;
    public bool HasMedia => HasImages || HasVideos || HasGifs || HasVoice;

    private ChatMessage() { } // EF Core constructor

    public ChatMessage(Guid channelId, Guid userId, string username, string content, DateTime timestamp, List<string>? imageUrls = null, Guid? replyToMessageId = null, List<string>? videoUrls = null, List<GifAttachment>? gifAttachments = null, VoiceAttachment? voice = null)
    {
        ChannelId = channelId;
        UserId = userId;
        Username = username;
        Content = content;
        Timestamp = timestamp;
        ImageUrls = imageUrls ?? new();
        VideoUrls = videoUrls ?? new();
        GifAttachments = gifAttachments ?? new();
        Voice = voice;
        ReplyToMessageId = replyToMessageId;
        Reactions = new();
    }
}
