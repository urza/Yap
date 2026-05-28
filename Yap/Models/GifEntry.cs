namespace Yap.Models;

/// <summary>
/// A short looping media item (concept of "GIF") sourced either from an external provider (Tenor)
/// or uploaded directly by a user. May exist as MP4, WebM, original GIF, or any combination.
///
/// Sourced entries (SourceProviderId != null) start with only Remote URLs set and get Local URLs
/// filled in by a background download + transcode pipeline. Custom uploads (SourceProviderId == null)
/// have Local URLs set immediately and Remote URLs left null.
///
/// At least one of Mp4Url / WebmUrl / GifUrl / RemoteMp4Url / RemoteWebmUrl / RemoteGifUrl is non-null.
/// </summary>
public class GifEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Provider that supplied this GIF (e.g. "tenor"). Null for custom uploads.</summary>
    public string? SourceProviderId { get; set; }

    /// <summary>Provider's GIF identifier. Null for custom uploads. Unique-when-not-null with SourceProviderId.</summary>
    public string? SourceId { get; set; }

    // Local URLs — served from disk. Filled in by background normalization for provider-sourced entries.
    public string? Mp4Url { get; set; }
    public string? WebmUrl { get; set; }
    public string? GifUrl { get; set; }

    // Remote URLs — provider CDN fallbacks. Always present for provider-sourced entries, null for custom uploads.
    public string? RemoteMp4Url { get; set; }
    public string? RemoteWebmUrl { get; set; }
    public string? RemoteGifUrl { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Duration in seconds from ffprobe. 0 for entries not yet locally analyzed.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Sum of local file sizes on disk.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>JSON-serialized List&lt;string&gt; of lowercased search terms accumulated from usage.</summary>
    public string? Tags { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public int UseCount { get; set; }

    /// <summary>How many messages currently reference this entry. Maintained on send/edit/delete.</summary>
    public int ReferenceCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The content type provided by the source (e.g. "video/mp4", "image/gif").</summary>
    public string? OriginalContentType { get; set; }

    public GifTranscodeStatus TranscodeStatus { get; set; } = GifTranscodeStatus.None;

    /// <summary>Reserved for Phase 2 cache-eviction; never set in MVP.</summary>
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public User? UploadedByUser { get; set; }

    private GifEntry() { } // EF Core

    public GifEntry(string? sourceProviderId, string? sourceId, Guid? uploadedByUserId)
    {
        SourceProviderId = sourceProviderId;
        SourceId = sourceId;
        UploadedByUserId = uploadedByUserId;
    }
}

[Flags]
public enum GifTranscodeStatus
{
    None     = 0,
    Pending  = 1 << 0,
    DoneMp4  = 1 << 1,
    DoneWebm = 1 << 2,
    DoneGif  = 1 << 3,
    Failed   = 1 << 4,
}
