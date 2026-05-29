using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yap.Data;
using Yap.Models;

namespace Yap.Services.Gifs;

/// <summary>
/// Singleton orchestrator for the GIF feature: local cache index, provider delegation,
/// download + normalization pipeline, favorites, and reference counting.
///
/// Mirrors the LinkPreviewService / MediaCacheService patterns: ConcurrentDictionary
/// in-memory state, fire-and-forget background work with in-flight dedup,
/// callback events for UI to refresh when async work completes.
/// </summary>
public class GifService
{
    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _env;
    private readonly IGifSourceProvider _provider;
    private readonly GifFfmpegHelper _ffmpeg;
    private readonly UserService _userService;
    private readonly ILogger<GifService> _logger;

    // In-memory index of all entries, populated on startup.
    private readonly ConcurrentDictionary<Guid, GifEntry> _entries = new();

    // (providerId, sourceId) → entry id, for fast dedup of provider results.
    private readonly ConcurrentDictionary<(string Provider, string SourceId), Guid> _byProviderSourceId = new();

    // tag → set of entry ids. Tags are lowercased + trimmed. Lookups during local-first search.
    private readonly Dictionary<string, HashSet<Guid>> _tagIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagIndexLock = new();

    // Dedup background download/transcode tasks per provider sourceId.
    private readonly ConcurrentDictionary<string, byte> _inFlightDownloads = new();

    // Dirty-flush: UseCount + LastUsedAt + Tags updates batched every 10s.
    private readonly ConcurrentDictionary<Guid, byte> _dirtyEntries = new();
    private CancellationTokenSource? _flushCts;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    // Per-user favorites (cached in memory, written to DB immediately on toggle).
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _favoritesByUser = new();

    private const int MaxRecentGifs = 30;
    private const int LocalSearchLimit = 24;
    private const long MaxDownloadBytes = 64L * 1024 * 1024; // 64MB safety ceiling per file
    private const int DownloadTimeoutMs = 30_000;

    /// <summary>Raised when an entry's state changes (download/transcode complete). UI filters by attached entryId.</summary>
    public event Action<Guid>? OnGifEntryUpdated;

    /// <summary>Raised when a user's favorites change. UI filters by their own userId.</summary>
    public event Action<Guid>? OnFavoritesChanged;

    /// <summary>Raised when the library grew (new entry added). For "Recent" tab refresh in the picker.</summary>
    public event Action<GifEntry>? OnGifLibraryChanged;

    public IGifSourceProvider Provider => _provider;
    public bool IsConfigured => _provider.IsConfigured;

    public GifService(IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment env,
        IGifSourceProvider provider,
        GifFfmpegHelper ffmpeg,
        UserService userService,
        ILogger<GifService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _env = env;
        _provider = provider;
        _ffmpeg = ffmpeg;
        _userService = userService;
        _logger = logger;
        _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();

        EnsureDirectories();

        var lifetime = serviceProvider.GetService<IHostApplicationLifetime>();
        lifetime?.ApplicationStopping.Register(() => FlushDirtyAsync().GetAwaiter().GetResult());
    }

    private string CustomUploadsDir => Path.Combine(_env.WebRootPath, "uploads", "gifs");
    private string CacheDir => Path.Combine(_env.ContentRootPath, "Data", "gif-cache");

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(CustomUploadsDir);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>
    /// Loads all GifEntries + FavoriteGifs from DB into the in-memory index. Call on startup
    /// after persistence is initialized.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_dbFactory == null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entries = await db.GifEntries.AsNoTracking().ToListAsync();
            foreach (var entry in entries)
            {
                _entries[entry.Id] = entry;
                if (!string.IsNullOrEmpty(entry.SourceProviderId) && !string.IsNullOrEmpty(entry.SourceId))
                    _byProviderSourceId[(entry.SourceProviderId, entry.SourceId)] = entry.Id;
                IndexEntryTags(entry);
            }

