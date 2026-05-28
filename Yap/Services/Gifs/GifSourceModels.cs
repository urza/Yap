namespace Yap.Services.Gifs;

/// <summary>
/// A paginated batch of GIFs from a provider.
/// </summary>
public record GifSearchResult(List<GifSearchItem> Items, string? NextCursor);

/// <summary>
/// One GIF item as returned by a provider. Carries multiple format URLs because providers
/// (and individual items within a provider's catalog) vary in which formats they ship.
/// Consumers pick the best available format based on a preference list.
/// </summary>
public record GifSearchItem(
    string SourceId,
    string Title,
    int Width,
    int Height,
    List<MediaFormat> Formats,         // Full-quality variants for sending (mp4, webm, gif, etc.)
    List<MediaFormat> PreviewFormats   // Small variants for picker grid (tinymp4, tinygif, etc.)
);

public record MediaFormat(
    string Url,
    string ContentType,
    int Width,
    int Height,
    long SizeBytes
);

public record GifCategory(string SearchTerm, string Name, string ImageUrl);
