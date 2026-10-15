using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yap.Helpers;

namespace Yap.Services.Accounts;

/// <summary>
/// The networks Cloudflare's proxy connects from. A request whose last hop was a Cloudflare address carries the
/// visitor's address in CF-Connecting-IP, which Cloudflare sets itself (IpHelper). The ranges change rarely, but
/// they do change: the built-in list is the fallback for a start without network, Data/cloudflare-ips.json is the
/// last download, and the refresh fetches Cloudflare's own list once a day (ChatSettings:CloudflareIpsUrl).
/// </summary>
public sealed class CloudflareIps
{
    // https://www.cloudflare.com/ips/ as of 2026-10-15. Only the fallback: the download replaces it.
    private static readonly string[] BuiltIn =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
    ];

    // Replaced as a whole, never edited in place: a request thread reads it while the refresh swaps it.
    private volatile IPNetwork[] _networks = Parse(BuiltIn);

    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>Where the current list came from: "built-in", "cache" or "download", and when (download only).</summary>
    public string Source { get; private set; } = "built-in";
    public DateTime? RefreshedAt { get; private set; }

    public CloudflareStatusDto Status() => new(_networks.Length, Source, RefreshedAt);

    public bool Contains(IPAddress address)
    {
        address = IpHelper.Unmapped(address);
        foreach (var network in _networks)
            if (network.Contains(address)) return true;
        return false;
    }

    /// <summary>Replaces the list. Entries that do not parse are skipped; an empty result keeps the old list.</summary>
    public bool Apply(IEnumerable<string> cidrs, string source, DateTime? refreshedAt = null)
    {
        var parsed = Parse(cidrs);
        if (parsed.Length == 0) return false;
        _networks = parsed;
        Source = source;
        RefreshedAt = refreshedAt;
        return true;
    }

    private static IPNetwork[] Parse(IEnumerable<string> cidrs) =>
        cidrs.Select(c => IPNetwork.TryParse(c.Trim(), out var n) ? n : (IPNetwork?)null).OfType<IPNetwork>().ToArray();
}

public sealed record CloudflareStatusDto(int Count, string Source, DateTime? RefreshedAt);

/// <summary>
/// Keeps <see cref="CloudflareIps"/> current: the last download from Data/cloudflare-ips.json at start, then a
/// download a day. A failed download keeps the list it had; the log says so at debug level, because a server
/// without outbound network is a normal deployment.
/// </summary>
public sealed class CloudflareIpsRefresh(CloudflareIps ips, IHttpClientFactory clients, IConfiguration configuration,
    IWebHostEnvironment env, ILogger<CloudflareIpsRefresh> log) : BackgroundService
{
    public const string DefaultUrl = "https://api.cloudflare.com/client/v4/ips";

    private string CachePath => Path.Combine(env.ContentRootPath, "Data", "cloudflare-ips.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadCache();
        var url = configuration.GetValue("ChatSettings:CloudflareIpsUrl", DefaultUrl);
        if (string.IsNullOrWhiteSpace(url)) return;   // refresh off: the cache or the built-in list stays

        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try { await DownloadAsync(url, stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogDebug(e, "Cloudflare ranges: the download from {Url} failed, the current list stays", url); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var cached = JsonSerializer.Deserialize<Cidrs>(File.ReadAllText(CachePath));
            if (cached is not null && ips.Apply(cached.All, "cache", File.GetLastWriteTimeUtc(CachePath))) log.LogDebug("Cloudflare ranges: {Count} from {Path}", ips.Networks.Count, CachePath);
        }
        catch (Exception e) { log.LogDebug(e, "Cloudflare ranges: {Path} is not readable, the built-in list stays", CachePath); }
    }

    private async Task DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await clients.CreateClient("Cloudflare").GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Envelope>(ct);
        if (body?.Result is not { } result || !ips.Apply(result.All, "download", DateTime.UtcNow)) throw new InvalidDataException("no ranges in the response");
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(result), ct);
        log.LogInformation("Cloudflare ranges: {Count} networks, refreshed", ips.Networks.Count);
    }

    private sealed record Envelope([property: JsonPropertyName("result")] Cidrs? Result);

    /// <summary>The shape of the Cloudflare API result, and of the cache file.</summary>
    private sealed record Cidrs(
        [property: JsonPropertyName("ipv4_cidrs")] string[]? Ipv4,
        [property: JsonPropertyName("ipv6_cidrs")] string[]? Ipv6)
    {
        [JsonIgnore] public IEnumerable<string> All => (Ipv4 ?? []).Concat(Ipv6 ?? []);
    }
}