            var favorites = await db.FavoriteGifs.AsNoTracking().ToListAsync();
            foreach (var fav in favorites)
            {
                var set = _favoritesByUser.GetOrAdd(fav.UserId, _ => new HashSet<Guid>());
                lock (set) set.Add(fav.GifEntryId);
            }

            _logger.LogInformation("Loaded {EntryCount} GIF entries, {FavCount} favorites", entries.Count, favorites.Count);

            // Backfill: any provider entries that have a remote gif URL but no local gif file
            // get queued for download. Catches entries created before we cached gifs locally,
            // and also re-fetches if files were manually deleted.
            var backfillNeeded = entries.Where(NeedsBackgroundNormalization).ToList();
            if (backfillNeeded.Count > 0)
            {
                _logger.LogInformation("Backfilling local cache for {Count} GIF entries", backfillNeeded.Count);
                foreach (var entry in backfillNeeded)
                    QueueNormalization(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load GIF library from database");
        }

        StartFlushLoop();
    }

    public GifEntry? GetEntry(Guid id) => _entries.TryGetValue(id, out var e) ? e : null;

    #region Public read API

    /// <summary>Local-first search across the cached library by tag tokens.</summary>
    public List<GifEntry> SearchLocal(string query, int limit = LocalSearchLimit)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = query.Trim().ToLowerInvariant();
        if (q.Length < 2) return new();

        var hits = new HashSet<Guid>();

        lock (_tagIndexLock)
        {
            // Exact tag hit
            if (_tagIndex.TryGetValue(q, out var direct))
                foreach (var id in direct) hits.Add(id);

            // Partial tag match — cheap because the tag index is small.
            foreach (var (tag, ids) in _tagIndex)
            {
                if (tag.Length > q.Length && tag.Contains(q))
                    foreach (var id in ids) hits.Add(id);
            }
        }

