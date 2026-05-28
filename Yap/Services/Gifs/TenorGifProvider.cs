using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Yap.Services.Gifs;

/// <summary>
/// Tenor v2 API implementation of IGifSourceProvider.
/// Docs: https://developers.google.com/tenor/guides/quickstart
/// </summary>
public class TenorGifProvider : IGifSourceProvider
{
    private const string BaseUrl = "https://tenor.googleapis.com/v2/";
    private const string MediaFilter = "mp4,webm,gif,tinymp4,tinygif";
    private static readonly HashSet<string> FullMediaTypes = new(StringComparer.Ordinal)
        { "mp4", "loopedmp4", "webm", "gif", "mediumgif", "webp_transparent" };
    private static readonly HashSet<string> PreviewMediaTypes = new(StringComparer.Ordinal)
        { "tinymp4", "nanomp4", "tinywebm", "nanowebm", "tinygif", "nanogif" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GifAdminSettingsService _adminSettings;
    private readonly ILogger<TenorGifProvider> _logger;
    private readonly string _apiKey;
    private readonly string _clientKey;
    private readonly string _locale;

    public string ProviderId => "tenor";
    public string DisplayName => "Tenor";
    // SVG placeholder is bundled; replace with the official Tenor PNG from
    // https://tenor.com/gifapi/documentation#attribution if desired.
    public string AttributionImageUrl => "/images/tenor-attribution.svg";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public TenorGifProvider(IHttpClientFactory httpClientFactory, GifAdminSettingsService adminSettings,
        IConfiguration config, ILogger<TenorGifProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _adminSettings = adminSettings;
        _logger = logger;
        _apiKey = config.GetValue<string>("ChatSettings:GifSettings:Tenor:ApiKey") ?? "";
        _clientKey = config.GetValue<string>("ChatSettings:GifSettings:Tenor:ClientKey") ?? "yap";
        _locale = config.GetValue<string>("ChatSettings:GifSettings:Tenor:Locale") ?? "en_US";
    }

    public async Task<GifSearchResult> SearchAsync(string query, string? cursor, int limit, CancellationToken ct)
    {
        if (!IsConfigured) return new GifSearchResult(new(), null);

        var url = $"{BaseUrl}search?q={Uri.EscapeDataString(query)}&key={_apiKey}&client_key={_clientKey}" +
                  $"&limit={Math.Clamp(limit, 1, 50)}&media_filter={MediaFilter}" +
                  $"&contentfilter={ContentFilterParam()}&locale={_locale}" +
                  (string.IsNullOrEmpty(cursor) ? "" : $"&pos={Uri.EscapeDataString(cursor)}");
        return await FetchSearchAsync(url, ct);
    }

    public async Task<GifSearchResult> GetTrendingAsync(string? cursor, int limit, CancellationToken ct)
    {
        if (!IsConfigured) return new GifSearchResult(new(), null);

        var url = $"{BaseUrl}featured?key={_apiKey}&client_key={_clientKey}" +
                  $"&limit={Math.Clamp(limit, 1, 50)}&media_filter={MediaFilter}" +
                  $"&contentfilter={ContentFilterParam()}&locale={_locale}" +
                  (string.IsNullOrEmpty(cursor) ? "" : $"&pos={Uri.EscapeDataString(cursor)}");
        return await FetchSearchAsync(url, ct);
    }

    public async Task<List<GifCategory>> GetCategoriesAsync(CancellationToken ct)
    {
        if (!IsConfigured) return new();

        var url = $"{BaseUrl}categories?type=featured&key={_apiKey}&client_key={_clientKey}" +
                  $"&contentfilter={ContentFilterParam()}&locale={_locale}";
        try
        {
            var client = _httpClientFactory.CreateClient("Tenor");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenor categories failed: {Status}", response.StatusCode);
                return new();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var results = new List<GifCategory>();
            if (doc.RootElement.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    var searchTerm = tag.TryGetProperty("searchterm", out var st) ? st.GetString() : null;
                    var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var image = tag.TryGetProperty("image", out var img) ? img.GetString() : null;
                    if (!string.IsNullOrEmpty(searchTerm) && !string.IsNullOrEmpty(image))
                    {
                        results.Add(new GifCategory(searchTerm, name ?? searchTerm, image));
                    }
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Tenor categories");
            return new();
        }
    }

    public async Task RegisterShareAsync(string sourceId, string? query, CancellationToken ct)
    {
        if (!IsConfigured) return;
        var url = $"{BaseUrl}registershare?id={Uri.EscapeDataString(sourceId)}" +
                  (string.IsNullOrEmpty(query) ? "" : $"&q={Uri.EscapeDataString(query)}") +
                  $"&key={_apiKey}&client_key={_clientKey}&locale={_locale}";
        try
        {
            var client = _httpClientFactory.CreateClient("Tenor");
            using var response = await client.GetAsync(url, ct);
            // Don't care about the result — TOS requires us to call, not to verify success
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tenor registershare failed for {SourceId} (non-critical)", sourceId);
        }
    }

    public async Task<GifSearchItem?> ResolveByIdAsync(string sourceId, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var url = $"{BaseUrl}posts?ids={Uri.EscapeDataString(sourceId)}&key={_apiKey}&client_key={_clientKey}" +
                  $"&media_filter={MediaFilter}";
        try
        {
            var result = await FetchSearchAsync(url, ct);
            return result.Items.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task<GifSearchResult> FetchSearchAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Tenor");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenor request failed: {Status} for {Url}", response.StatusCode, RedactKey(url));
                return new GifSearchResult(new(), null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var items = new List<GifSearchItem>();
            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var r in results.EnumerateArray())
                {
                    var item = ParseResultItem(r);
                    if (item != null) items.Add(item);
                }
            }
            var next = doc.RootElement.TryGetProperty("next", out var nextEl) ? nextEl.GetString() : null;
            return new GifSearchResult(items, string.IsNullOrEmpty(next) ? null : next);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenor request error for {Url}", RedactKey(url));
            return new GifSearchResult(new(), null);
        }
    }

