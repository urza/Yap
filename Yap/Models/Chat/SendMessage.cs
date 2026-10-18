namespace Yap.Models.Chat;

/// <summary>What a sender hands to <c>ChatService.Send</c>. Text, media or both; the rules of IB-14 and IB-32 are applied there.</summary>
public sealed record SendMessage(Guid ChannelId, Guid UserId, string Username, string Text)
{
    /// <summary>The id the client made at send time, for the dedupe of repeated sends. Null for server-made messages.</summary>
    public Guid? ClientId { get; init; }
    public Guid? ReplyToMessageId { get; init; }
    public List<string>? ImageUrls { get; init; }
    public List<string>? VideoUrls { get; init; }
    public List<GifAttachment>? GifAttachments { get; init; }
    public VoiceAttachment? Voice { get; init; }

    /// <summary>
    /// A server-made message from a sender who is not one of the two participants: the bot writing into a
    /// person's conversation with the admin (docs/dmonly.md). Only server code sets this; the send endpoint
    /// never does, so a client cannot write into a conversation it is not part of.
    /// </summary>
    public bool System { get; init; }
}

/// <summary>Either the stored message or the reason the server refused it (GEN-02).</summary>
public sealed record SendResult(ChatMessage? Message, string? Error, int Status = 200)
{
    public static SendResult Ok(ChatMessage message) => new(message, null);
    public static SendResult Refused(string error, int status = 400) => new(null, error, status);
}