        return hits
            .Select(id => _entries.TryGetValue(id, out var e) ? e : null)
            .Where(e => e != null && e.DeletedAt == null)
            .OrderByDescending(e => e!.UseCount)
            .ThenByDescending(e => e!.LastUsedAt)
            .Take(limit)
            .Select(e => e!)
            .ToList();
    }

    public Task<GifSearchResult> SearchProviderAsync(string query, string? cursor, int limit, CancellationToken ct)
        => _provider.SearchAsync(query, cursor, limit, ct);

    public Task<GifSearchResult> GetTrendingAsync(string? cursor, int limit, CancellationToken ct)
        => _provider.GetTrendingAsync(cursor, limit, ct);

    public Task<List<GifCategory>> GetCategoriesAsync(CancellationToken ct)
        => _provider.GetCategoriesAsync(ct);

    public List<GifEntry> GetFavorites(Guid userId)
    {
        if (!_favoritesByUser.TryGetValue(userId, out var set)) return new();
        Guid[] ids;
        lock (set) ids = set.ToArray();
        return ids
            .Select(id => _entries.TryGetValue(id, out var e) ? e : null)
            .Where(e => e != null && e.DeletedAt == null)
            .Select(e => e!)
            .OrderByDescending(e => e.LastUsedAt)
            .ToList();
    }

    public bool IsFavorite(Guid userId, Guid gifEntryId)
    {
        if (!_favoritesByUser.TryGetValue(userId, out var set)) return false;
        lock (set) return set.Contains(gifEntryId);
    }

    public List<GifEntry> GetRecents(string username)
    {
        var ids = _userService.GetRecentGifs(username);
        return ids
            .Select(id => _entries.TryGetValue(id, out var e) ? e : null)
            .Where(e => e != null && e.DeletedAt == null)
            .Select(e => e!)
            .ToList();
    }

    #endregion

    #region Send flows

    /// <summary>
    /// Called when the user clicks a provider result in the picker. Creates a GifEntry from the
    /// already-fetched search item (or hits an existing entry if we've seen this sourceId before),
    /// updates usage, queues background download, and returns a GifAttachment ready to embed in a
    /// ChatMessage. No extra round-trip to the provider — the search response carried everything we
    /// need.
    /// </summary>
    public async Task<GifAttachment?> SendProviderGifAsync(GifSearchItem item, string? query, Guid userId, string username)
    {
        if (string.IsNullOrEmpty(item.SourceId)) return null;

        GifEntry entry;
        if (_byProviderSourceId.TryGetValue((_provider.ProviderId, item.SourceId), out var existingId)
            && _entries.TryGetValue(existingId, out var existing))
        {
            entry = existing;
            TouchUsage(entry, query);
            _logger.LogInformation("SendProviderGif hit cached entry {EntryId} for sourceId={SourceId}", entry.Id, item.SourceId);
        }
        else
        {
            entry = await CreateEntryFromProviderItemAsync(item, query);
            _logger.LogInformation("SendProviderGif created new entry {EntryId} for sourceId={SourceId} (formats: {FormatCount})",
                entry.Id, item.SourceId, item.Formats.Count);
        }

        // Fire-and-forget provider share notification (TOS requirement for some providers).
        _ = _provider.RegisterShareAsync(item.SourceId, query, CancellationToken.None);

        // Queue background normalization if local files aren't ready yet.
        if (NeedsBackgroundNormalization(entry))
            QueueNormalization(entry);

        AddToRecents(userId, username, entry.Id);

        return new GifAttachment(entry.Id, entry.Width, entry.Height);
    }

    /// <summary>
    /// Called when the user picks an already-cached GIF (Recent, Favorite, or a local search hit).
    /// </summary>
    public Task<GifAttachment?> SendCachedGifAsync(Guid gifEntryId, string? query, Guid userId, string username)
    {
        if (!_entries.TryGetValue(gifEntryId, out var entry) || entry.DeletedAt != null)
            return Task.FromResult<GifAttachment?>(null);

        TouchUsage(entry, query);
        AddToRecents(userId, username, gifEntryId);

        return Task.FromResult<GifAttachment?>(new GifAttachment(entry.Id, entry.Width, entry.Height));
    }

    /// <summary>
    /// Try to accept an uploaded file as a custom GIF. If the file qualifies (explicit kind=gif marker,
    /// .gif extension, OR short silent video heuristic), it gets normalized and registered as a
    /// GifEntry, and the source file is consumed (deleted). Returns the new attachment, or null if
    /// the file should remain in the regular image/video pipeline.
    /// </summary>
    public async Task<GifAttachment?> TryAcceptAsGifAsync(string sourceFilePath, string originalContentType,
        bool isExplicitGif, Guid? uploaderUserId, CancellationToken ct = default)
    {
        if (!File.Exists(sourceFilePath)) return null;
        if (!GifFfmpegHelper.IsAvailable)
        {
            _logger.LogWarning("ffmpeg unavailable — cannot accept custom GIFs");
            return null;
        }

        var probe = await _ffmpeg.ProbeAsync(sourceFilePath, ct);
        if (probe == null)
        {
            if (isExplicitGif) _logger.LogWarning("ffprobe failed on explicit GIF upload {Path}", sourceFilePath);
            return null;
        }

        // Reject obviously-too-long uploads (defends against tar pit and accidental video attaches).
        const double MaxGifDurationSec = 30.0;
        if (probe.DurationSeconds > MaxGifDurationSec && !isExplicitGif)
            return null;

        // Auto-classify: short + no audio + has video stream → GIF-like.
        // Explicit kind=gif marker bypasses classification (audio gets stripped on transcode).
        var qualifies = isExplicitGif
            || (!probe.HasAudio && probe.VideoCodec != null && probe.DurationSeconds <= MaxGifDurationSec && probe.DurationSeconds > 0);
        if (!qualifies) return null;

        // Produce an animated WebP for any upload. WebP in <img> = instant animation on page load
        // (no autoplay policy block), and ~2× smaller than the equivalent GIF. .gif sources are
        // copied as-is (already img-tag-friendly); everything else (mp4, mov, webm) goes through
        // ffmpeg's libwebp encoder.
        var entryId = Guid.NewGuid();
        var sourceExt = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        string localExt;
        string localPath;
        bool produced;

        if (sourceExt == ".gif")
        {
            localExt = ".gif";
            localPath = Path.Combine(CustomUploadsDir, $"{entryId}.gif");
            try
            {
                File.Copy(sourceFilePath, localPath, overwrite: true);
                produced = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy uploaded gif to {Path}", localPath);
                produced = false;
            }
        }
        else
        {
            localExt = ".webp";
            localPath = Path.Combine(CustomUploadsDir, $"{entryId}.webp");
            produced = await _ffmpeg.TranscodeToAnimatedWebpAsync(sourceFilePath, localPath, ct);
            if (!produced)
                _logger.LogWarning("Failed to transcode upload to animated WebP: {Path}", sourceFilePath);
        }

        if (!produced) return null;

        // Dimensions: prefer the source probe; fall back to probing the produced output.
        int width = probe.Width, height = probe.Height;
        if (width == 0 || height == 0)
        {
            var outProbe = await _ffmpeg.ProbeAsync(localPath, ct);
            if (outProbe != null) { width = outProbe.Width; height = outProbe.Height; }
        }

        var entry = new GifEntry(sourceProviderId: null, sourceId: null, uploaderUserId)
        {
            Id = entryId,
            GifUrl = $"/uploads/gifs/{entryId}{localExt}",
            Width = width,
            Height = height,
            DurationSeconds = probe.DurationSeconds,
            FileSizeBytes = SafeFileSize(localPath),
            OriginalContentType = originalContentType,
            TranscodeStatus = GifTranscodeStatus.DoneGif,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };

        await PersistNewEntryAsync(entry);
        IndexEntry(entry);

        // Consume the source file.
        TryDelete(sourceFilePath);

        OnGifLibraryChanged?.Invoke(entry);
        return new GifAttachment(entry.Id, entry.Width, entry.Height);
    }

    #endregion

    #region Favorites

    public async Task<bool> ToggleFavoriteAsync(Guid userId, Guid gifEntryId)
    {
        if (!_entries.ContainsKey(gifEntryId)) return false;

        var set = _favoritesByUser.GetOrAdd(userId, _ => new HashSet<Guid>());
        bool nowFavorite;
        lock (set)
        {
            if (set.Contains(gifEntryId))
            {
                set.Remove(gifEntryId);
                nowFavorite = false;
            }
            else
            {
                set.Add(gifEntryId);
                nowFavorite = true;
            }
        }

        if (_dbFactory != null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                if (nowFavorite)
                {
                    db.FavoriteGifs.Add(new FavoriteGif(userId, gifEntryId));
                }
                else
                {
                    await db.FavoriteGifs
                        .Where(f => f.UserId == userId && f.GifEntryId == gifEntryId)
                        .ExecuteDeleteAsync();
                }
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist favorite toggle for user {UserId} gif {GifId}", userId, gifEntryId);
            }
        }

        OnFavoritesChanged?.Invoke(userId);
        return nowFavorite;
    }

    #endregion

    #region Reference count maintenance (called by ChatService)

    public void IncrementReferences(List<GifAttachment>? attachments)
    {
        if (attachments == null || attachments.Count == 0) return;
        foreach (var att in attachments)
        {
            if (_entries.TryGetValue(att.GifEntryId, out var entry))
            {
                lock (entry) { entry.ReferenceCount++; }
                _dirtyEntries.TryAdd(att.GifEntryId, 0);
            }
        }
    }

    public void DecrementReferences(List<GifAttachment>? attachments)
    {
        if (attachments == null || attachments.Count == 0) return;
        foreach (var att in attachments)
        {
            if (_entries.TryGetValue(att.GifEntryId, out var entry))
            {
                lock (entry) { if (entry.ReferenceCount > 0) entry.ReferenceCount--; }
                _dirtyEntries.TryAdd(att.GifEntryId, 0);
            }
        }
    }

    #endregion

    #region Internals: entry creation, indexing, background work

    private async Task<GifEntry> CreateEntryFromProviderItemAsync(GifSearchItem item, string? query)
    {
        var entry = new GifEntry(_provider.ProviderId, item.SourceId, uploadedByUserId: null)
        {
            Width = item.Width,
            Height = item.Height,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            TranscodeStatus = GifTranscodeStatus.Pending,
        };

        // Consider full-quality formats first, then preview formats, so items that ship only a
        // preview tier still yield a usable source instead of a dead (all-null) entry.
        var formats = item.Formats.Concat(item.PreviewFormats).ToList();

        foreach (var fmt in formats)
        {
            switch (fmt.ContentType)
            {
                case "video/mp4": entry.RemoteMp4Url ??= fmt.Url; break;
                case "video/webm": entry.RemoteWebmUrl ??= fmt.Url; break;
            }
            entry.OriginalContentType ??= fmt.ContentType;
        }

        // For the animated-image slot, prefer WebP (~2× smaller than GIF) and fall back to GIF.
        // Stored in RemoteGifUrl regardless of format — the field is "URL of an animated image",
        // and the <img> tag plays both formats transparently.
        entry.RemoteGifUrl = formats.FirstOrDefault(f => f.ContentType == "image/webp")?.Url
                          ?? formats.FirstOrDefault(f => f.ContentType == "image/gif")?.Url;

        // Ensure dims if provider didn't give them at the top level — pull from any format.
        if (entry.Width == 0 || entry.Height == 0)
        {
            var anyDims = formats.FirstOrDefault(f => f.Width > 0 && f.Height > 0);
            if (anyDims != null) { entry.Width = anyDims.Width; entry.Height = anyDims.Height; }
        }

        AppendTag(entry, query);

        await PersistNewEntryAsync(entry);
        IndexEntry(entry);
        OnGifLibraryChanged?.Invoke(entry);
        return entry;
    }

    private void IndexEntry(GifEntry entry)
    {
        _entries[entry.Id] = entry;
        if (!string.IsNullOrEmpty(entry.SourceProviderId) && !string.IsNullOrEmpty(entry.SourceId))
            _byProviderSourceId[(entry.SourceProviderId, entry.SourceId)] = entry.Id;
        IndexEntryTags(entry);
    }

    private void IndexEntryTags(GifEntry entry)
    {
        var tags = DeserializeTags(entry.Tags);
        if (tags.Count == 0) return;
        lock (_tagIndexLock)
        {
            foreach (var tag in tags)
            {
                if (!_tagIndex.TryGetValue(tag, out var set))
                    _tagIndex[tag] = set = new HashSet<Guid>();
                set.Add(entry.Id);
            }
        }
    }

    private static bool NeedsBackgroundNormalization(GifEntry entry)
    {
        if (entry.SourceProviderId == null) return false; // Custom uploads are already normalized.
        if (entry.DeletedAt != null) return false;
        if (!string.IsNullOrEmpty(entry.GifUrl)) return false; // Local animated image already present.
        // We render via <img>; need to produce a local animated image from whatever the provider
        // gave us — a remote webp/gif, or (failing that) a remote mp4/webm we transcode to webp.
        return !string.IsNullOrEmpty(entry.RemoteGifUrl)
            || !string.IsNullOrEmpty(entry.RemoteMp4Url)
            || !string.IsNullOrEmpty(entry.RemoteWebmUrl);
    }

    private void QueueNormalization(GifEntry entry)
    {
        if (entry.SourceProviderId == null || string.IsNullOrEmpty(entry.SourceId)) return;
        var dedupKey = $"{entry.SourceProviderId}:{entry.SourceId}";
        if (!_inFlightDownloads.TryAdd(dedupKey, 0)) return;

        _ = Task.Run(async () =>
        {
            try { await NormalizeProviderEntryAsync(entry); }
            catch (Exception ex) { _logger.LogWarning(ex, "GIF normalization failed for {EntryId}", entry.Id); }
            finally { _inFlightDownloads.TryRemove(dedupKey, out _); }
        });
    }

    private async Task NormalizeProviderEntryAsync(GifEntry entry)
    {
        // Chat renders provider GIFs via <img> — animated WebP/GIF in img tags has no autoplay
        // policy (instant playback on page load), unlike <video> which Chrome's MEI can block until
        // first user gesture. We serve from our own /gif-cache instead of hammering the provider's CDN.
        if (!string.IsNullOrEmpty(entry.RemoteGifUrl))
        {
            // Provider gave us an animated image (webp/gif) — download it as-is. The file extension
            // is taken from the URL so the static-file middleware sets the right Content-Type.
            var ext = GuessExtensionFromUrl(entry.RemoteGifUrl);
            var localPath = Path.Combine(CacheDir, $"{entry.Id}{ext}");
            if (!File.Exists(localPath))
            {
                var ok = await DownloadAsync(entry.RemoteGifUrl, localPath, CancellationToken.None);
                if (!ok) { MarkFailed(entry); return; }
            }
            entry.GifUrl = $"/gif-cache/{entry.Id}{ext}";
            entry.FileSizeBytes = SafeFileSize(localPath);
        }
        else
        {
            // Video-only item (no webp/gif): download the mp4/webm and transcode it to an animated
            // WebP so it still renders in an <img> instead of hot-linking the CDN via <video>.
            // Without ffmpeg we leave GifUrl null and fall back to the remote <video> at render time.
            string videoUrl, srcExt;
            if (!string.IsNullOrEmpty(entry.RemoteMp4Url)) { videoUrl = entry.RemoteMp4Url; srcExt = ".mp4"; }
            else if (!string.IsNullOrEmpty(entry.RemoteWebmUrl)) { videoUrl = entry.RemoteWebmUrl; srcExt = ".webm"; }
            else return;

            if (!GifFfmpegHelper.IsAvailable) return; // No transcoder; render falls back to remote <video>.

            var webpPath = Path.Combine(CacheDir, $"{entry.Id}.webp");
            if (!File.Exists(webpPath))
            {
                // Intermediate video lives in the system temp dir, not the web-served cache.
                var tempVideo = Path.Combine(Path.GetTempPath(), $"gifsrc-{entry.Id}{srcExt}");
                try
                {
                    if (!await DownloadAsync(videoUrl, tempVideo, CancellationToken.None)) { MarkFailed(entry); return; }
                    if (!await _ffmpeg.TranscodeToAnimatedWebpAsync(tempVideo, webpPath, CancellationToken.None)) { MarkFailed(entry); return; }
                }
                finally { TryDelete(tempVideo); }
            }
            entry.GifUrl = $"/gif-cache/{entry.Id}.webp";
            entry.FileSizeBytes = SafeFileSize(webpPath);
        }

        entry.TranscodeStatus |= GifTranscodeStatus.DoneGif;
        await PersistFormatsAsync(entry);
        OnGifEntryUpdated?.Invoke(entry.Id);
    }

    /// <summary>
    /// Pulls the file extension off the URL path (handles query strings / fragments). Falls back
    /// to .webp when nothing looks right.
    /// </summary>
    private static string GuessExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.Absolute).AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch { ".gif" => ".gif", ".webp" => ".webp", _ => ".webp" };
        }
        catch
        {
            return ".webp";
        }
    }

    private async Task<bool> DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            if (!await NetworkSecurityHelper.IsPublicHostAsync(uri.Host))
            {
                _logger.LogWarning("Refusing to download GIF from non-public host {Host}", uri.Host);
                return false;
            }

            var client = _httpClientFactory.CreateClient("Klipy");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeoutMs);

            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GIF download HTTP {Status} for {Url}", response.StatusCode, url);
                return false;
            }
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                _logger.LogWarning("GIF too large ({Size}B) for {Url}", response.Content.Headers.ContentLength, url);
                return false;
            }

            // Stream to disk with a hard cap so a chunked/headerless response (Content-Length
            // absent) can't blow past MaxDownloadBytes — the header check above only helps when
            // the length is advertised.
            bool overCap = false;
            await using (var fs = File.Create(destPath))
            await using (var src = await response.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, cts.Token)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) { overCap = true; break; }
                    await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                }
            }

            if (overCap)
            {
                TryDelete(destPath);
                _logger.LogWarning("GIF exceeded size cap mid-download: {Url}", url);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GIF download failed: {Url}", url);
            TryDelete(destPath);
            return false;
        }
    }

    private void MarkFailed(GifEntry entry)
    {
        entry.TranscodeStatus |= GifTranscodeStatus.Failed;
        _dirtyEntries.TryAdd(entry.Id, 0);
    }

    private void TouchUsage(GifEntry entry, string? query)
    {
        lock (entry)
        {
            entry.UseCount++;
            entry.LastUsedAt = DateTime.UtcNow;
        }
        AppendTag(entry, query);
        _dirtyEntries.TryAdd(entry.Id, 0);
    }

    private void AppendTag(GifEntry entry, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var tag = query.Trim().ToLowerInvariant();
        if (tag.Length < 2 || tag.Length > 64) return;

        var tags = DeserializeTags(entry.Tags);
        if (tags.Contains(tag)) return;

        tags.Add(tag);
        entry.Tags = JsonSerializer.Serialize(tags);

        lock (_tagIndexLock)
        {
            if (!_tagIndex.TryGetValue(tag, out var set))
                _tagIndex[tag] = set = new HashSet<Guid>();
            set.Add(entry.Id);
        }
    }

    private static List<string> DeserializeTags(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private void AddToRecents(Guid userId, string username, Guid gifEntryId)
    {
        var recents = _userService.GetRecentGifs(username);
        recents.Remove(gifEntryId);
        recents.Insert(0, gifEntryId);
        if (recents.Count > MaxRecentGifs) recents = recents.Take(MaxRecentGifs).ToList();
        _userService.UpdateRecentGifs(userId, recents);
    }

    #endregion

    #region DB persistence

    private async Task PersistNewEntryAsync(GifEntry entry)
    {
        if (_dbFactory == null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.GifEntries.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist GifEntry {Id}", entry.Id);
        }
    }

    private async Task PersistFormatsAsync(GifEntry entry)
    {
        if (_dbFactory == null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.GifEntries
                .Where(g => g.Id == entry.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(g => g.Mp4Url, entry.Mp4Url)
                    .SetProperty(g => g.WebmUrl, entry.WebmUrl)
                    .SetProperty(g => g.GifUrl, entry.GifUrl)
                    .SetProperty(g => g.FileSizeBytes, entry.FileSizeBytes)
                    .SetProperty(g => g.TranscodeStatus, entry.TranscodeStatus));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist GifEntry formats {Id}", entry.Id);
        }
    }

    private void StartFlushLoop()
    {
        if (_dbFactory == null) return;
        _flushCts = new CancellationTokenSource();
        _ = FlushLoopAsync(_flushCts.Token);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await FlushDirtyAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushDirtyAsync()
    {
        if (_dirtyEntries.IsEmpty || _dbFactory == null) return;

        var ids = _dirtyEntries.Keys.ToList();
        foreach (var id in ids) _dirtyEntries.TryRemove(id, out _);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            foreach (var id in ids)
            {
                if (!_entries.TryGetValue(id, out var entry)) continue;
                await db.GifEntries
                    .Where(g => g.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(g => g.UseCount, entry.UseCount)
                        .SetProperty(g => g.LastUsedAt, entry.LastUsedAt)
                        .SetProperty(g => g.ReferenceCount, entry.ReferenceCount)
                        .SetProperty(g => g.Tags, entry.Tags)
                        .SetProperty(g => g.TranscodeStatus, entry.TranscodeStatus));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush {Count} dirty GIF entries", ids.Count);
        }
    }

    #endregion

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
