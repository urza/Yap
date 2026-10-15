# Authentication and Program.cs

This page specifies how a request gets its user, how the hub gets its user, how an admin is recognised, and how `Program.cs` composes the application. The sign-in pages and their routes (check, verify, invite, approval) are the subject of the next page, [Sign-in flows](sign-in.md).

## What stays the same

- The cookie. Name `yap_auth`. Value: the account's opaque token, 32 random bytes, base64url. Flags: HttpOnly, Secure, SameSite=Lax, MaxAge 365 days, Path `/`. Installed apps and current sessions depend on these bytes. Nothing changes.
- The token. `User.Token`, one per account, unique index, in UserService memory. All devices of one account hold the same token.
- The admin. The first human account. `UserService.IsAdmin(userId)`.
- The link token for the installed app (`/pwa-launch`, HMAC over user id, expiry and the hash of the auth token).

## What changes

Today `AuthMiddleware` looks up the cookie, fills a scoped `UserStateService`, and `AdminGate` reads that service. The new code uses the ASP.NET Core authentication pipeline. One handler builds a `ClaimsPrincipal`. `[Authorize]` and `RequireAuthorization()` do the rest. Three classes go away: `AuthMiddleware`, `AdminGate`, `UserStateService`.

### The handler

```cs
// Services/Accounts/YapCookieAuthHandler.cs (sketch)
public sealed class YapCookieAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
                                         UrlEncoder encoder, UserService users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "yap";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AuthCookie.Name, out var token)) return Task.FromResult(AuthenticateResult.NoResult());
        var user = users.ByToken(token);                                       // a dictionary lookup in memory
        if (user is null) return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(Scheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));  // SignalR's user identifier
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        if (users.IsAdmin(user.Id)) identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties p) { Response.StatusCode = 401; return Task.CompletedTask; }
    protected override Task HandleForbiddenAsync(AuthenticationProperties p)  { Response.StatusCode = 404; return Task.CompletedTask; }
}
```

The principal carries three claims: id, username, and the admin role. Display name, theme and the other account fields are not claims. The client gets them from the bootstrap. A challenge returns 401. A forbidden request returns 404, as `AdminGate` does today: a non-admin does not learn that a route exists.

```cs
public static class PrincipalExtensions
{
    public static Guid Id(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
```

### Where the rules apply

```cs
builder.Services.AddAuthentication(YapCookieAuthHandler.Scheme)
    .AddScheme<AuthenticationSchemeOptions, YapCookieAuthHandler>(YapCookieAuthHandler.Scheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admin", policy => policy.RequireRole("admin"));
```

| Surface | Rule |
| --- | --- |
| `/hub` | `[Authorize]` on `ChatHub`. The cookie travels with the negotiate request and the WebSocket upgrade. |
| `/api/...` | `MapGroup("/api").RequireAuthorization()`. Each area maps into this group. |
| `/api/admin/...` | `MapGroup("/api/admin").RequireAuthorization("admin")`. |
| anonymous routes | `.AllowAnonymous()` on the bootstrap (it answers both states), the sign-in routes, `/api/invite/{code}`, the VAPID key, `/api/push/delivered`, the beacon, the manifest, `/pwa-launch`, and the shell. |

SignalR's `IUserIdProvider` reads `ClaimTypes.NameIdentifier` by default. `Clients.User(userId)` therefore reaches every connection of the account without a user group. `Context.User.Id()` gives the hub its user in every method.

### The client's address

`IpHelper.GetClientIp` is the one reader of the client's address. It returns `RemoteIpAddress`. The forwarded-headers middleware runs first in the pipeline with `TrustedProxies.Options`: for a connection from a proxy in `ChatSettings:TrustedProxies` (addresses or CIDR networks; loopback always) it rewrites `RemoteIpAddress` and `Request.Scheme` from `X-Forwarded-For` and `X-Forwarded-Proto`, and removes the entries it consumed. An `X-Forwarded-For` that is still on the request came from a peer the server does not trust; the address is then unknown (`null`). Smart login (AU-09), the memory of known addresses, the IPv4 beacon and the logs all take "unknown" over a value the caller chose. `X-Real-IP` is never read. `CF-Connecting-IP` is read in one case: when the hop after the trusted proxies is a Cloudflare address (`CloudflareIps`: a built-in list, the last download in `Data/cloudflare-ips.json`, and a daily refresh from `ChatSettings:CloudflareIpsUrl`). Cloudflare sets that header itself and overwrites a visitor's value, and a Caddy or nginx between Cloudflare and Yap passes it through, so a Cloudflare deployment needs no Cloudflare entry anywhere, only the proxy next to Yap in `TrustedProxies`. Without this rule a username and a guessed address were a sign-in. A deployment behind a proxy that is not listed keeps running; `ProxyWatch` records the first such request, the log warns (at start when the list is empty, and at that request), the bot tells the admin, and the admin page shows a red notice with the peer to add, until the proxy is listed and the server restarted.

