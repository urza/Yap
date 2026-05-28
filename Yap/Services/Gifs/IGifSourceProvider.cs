namespace Yap.Services.Gifs;

/// <summary>
/// Source of GIFs from outside the local cache (Tenor today, possibly Giphy or self-hosted later).
/// Implementations are stateless except for HttpClient and config.
/// </summary>
public interface IGifSourceProvider
{
    /// <summary>Stable identifier persisted on GifEntry.SourceProviderId (e.g. "tenor").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name shown in the picker attribution.</summary>
    string DisplayName { get; }

    /// <summary>URL of the bundled attribution badge image (TOS often requires this be visible).</summary>
    string AttributionImageUrl { get; }

    /// <summary>True if the provider has the credentials/config it needs to serve requests.</summary>
    bool IsConfigured { get; }

    Task<GifSearchResult> SearchAsync(string query, string? cursor, int limit, CancellationToken ct);
    Task<GifSearchResult> GetTrendingAsync(string? cursor, int limit, CancellationToken ct);
    Task<List<GifCategory>> GetCategoriesAsync(CancellationToken ct);

    /// <summary>Notify provider that a user shared this GIF (Tenor TOS requirement; no-op elsewhere).</summary>
    Task RegisterShareAsync(string sourceId, string? query, CancellationToken ct);

    /// <summary>Re-resolve a GIF by source id when local files are lost. Returns null if no longer available.</summary>
    Task<GifSearchItem?> ResolveByIdAsync(string sourceId, CancellationToken ct);
}
