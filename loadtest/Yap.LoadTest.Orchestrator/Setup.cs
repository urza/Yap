using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

public sealed record SavedUser(string Username, string Token);

/// <summary>Tokens survive between runs in state/&lt;host&gt;.json; a token is a 365-day cookie, so a user never signs in twice.</summary>
public sealed class RunState
{
    public SavedUser? Admin { get; set; }
    public List<SavedUser> Users { get; set; } = [];

    public static string PathFor(Uri server, string dir) => Path.Combine(dir, $"{server.Host}_{server.Port}.json");
    public static RunState Load(string path) => File.Exists(path) ? JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), Json.Options) ?? new() : new();
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    }
}

/// <summary>Everything a run needs on the server before the first worker starts: the admin, the settings, the users, the rooms.</summary>
public sealed class Setup(YapApi api, RunState state, Action<string> log)
{
    public async Task<string> EnsureAdminAsync(string username, string? code, CancellationToken ct)
    {
        if (state.Admin is { } saved && await IsAdminAsync(saved.Token, ct)) return saved.Token;
        var result = await api.SignInAsync(username, code, ct);
        if (result.Status == HttpStatusCode.OK && result.Token is { } token)
        {
            if (!await IsAdminAsync(token, ct))
                throw new InvalidOperationException($"'{username}' is signed in but is not the admin. The first account created on a server is its admin; on a fresh test instance start the orchestrator before anyone else signs in, or pass --admin <the admin's username> --admin-code <its code>.");
            state.Admin = new SavedUser(username, token);
            log($"admin: signed in as {username}");
            return token;
        }
        var why = result.Status switch
        {
            HttpStatusCode.Unauthorized => $"the account '{username}' exists and needs its code: pass --admin-code with the last part of the admin's login link (Settings, Login Link); a legacy secret code works too.",
            HttpStatusCode.Accepted => "registration requires approval; approve the account in the admin page or edit Data/registration-settings.json.",
            HttpStatusCode.Forbidden => "registration is closed; open it in the admin page or edit Data/registration-settings.json.",
            _ => result.Detail ?? "unexpected answer",
        };
        throw new InvalidOperationException($"admin sign-in failed ({(int)result.Status}): {why}");
    }

    private async Task<bool> IsAdminAsync(string token, CancellationToken ct)
    {
        try { return (await api.GetAsync<BootstrapDto>("/api/bootstrap", token, ct))?.User?.IsAdmin == true; }
        catch { return false; }
    }

    /// <summary>
    /// The admin's login link, so the person running the test can open the admin page and watch the Diagnostics tab
    /// during the run. There is no other way in: the orchestrator holds the token, and a new account has no code.
    /// Null when the server cannot give one; the run does not need it.
    /// </summary>
    public async Task<string?> AdminLinkAsync(string admin, CancellationToken ct)
    {
        try
        {
            var link = await api.GetAsync<LinkDto>("/api/account/link", admin, ct);
            if (link is null)   // 204: an account without a link. The POST makes one; there is no old link to revoke.
            {
                using var made = await api.SendJsonAsync(HttpMethod.Post, "/api/account/link", new { }, admin, ct);
                link = made.IsSuccessStatusCode ? await made.Content.ReadFromJsonAsync<LinkDto>(Json.Options, ct) : null;
            }
            if (link?.Url is { } url) { log($"admin: login link {url} (opens the admin page; the Diagnostics tab shows the server load)"); return url; }
            log("admin: the server gave no login link");
        }
        catch (Exception e) when (e is not OperationCanceledException) { log($"admin: no login link ({e.Message})"); }
        return null;
    }

    /// <summary>
    /// Registration open and approval off, so accounts can be created. With <paramref name="previews"/> off (the default), the
    /// server does nothing with a shared link: the run measures the chat alone, with no yt-dlp or ffmpeg on the box and no
    /// requests to the platforms. On, link previews and media caching are on, so a link costs what it costs in production.
    /// </summary>
    public async Task ApplySettingsAsync(string admin, bool previews, CancellationToken ct)
    {
        var caching = previews;
        var body = new SettingsRequest(RegistrationClosed: false, RequireApproval: false, SmartLogin: null, LinkPreviews: previews, MediaCaching: caching);
        using var response = await api.SendJsonAsync(HttpMethod.Put, "/api/admin/settings", body, admin, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // The server refuses media caching when yt-dlp is not installed. The run goes on without it.
            log("settings: media caching is not available on this server (yt-dlp missing); running with it off");
            caching = false;
            using var retry = await api.SendJsonAsync(HttpMethod.Put, "/api/admin/settings", body with { MediaCaching = false }, admin, ct);
            if (!retry.IsSuccessStatusCode) throw new InvalidOperationException($"PUT /api/admin/settings -> {(int)retry.StatusCode} {await YapApi.ProblemDetail(retry, ct)}");
        }
        else if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"PUT /api/admin/settings -> {(int)response.StatusCode} {await YapApi.ProblemDetail(response, ct)}");
        log($"settings: registration open, approval off, previews {(previews ? "on" : "off")}, media caching {(caching ? "on" : "off")}");
    }

    /// <summary>Creates the accounts up to <paramref name="count"/> that are missing, eight at a time. Each sign-in is one POST; the token is the cookie.</summary>
    public async Task EnsureUsersAsync(string prefix, int count, CancellationToken ct, Action<int, int>? progress = null)
    {
        var have = state.Users.Select(u => u.Username).ToHashSet();
        var missing = Enumerable.Range(1, count).Select(i => $"{prefix}{i:D4}").Where(n => !have.Contains(n)).ToList();
        if (missing.Count == 0) return;
        log($"accounts: creating {missing.Count}");
        var gate = new SemaphoreSlim(8);
        var created = new System.Collections.Concurrent.ConcurrentBag<SavedUser>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        await Task.WhenAll(missing.Select(async name =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await api.SignInAsync(name, null, ct);
                if (r.Status == HttpStatusCode.OK && r.Token is { } token) created.Add(new SavedUser(name, token));
                else failures.Add($"{name}: {(int)r.Status} {r.Detail}");
                progress?.Invoke(Interlocked.Increment(ref done), missing.Count);
            }
            finally { gate.Release(); }
        }));
        state.Users.AddRange(created);
        state.Users = state.Users.OrderBy(u => u.Username, StringComparer.Ordinal).ToList();
        if (!failures.IsEmpty)
            throw new InvalidOperationException($"{failures.Count} accounts could not be created, e.g. {failures.First()}. A 401 means the name exists from an earlier run whose state file is gone: pass a new --prefix.");
        log($"accounts: {created.Count} created, {state.Users.Count} in the state file");
    }

    public async Task EnsureRoomsAsync(string admin, int rooms, CancellationToken ct)
    {
        if (rooms <= 1) { log("rooms: the lobby"); return; }
        var existing = (await api.GetAsync<BootstrapDto>("/api/bootstrap", admin, ct))?.Rooms?.Select(r => r.Name).ToHashSet() ?? [];
        var made = 0;
        for (var i = 1; i <= rooms; i++)
        {
            var name = $"lt-room-{i:D2}";
            if (existing.Contains(name)) continue;
            // Unlimited history and no since-joined cutoff: a restricted room sends every event per reader, which is a different, heavier test.
            using var response = await api.SendJsonAsync(HttpMethod.Post, "/api/rooms", new RoomRequest(name, "load test", AdminOnly: false), admin, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"POST /api/rooms {name} -> {(int)response.StatusCode} {await YapApi.ProblemDetail(response, ct)}");
            made++;
        }
        log($"rooms: {rooms} test rooms ({made} created)");
    }
}
