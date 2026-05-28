using System.Text.Json;

namespace Yap.Services.Gifs;

/// <summary>
/// Klipy GIF API v1 implementation of IGifSourceProvider.
/// Docs: https://docs.klipy.com/gifs-api
///
/// Klipy positions itself as a Tenor drop-in replacement after Tenor stopped accepting new
/// API clients in Jan 2026. The API surface mirrors Tenor's at the concept level but uses:
///   - API key in URL path (/api/v1/{key}/gifs/...) instead of ?key= query string
///   - rating=g|pg|pg-13|r instead of contentfilter=off|low|medium|high
///   - page=N (integer) instead of pos=opaque-cursor
///   - response wrapped in {"result":true,"data":{"data":[...],"has_next":...}}
/// Per-item media still uses Tenor-style format keys: mp4, webm, gif, tinymp4, tinygif.
/// </summary>
public class KlipyGifProvider : IGifSourceProvider
{
    private const string BaseUrl = "https://api.klipy.com/api/v1/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GifAdminSettingsService _adminSettings;
    private readonly ILogger<KlipyGifProvider> _logger;
    private readonly string _apiKey;
    private readonly string _customerId;
    private readonly string _locale;

    public string ProviderId => "klipy";
    public string DisplayName => "Klipy";
    public string AttributionImageUrl => "/images/klipy-attribution.svg";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public KlipyGifProvider(IHttpClientFactory httpClientFactory, GifAdminSettingsService adminSettings,
        IConfiguration config, ILogger<KlipyGifProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _adminSettings = adminSettings;
        _logger = logger;
        _apiKey = config.GetValue<string>("ChatSettings:GifSettings:Klipy:ApiKey") ?? "";
        _customerId = config.GetValue<string>("ChatSettings:GifSettings:Klipy:CustomerId") ?? "yap";
        _locale = config.GetValue<string>("ChatSettings:GifSettings:Klipy:Locale") ?? "en_US";
    }

    public Task<GifSearchResult> SearchAsync(string query, string? cursor, int limit, CancellationToken ct)
    {
        if (!IsConfigured) return Task.FromResult(new GifSearchResult(new(), null));

        var url = $"{BaseUrl}{_apiKey}/gifs/search?q={Uri.EscapeDataString(query)}" +
                  CommonPaginationParams(cursor, limit);
        return FetchSearchAsync(url, cursor, ct);
    }

    public Task<GifSearchResult> GetTrendingAsync(string? cursor, int limit, CancellationToken ct)
    {
        if (!IsConfigured) return Task.FromResult(new GifSearchResult(new(), null));

        var url = $"{BaseUrl}{_apiKey}/gifs/trending?customer_id={Uri.EscapeDataString(_customerId)}" +
                  CommonPaginationParams(cursor, limit);
        return FetchSearchAsync(url, cursor, ct);
    }