    private static GifSearchItem? ParseResultItem(JsonElement r)
    {
        var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;
        var title = r.TryGetProperty("content_description", out var cd) ? cd.GetString()
                  : r.TryGetProperty("title", out var t) ? t.GetString()
                  : "";

        var full = new List<MediaFormat>();
        var preview = new List<MediaFormat>();
        int width = 0, height = 0;

        if (r.TryGetProperty("media_formats", out var formats))
        {
            foreach (var fmt in formats.EnumerateObject())
            {
                var name = fmt.Name;
                var media = ParseMediaFormat(fmt.Value, name);
                if (media == null) continue;

                if (FullMediaTypes.Contains(name))
                {
                    full.Add(media);
                    // Capture canonical dimensions from the highest-priority full format we see.
                    if (width == 0 || height == 0)
                    {
                        width = media.Width;
                        height = media.Height;
                    }
                }
                else if (PreviewMediaTypes.Contains(name))
                {
                    preview.Add(media);
                }
            }
        }

        if (full.Count == 0 && preview.Count == 0) return null;
        return new GifSearchItem(id!, title ?? "", width, height, full, preview);
    }

    private static MediaFormat? ParseMediaFormat(JsonElement el, string formatName)
    {
        var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url)) return null;

        var (w, h) = (0, 0);
        if (el.TryGetProperty("dims", out var dims) && dims.ValueKind == JsonValueKind.Array && dims.GetArrayLength() >= 2)
        {
            w = dims[0].GetInt32();
            h = dims[1].GetInt32();
        }
        var size = el.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0L;

        var contentType = formatName switch
        {
            "mp4" or "loopedmp4" or "tinymp4" or "nanomp4" => "video/mp4",
            "webm" or "tinywebm" or "nanowebm" => "video/webm",
            "gif" or "mediumgif" or "tinygif" or "nanogif" => "image/gif",
            "webp_transparent" => "image/webp",
            _ => "application/octet-stream"
        };

        return new MediaFormat(url!, contentType, w, h, size);
    }

    private string ContentFilterParam() => _adminSettings.TenorContentFilter switch
    {
        TenorContentFilter.Off => "off",
        TenorContentFilter.Low => "low",
        TenorContentFilter.High => "high",
        _ => "medium"
    };

    private string RedactKey(string url) =>
        string.IsNullOrEmpty(_apiKey) ? url : url.Replace(_apiKey, "***");
}
