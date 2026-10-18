namespace Yap.Models.Chat;

/// <summary>
/// A message as the client receives it, in the hub's <c>message</c> event, in a conversation tail and in the
/// answer to a send. Text is plain text: the client renders links and emoji (sketch, principle 6). Display
/// names and avatars are not here; the client resolves <see cref="Username"/> in its users directory.
/// </summary>
public sealed record MessageDto(
    Guid Id,
    Guid ChannelId,
    Guid? ClientId,
    string Username,
    string Text,
    DateTime Timestamp,
    bool Edited,
    ReplyToDto? ReplyTo,
    IReadOnlyList<ImageDto> Images,
    IReadOnlyList<VideoDto> Videos,
    IReadOnlyList<GifDto> Gifs,
    VoiceDto? Voice,
    IReadOnlyList<ReactionGroupDto> Reactions,
    IReadOnlyList<PreviewDto> Previews);

/// <summary>The quoted original of a reply (MR-32). <see cref="Unavailable"/> when the original was deleted (MR-35).
/// <see cref="Voice"/> names a voice message, which the quote says in words instead of "attachment".</summary>
public sealed record ReplyToDto(Guid Id, string? Username, string? Text, bool HasMedia, bool Voice, bool Unavailable);

/// <summary>A voice message (MR-76): the file, its length, and the bars of its waveform as the recorder measured them
/// (one base-36 digit per bar, see <see cref="VoiceAttachment"/>).</summary>
public sealed record VoiceDto(string Src, int DurationMs, string Peaks);

/// <summary>Two sizes of one uploaded image: the row shows the medium one, the viewer the large one (MR-24, TL-22).</summary>
public sealed record ImageDto(string Medium, string Large);

public sealed record VideoDto(string Poster, string Src);

/// <summary>One GIF in a message. <see cref="IsImage"/> says whether <see cref="Src"/> is an image or a video file.
/// <see cref="Uploader"/> is the username for "via {name}" (MR-31); null for provider GIFs. Null <see cref="Src"/>
/// means the GIF no longer exists (MR-28, "GIF unavailable").</summary>
public sealed record GifDto(Guid EntryId, string? Src, bool IsImage, int Width, int Height, string? Uploader);

/// <summary>One emoji and everyone who reacted with it, in reaction order (MR-38, MR-39).</summary>
public sealed record ReactionGroupDto(string Emoji, IReadOnlyList<string> Users);

/// <summary>A link card (MR-20), or a player when the server cached the page's media (MR-23).</summary>
public sealed record PreviewDto(string Url, string? SiteName, string? Title, string? Description, string? ImageUrl, PreviewMediaDto? Media);

public sealed record PreviewMediaDto(string Type, string Src, string? Poster, int? Width, int? Height, int? DurationSeconds);