### The cookie helper

One static class owns the cookie bytes. The sign-in routes, the bootstrap, sign-out, `/pwa-launch` and `/auth/refresh-token` call it. No other code writes the cookie. The same class owns the sign-out mark: a second cookie that `Clear` sets and `Set` removes, so `/pwa-launch` knows that this jar signed out since the install (AU-22).

```cs
public static class AuthCookie
{
    public const string Name = "yap_auth";
    static readonly CookieOptions Options = new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,        // Lax on purpose: PWA launches and notification taps
        MaxAge = TimeSpan.FromDays(365), Path = "/", IsEssential = true,
    };
    public static void Set(HttpResponse r, string token) => r.Cookies.Append(Name, token, Options);
    public static void Clear(HttpResponse r) => r.Cookies.Delete(Name, Options);
}
```

The bootstrap sets the cookie again on every call. This slides the expiry (AU-18). Today every HTML GET does this. The bootstrap also records the known address for smart login (AU-09) and observes the public origin for links (AU-15).

### Sign out all other devices (AU-20)

```cs
// Endpoints/AccountEndpoints.cs (sketch)
group.MapPost("/devices/sign-out-others", (ClaimsPrincipal me, string deviceId, string? pushEndpoint, HttpResponse response,
                                           UserService users, PresenceService presence, PushSubscriptionStore push,
                                           IHubContext<ChatHub> hub) =>
{
    var token = users.RotateToken(me.Id());                  // old cookies fail the lookup from now on
    AuthCookie.Set(response, token);                         // the caller's browser keeps its session; its other tabs share the cookie
    users.ForgetKnownIps(me.Id());
    users.ReplacePassphraseIfAny(me.Id());
    push.RemoveAllExcept(me.Id(), pushEndpoint);             // NT-24
    foreach (var session in presence.SessionsOf(me.Id()).Where(s => s.DeviceId != deviceId))
    {
        _ = hub.Clients.Client(session.ConnectionId).SendAsync("signedOut");
        session.Abort();                                     // HubCallerContext.Abort, captured at connect
        presence.Remove(session.ConnectionId);
    }
    return Results.Ok(new { passphrase = users.Passphrase(me.Id()) });
});
```

The session stores an `Abort` action at connect. The server closes the other devices' connections at once. Their next request fails in the handler. Their next bootstrap returns 401. The client then shows the login page and removes the local data of the account (AU-21, GEN-05). The link token of the installed app depends on the hash of the auth token, so a rotation also ends the automatic first-launch sign-in (AU-20).

## Program.cs

`Program.cs` is the composition root and nothing else. Each area has one static class in `Endpoints/` with a `Map` method. Each service lives in its area folder. `Program.cs` stays short enough to read in one screen.