    public async Task<List<GifCategory>> GetCategoriesAsync(CancellationToken ct)
    {
        if (!IsConfigured) return new();

        var url = $"{BaseUrl}{_apiKey}/gifs/categories?locale={_locale}";
        try
        {
            var client = _httpClientFactory.CreateClient("Klipy");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Klipy categories failed: {Status}", response.StatusCode);
                return new();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Klipy categories format isn't formally documented; we accept either the wrapped
            // {result, data:[...]} shape Klipy uses elsewhere, or a flat top-level array.
            JsonElement arr = default;
            var found = false;
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array) { arr = data; found = true; }
                else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    arr = inner; found = true;
                }
            }
            if (!found && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                arr = doc.RootElement; found = true;
            }
            if (!found) return new();

            var results = new List<GifCategory>();
            foreach (var c in arr.EnumerateArray())
            {
                var searchTerm = TryGetString(c, "search_term", "searchterm", "name");
                var name = TryGetString(c, "name", "search_term") ?? searchTerm;
                var image = TryGetString(c, "image", "image_url", "thumbnail");
                if (!string.IsNullOrEmpty(searchTerm) && !string.IsNullOrEmpty(image))
                    results.Add(new GifCategory(searchTerm!, name ?? searchTerm!, image!));
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Klipy categories");
            return new();
        }
    }

    public async Task RegisterShareAsync(string sourceId, string? query, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrEmpty(sourceId)) return;
        var url = $"{BaseUrl}{_apiKey}/gifs/share/{Uri.EscapeDataString(sourceId)}" +
                  $"?customer_id={Uri.EscapeDataString(_customerId)}" +
                  (string.IsNullOrEmpty(query) ? "" : $"&q={Uri.EscapeDataString(query)}");
        try
        {
            var client = _httpClientFactory.CreateClient("Klipy");
            using var response = await client.GetAsync(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Klipy share-trigger failed for {SourceId} (non-critical)", sourceId);
        }
    }

    public async Task<GifSearchItem?> ResolveByIdAsync(string sourceId, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrEmpty(sourceId)) return null;
        var url = $"{BaseUrl}{_apiKey}/gifs/items/{Uri.EscapeDataString(sourceId)}";
        try
        {
            var client = _httpClientFactory.CreateClient("Klipy");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Single-item endpoint: data is the item object directly.
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                // If data is wrapped as {data: {...item}}, unwrap one level.
                if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
                    return ParseResultItem(inner);
                return ParseResultItem(data);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private string CommonPaginationParams(string? cursor, int limit)
    {
        // Klipy uses 1-based integer page; we store it as a string in our cursor field.
        var page = 1;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed > 0)
            page = parsed;
        var perPage = Math.Clamp(limit, 8, 50);
        return $"&page={page}&per_page={perPage}&rating={RatingParam()}&locale={_locale}";
    }

    private async Task<GifSearchResult> FetchSearchAsync(string url, string? cursor, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Klipy");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Klipy request failed: {Status} for {Url}", response.StatusCode, RedactKey(url));
                return new GifSearchResult(new(), null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Standard Klipy envelope: { "result": true, "data": { "data": [...], "current_page": N, "has_next": bool } }
            if (!doc.RootElement.TryGetProperty("data", out var outer) || outer.ValueKind != JsonValueKind.Object)
                return new GifSearchResult(new(), null);
            if (!outer.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
                return new GifSearchResult(new(), null);

            var list = new List<GifSearchItem>();
            foreach (var r in items.EnumerateArray())
            {
                // Skip ad objects if Klipy mixes them in (type=="ad").
                if (r.TryGetProperty("type", out var typ) && typ.ValueKind == JsonValueKind.String
                    && typ.GetString() == "ad")
                    continue;

                var item = ParseResultItem(r);
                if (item != null) list.Add(item);
            }

            string? nextCursor = null;
            if (outer.TryGetProperty("has_next", out var hasNext) && hasNext.ValueKind == JsonValueKind.True)
            {
                var currentPage = 1;
                if (outer.TryGetProperty("current_page", out var cp) && cp.ValueKind == JsonValueKind.Number)
                    currentPage = cp.GetInt32();
                else if (int.TryParse(cursor, out var fromCursor))
                    currentPage = fromCursor;
                nextCursor = (currentPage + 1).ToString();
            }

            return new GifSearchResult(list, nextCursor);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Klipy request error for {Url}", RedactKey(url));
            return new GifSearchResult(new(), null);
        }
    }

    private static GifSearchItem? ParseResultItem(JsonElement r)
    {
        // Klipy's id can be numeric or string; the slug (also string) is the share-friendly identifier.
        // Prefer slug if present (the share / items endpoints take it), fall back to id.
        var slug = TryGetString(r, "slug");
        var idStr = slug ?? TryGetStringOrNumeric(r, "id");
        if (string.IsNullOrEmpty(idStr)) return null;

        var title = TryGetString(r, "title", "content_description") ?? "";
        var full = new List<MediaFormat>();
        var preview = new List<MediaFormat>();
        int width = 0, height = 0;

        // Klipy's media object mirrors Tenor's format keys: mp4 / webm / gif / tinymp4 / tinygif / etc.
        if (r.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            foreach (var fmt in media.EnumerateObject())
            {
                var media1 = ParseMediaFormat(fmt.Value, fmt.Name);
                if (media1 == null) continue;
                if (IsFullSizeFormatKey(fmt.Name))
                {
                    full.Add(media1);
                    if (width == 0 || height == 0) { width = media1.Width; height = media1.Height; }
                }
                else if (IsPreviewFormatKey(fmt.Name))
                {
                    preview.Add(media1);
                }
            }
        }
        // Some Klipy endpoints/keys may use "file_meta" or "files" wrapper — be tolerant.
        else if (r.TryGetProperty("file_meta", out var fileMeta) && fileMeta.ValueKind == JsonValueKind.Object)
        {
            foreach (var fmt in fileMeta.EnumerateObject())
            {
                var media1 = ParseMediaFormat(fmt.Value, fmt.Name);
                if (media1 == null) continue;
                if (IsFullSizeFormatKey(fmt.Name)) full.Add(media1);
                else if (IsPreviewFormatKey(fmt.Name)) preview.Add(media1);
            }
        }

        if (full.Count == 0 && preview.Count == 0) return null;
        return new GifSearchItem(idStr, title, width, height, full, preview);
    }

    private static MediaFormat? ParseMediaFormat(JsonElement el, string formatName)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var url = TryGetString(el, "url", "src");
        if (string.IsNullOrEmpty(url)) return null;

        // Klipy returns dims either as { "width", "height" } or as a [w, h] array under "dims" (Tenor style).
        int w = 0, h = 0;
        if (el.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number) w = wEl.GetInt32();
        if (el.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number) h = hEl.GetInt32();
        if ((w == 0 || h == 0) && el.TryGetProperty("dims", out var dims) && dims.ValueKind == JsonValueKind.Array && dims.GetArrayLength() >= 2)
        {
            w = dims[0].GetInt32();
            h = dims[1].GetInt32();
        }

        long size = 0;
        if (el.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
        else if (el.TryGetProperty("file_size", out var fs) && fs.ValueKind == JsonValueKind.Number) size = fs.GetInt64();

        return new MediaFormat(url!, ContentTypeFor(formatName), w, h, size);
    }

    private static readonly HashSet<string> FullFormatKeys = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "loopedmp4", "looped_mp4", "webm", "gif", "mediumgif", "medium_gif", "hd" };

    private static readonly HashSet<string> PreviewFormatKeys = new(StringComparer.OrdinalIgnoreCase)
        { "tinymp4", "tiny_mp4", "nanomp4", "nano_mp4", "tinywebm", "tiny_webm", "nanowebm", "nano_webm",
          "tinygif", "tiny_gif", "nanogif", "nano_gif", "sm", "preview" };

    private static bool IsFullSizeFormatKey(string key) => FullFormatKeys.Contains(key);
    private static bool IsPreviewFormatKey(string key) => PreviewFormatKeys.Contains(key);

    private static string ContentTypeFor(string formatName)
    {
        var k = formatName.ToLowerInvariant();
        if (k.Contains("mp4")) return "video/mp4";
        if (k.Contains("webm")) return "video/webm";
        if (k.Contains("gif")) return "image/gif";
        if (k.Contains("webp")) return "image/webp";
        return "application/octet-stream";
    }

    private string RatingParam() => _adminSettings.KlipyRating switch
    {
        KlipyRating.G => "g",
        KlipyRating.PG => "pg",
        KlipyRating.PG13 => "pg-13",
        KlipyRating.R => "r",
        _ => "pg"
    };

    private string RedactKey(string url) =>
        string.IsNullOrEmpty(_apiKey) ? url : url.Replace(_apiKey, "***");

    private static string? TryGetString(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    private static string? TryGetStringOrNumeric(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }
}
