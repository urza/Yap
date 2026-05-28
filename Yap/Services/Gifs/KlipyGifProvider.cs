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
        _logger.LogInformation("Klipy GET {Url}", RedactKey(url));
        try
        {
            var client = _httpClientFactory.CreateClient("Klipy");
            using var response = await client.GetAsync(url, ct);
            var bodyText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Klipy HTTP {Status} for {Url} — body: {Body}",
                    response.StatusCode, RedactKey(url), Truncate(bodyText, 500));
                return new GifSearchResult(new(), null);
            }

            _logger.LogInformation("Klipy HTTP {Status} for {Url} ({Bytes} bytes)",
                response.StatusCode, RedactKey(url), bodyText.Length);

            using var doc = JsonDocument.Parse(bodyText);

            // Standard Klipy envelope: { "result": true, "data": { "data": [...], "current_page": N, "has_next": bool } }
            if (!doc.RootElement.TryGetProperty("data", out var outer) || outer.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("Klipy response has no 'data' object. Root kind: {Kind}, body: {Body}",
                    doc.RootElement.ValueKind, Truncate(bodyText, 800));
                return new GifSearchResult(new(), null);
            }
            if (!outer.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Klipy response 'data.data' is not an array. Outer keys: [{Keys}], body: {Body}",
                    string.Join(",", outer.EnumerateObject().Select(p => p.Name)), Truncate(bodyText, 800));
                return new GifSearchResult(new(), null);
            }

            var totalItems = items.GetArrayLength();
            var list = new List<GifSearchItem>();
            var skippedAds = 0;
            var rejectedNoMedia = 0;
            foreach (var r in items.EnumerateArray())
            {
                if (r.TryGetProperty("type", out var typ) && typ.ValueKind == JsonValueKind.String
                    && typ.GetString() == "ad")
                {
                    skippedAds++;
                    continue;
                }

                var item = ParseResultItem(r);
                if (item != null) list.Add(item);
                else rejectedNoMedia++;
            }

            if (list.Count == 0 && totalItems > 0)
            {
                // Got items back but couldn't parse any — log the FIRST item so we can see the actual shape.
                var firstItem = items[0];
                _logger.LogWarning("Klipy returned {Total} items but parsed 0 (ads skipped={Ads}, rejected={Rej}). " +
                    "First item keys: [{Keys}]. First item sample: {Sample}",
                    totalItems, skippedAds, rejectedNoMedia,
                    string.Join(",", firstItem.EnumerateObject().Select(p => p.Name)),
                    Truncate(firstItem.GetRawText(), 1500));
            }
            else
            {
                _logger.LogInformation("Klipy parsed {Parsed}/{Total} items (ads={Ads}, rejected={Rej})",
                    list.Count, totalItems, skippedAds, rejectedNoMedia);
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

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private GifSearchItem? ParseResultItem(JsonElement r)
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

        // Current Klipy shape: file.{hd|md|sm}.{gif|webp|jpg|mp4|webm}.{url,width,height,size}.
        // We prefer the "md" tier for full formats: gif is ~3× smaller than hd (5MB vs 17MB on
        // the cat example), while mp4/webm are byte-identical between tiers (Klipy doesn't
        // downsize video). Fall back to "hd" if "md" isn't present for some items.
        // Previews come from "sm" tier (smallest), falling back to "md".
        if (r.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
        {
            JsonElement fullTier = default;
            if (file.TryGetProperty("md", out var md) && md.ValueKind == JsonValueKind.Object) fullTier = md;
            else if (file.TryGetProperty("hd", out var hd) && hd.ValueKind == JsonValueKind.Object) fullTier = hd;

            if (fullTier.ValueKind == JsonValueKind.Object)
            {
                foreach (var fmt in fullTier.EnumerateObject())
                {
                    if (!IsPlayableFormatKey(fmt.Name)) continue;
                    var mf = ParseMediaFormat(fmt.Value, fmt.Name);
                    if (mf == null) continue;
                    full.Add(mf);
                    if (width == 0 || height == 0) { width = mf.Width; height = mf.Height; }
                }
            }

            JsonElement previewTier = default;
            if (file.TryGetProperty("sm", out var sm) && sm.ValueKind == JsonValueKind.Object) previewTier = sm;
            else if (file.TryGetProperty("md", out var md2) && md2.ValueKind == JsonValueKind.Object) previewTier = md2;

            if (previewTier.ValueKind == JsonValueKind.Object)
            {
                foreach (var fmt in previewTier.EnumerateObject())
                {
                    if (!IsPlayableFormatKey(fmt.Name)) continue;
                    var mf = ParseMediaFormat(fmt.Value, fmt.Name);
                    if (mf == null) continue;
                    preview.Add(mf);
                }
            }
        }
        // Legacy Tenor-style fallback: media.{mp4|webm|gif|tinymp4|tinygif|...}.
        else if (r.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            foreach (var fmt in media.EnumerateObject())
            {
                var mf = ParseMediaFormat(fmt.Value, fmt.Name);
                if (mf == null) continue;
                if (IsFullSizeFormatKey(fmt.Name))
                {
                    full.Add(mf);
                    if (width == 0 || height == 0) { width = mf.Width; height = mf.Height; }
                }
                else if (IsPreviewFormatKey(fmt.Name))
                {
                    preview.Add(mf);
                }
            }
        }

        if (full.Count == 0 && preview.Count == 0)
        {
            var topKeys = string.Join(",", r.EnumerateObject().Select(p => p.Name));
            _logger.LogWarning("Klipy item {Id} produced no formats. Top keys: [{TopKeys}]", idStr, topKeys);
            return null;
        }
        return new GifSearchItem(idStr, title, width, height, full, preview);
    }

    /// <summary>
    /// Formats our renderer can use. WebP is the new preferred format — animated WebP plays in
    /// an &lt;img&gt; tag (instant animation, no browser autoplay policy) and is ~2× smaller than
    /// the equivalent GIF. Klipy's jpg is rejected (static-only).
    /// </summary>
    private static bool IsPlayableFormatKey(string formatKey) =>
        formatKey.Equals("mp4", StringComparison.OrdinalIgnoreCase)
        || formatKey.Equals("webm", StringComparison.OrdinalIgnoreCase)
        || formatKey.Equals("gif", StringComparison.OrdinalIgnoreCase)
        || formatKey.Equals("webp", StringComparison.OrdinalIgnoreCase);

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