```cs
// Program.cs (sketch, abridged)
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.UseDataFolderOverride();                       // Data/appsettings.json replaces the built-in file (kept)
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);   // the TUS endpoint enforces the limit (kept)

builder.Services.AddAuthentication(YapCookieAuthHandler.Scheme)
    .AddScheme<AuthenticationSchemeOptions, YapCookieAuthHandler>(YapCookieAuthHandler.Scheme, null);
builder.Services.AddAuthorizationBuilder().AddPolicy("admin", p => p.RequireRole("admin"));
builder.Services.AddSignalR().AddHubOptions<ChatHub>(o => o.AddFilter<HubLatencyFilter>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; o.Providers.Add<BrotliCompressionProvider>(); });
builder.Services.AddProblemDetails();
builder.Services.AddCors(TusUpload.Policy);                          // kept

builder.Services.AddPersistence(builder.Configuration);             // DbContext factory and PersistenceWriter, only when enabled
builder.Services.AddChat();                                          // ChatService, ChatTexts, LinkPreview, MediaCache, SystemBot
builder.Services.AddPresence();                                      // PresenceService, also hosted for its sweep
builder.Services.AddAccounts();                                      // UserService, AccessLinks, LinkToken, RegistrationGate
builder.Services.AddNotifications();                                 // settings, push store, push service, audit
builder.Services.AddMedia(); builder.Services.AddGifs(); builder.Services.AddEmoji(); builder.Services.AddAdmin();
builder.Services.AddSingleton<ChatHubBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatHubBroadcaster>());

var app = builder.Build();
await app.Services.LoadStateAsync();                                 // the startup order of today: migrate, chat, users, links, GIFs, push, bot

app.UseExceptionHandler();                                           // ProblemDetails; no /Error page
app.UseResponseCompression();
app.UseCors();
app.UseHttpsRedirection();
app.UseBrandingOverrides();                                          // Data/branding/* ahead of wwwroot (kept)
app.UseYapStaticFiles();                                             // /uploads, /emoji-packs, /custom-emojis, /welcome-content, /media-cache, /gif-cache (kept)
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequestLoggingMiddleware>();                       // kept; the skip list loses /_blazor and gains /hub

app.MapHub<ChatHub>("/hub");
app.MapShell();                                                      // index.html with the build stamp, the welcome page
var api = app.MapGroup("/api").RequireAuthorization();
api.MapBootstrap(); api.MapAccount(); api.MapMessages(); api.MapRooms(); api.MapGifs(); api.MapPush(); api.MapEmoji();
api.MapGroup("/admin").RequireAuthorization("admin").MapAdmin();
app.MapSignIn(); app.MapTus(); app.MapPwa(); app.MapBeacon();        // their own auth rules, as today

app.Run();
```

As built, Program.cs registers the services in one flat list (the per-area `AddX` methods of the sketch were not needed: the list is short and reads top to bottom). All services are singletons. No scoped service remains. The hub and the endpoints receive the user from the principal, not from a service.

### Removed from Program.cs

| Today | New |
| --- | --- |
| `AddRazorComponents().AddInteractiveServerComponents()`, `CircuitOptions`, `RegisterPersistentService` | gone |
| `AddCircuitHandler<ChatCircuitHandler>`, `CircuitTracker`, `CircuitIdentity` | PresenceService |
| scoped `UserStateService`, `ChatNavigationState`, `ChatConfigService`, `EmojiService` | principal, client state, `ChatTexts` singleton, `EmojiCatalog` singleton |
| `UseAntiforgery` | gone; the API is JSON with SameSite cookies, the kept form POSTs already opt out |
| `UseExceptionHandler("/Error")`, `UseStatusCodePagesWithReExecute("/not-found")` | ProblemDetails for the API; the client shows "Not Found" for an unknown app route (SH-42) |
| `AuthMiddleware`, `DeviceDetectionMiddleware` | the handler; one User-Agent check at hub connect |
| `MapStaticAssets`, `MapRazorComponents<App>()` | the shell endpoint with the build stamp |
| `AddHttpContextAccessor` | gone; nothing reads the context outside a request |

### Added

- `AddAuthentication` and `AddAuthorization` with one scheme and one policy.
- `AddSignalR` with the latency filter.
- `AddResponseCompression` with Brotli, HTTPS included.
- `AddProblemDetails`, so a refusal has one JSON shape (GEN-02).
- `JsonStringEnumConverter`, so a status travels as `"away"`, not `1`. The same converter goes into the SignalR JSON protocol options.
- `PersistenceWriter` as a hosted service, when persistence is enabled.
- `PresenceService` as a hosted service, for the 10 s sweep.
- `ChatHubBroadcaster` as a hosted service. It subscribes to the service events at start and unsubscribes at stop.

## The layers, restated

| Layer | Holds | Does not hold |
| --- | --- | --- |
| `Endpoints/` | the hub, the broadcaster, one class per area with routes: bind, validate, call a service, map the result | rules |
| `Services/<area>/` | the rules, the memory, the events, the enqueue of writes | HTTP types, hub types |
| `Models/<area>/` | entities and wire DTOs with their `From` mappings | behaviour |
| `Middleware/` | request logging | authentication (it is a handler now) |
| `Configuration/` | settings classes bound from `appsettings.json` | |
| `Data/` | `ChatDbContext`, migrations | |

A service never references `HttpContext`, `ClaimsPrincipal` or `Hub`. An endpoint never contains a rule that the spec names. This rule keeps the services testable without a web host.
