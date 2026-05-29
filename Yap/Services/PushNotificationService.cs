using System.Text.Json;
using WebPush;

namespace Yap.Services;

/// <summary>
/// Delegating handler that logs HTTP request/response details for push notifications.
/// </summary>
public class PushLogHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    // TEMP DIAGNOSTIC: the configured VAPID public key, used to verify the outgoing JWT signature.
    private readonly string? _configuredPublicKey;

    public PushLogHandler(ILogger logger, string? configuredPublicKey = null) : base(new SocketsHttpHandler
    {
        // The push HttpClient is a singleton (lives for the app's lifetime). Recycle pooled
        // connections so a silently-dropped keep-alive to a push endpoint can't hang the next
        // send — stale pooled connections caused 100s timeouts that delayed badge updates.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        _logger = logger;
        _configuredPublicKey = configuredPublicKey;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // ===== TEMP DIAGNOSTIC (push BadJwtToken investigation) — remove when resolved =====
        var nowUtc = DateTime.UtcNow;
        var host = request.RequestUri?.Host;
        var jwt = ExtractVapidJwt(request);
        _logger.LogWarning("PUSH-DIAG ▶ host={Host} serverUtcNow={NowUtc:o} {Claims} {Verify}",
            host, nowUtc, DescribeJwt(jwt, nowUtc), VerifySignature(jwt, _configuredPublicKey));
        // ===================================================================================

        _logger.LogDebug("Push HTTP {Method} {Url}", request.Method, request.RequestUri);

        var response = await base.SendAsync(request, cancellationToken);

        _logger.LogDebug("Push HTTP {StatusCode} ({StatusInt}) from {Url}",
            response.StatusCode, (int)response.StatusCode, request.RequestUri);

        // ===== TEMP DIAGNOSTIC: compare the push service's own clock (its Date header) to ours =====
        var serviceDate = response.Headers.Date;
        long? serverMinusServiceSec = serviceDate.HasValue
            ? (long)(DateTime.UtcNow - serviceDate.Value.UtcDateTime).TotalSeconds
            : null;
        _logger.LogWarning("PUSH-DIAG ◀ host={Host} status={Status}({Int}) pushServiceDate={ServiceDate:o} serverMinusService={SkewSec}s",
            host, response.StatusCode, (int)response.StatusCode, serviceDate?.UtcDateTime, serverMinusServiceSec);
        // ==========================================================================================

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Push HTTP error body: {Body}", body);
        }

        return response;
    }

    // ----- TEMP DIAGNOSTIC helpers (push BadJwtToken investigation) — remove when resolved -----

    /// <summary>Pulls the VAPID JWT out of the Authorization header ("vapid t=&lt;jwt&gt;, k=&lt;key&gt;" or "WebPush &lt;jwt&gt;").</summary>
    private static string? ExtractVapidJwt(HttpRequestMessage request)
    {
        var auth = request.Headers.Authorization;
        string? raw = auth?.Parameter;
        if (string.IsNullOrEmpty(raw) && request.Headers.TryGetValues("Authorization", out var vals))
            foreach (var v in vals) { raw = v; break; }
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Newer VAPID: "...t=<jwt>, k=<key>" → return the t= value.
        foreach (var piece in raw.Split(','))
        {
            var seg = piece.Trim();
            var i = seg.IndexOf("t=", StringComparison.Ordinal);
            if (i >= 0) return seg.Substring(i + 2).Trim();
        }
        // Older: "WebPush <jwt>" (scheme already split into auth.Scheme) or a bare token.
        var sp = raw.IndexOf(' ');
        return (sp >= 0 ? raw.Substring(sp + 1) : raw).Trim();
    }

    /// <summary>Decodes the JWT payload: exp/aud/sub + seconds-until-expiry (should be ~43200 = 12h; negative/odd ⇒ clock skew).</summary>
    private static string DescribeJwt(string? jwt, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(jwt)) return "jwt=(none)";
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return "jwt=(malformed)";
            var payload = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var aud = root.TryGetProperty("aud", out var a) ? a.GetString() : "(none)";
            var sub = root.TryGetProperty("sub", out var s) ? s.GetString() : "(none)";
            var exp = root.TryGetProperty("exp", out var e) ? e.GetInt64() : 0;
            var expUtc = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
            var secs = (long)(expUtc - nowUtc).TotalSeconds;
            return $"aud={aud} sub={sub} exp={expUtc:o} secsUntilExp={secs}(expect~43200)";
        }
        catch (Exception ex)
        {
            return $"jwt=(decode-failed:{ex.Message})";
        }
    }

    /// <summary>
    /// Verifies the JWT's ES256 signature against the configured public key.
    /// PASS ⇒ keys pair and the signature is valid (a 403 is then clock/aud/Apple-strictness, NOT the keys).
    /// FAIL ⇒ signature doesn't match the configured public key (key mismatch, or a bad-signature library bug).
    /// </summary>
    private static string VerifySignature(string? jwt, string? configuredPublicKey)
    {
        if (string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(configuredPublicKey)) return "verify=skipped";
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return "verify=malformed-jwt";
            var signingInput = System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var sig = Base64UrlDecode(parts[2]);
            var pub = Base64UrlDecode(configuredPublicKey);
            if (pub.Length != 65 || pub[0] != 0x04) return $"verify=bad-pubkey-format(len={pub.Length})";
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
            {
                Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                Q = new System.Security.Cryptography.ECPoint { X = pub[1..33], Y = pub[33..65] }
            });
            var ok = ecdsa.VerifyData(signingInput, sig, System.Security.Cryptography.HashAlgorithmName.SHA256);
            return ok ? "verify=PASS(keys-pair,sig-valid)" : "verify=FAIL(sig!=configured-pubkey)";
        }
        catch (Exception ex)
        {
            return $"verify=error({ex.GetType().Name})";
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}

/// <summary>
/// Service for sending push notifications to users.
/// Used to notify users of DMs when they're not actively using the app.
/// </summary>
public class PushNotificationService
{
    private readonly VapidDetails? _vapidDetails;
    private readonly WebPushClient _webPushClient;
    private readonly PushSubscriptionStore _subscriptionStore;
    private readonly UserService _userService;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly bool _isConfigured;

    public PushNotificationService(
        IConfiguration configuration,
        PushSubscriptionStore subscriptionStore,
        UserService userService,
        ILogger<PushNotificationService> logger)
    {
        _subscriptionStore = subscriptionStore;
        _userService = userService;
        _logger = logger;

        // Load VAPID keys from configuration
        var subject = configuration["Vapid:Subject"];
        var publicKey = configuration["Vapid:PublicKey"];
        var privateKey = configuration["Vapid:PrivateKey"];

        // TEMP DIAGNOSTIC: pass the configured public key into the handler so it can verify the
        // outgoing VAPID JWT signature (separates key-mismatch from clock skew from signature bug).
        var httpClient = new HttpClient(new PushLogHandler(logger, publicKey))
        {
            // Push sends should be fast (push services respond in ~1s). The 100s default let a single
            // stale connection delay a notification — and the badge it carries — by up to 100 seconds.
            Timeout = TimeSpan.FromSeconds(20)
        };
        _webPushClient = new WebPushClient(httpClient);

        // Check if VAPID is properly configured
        if (!string.IsNullOrEmpty(subject) &&
            !string.IsNullOrEmpty(publicKey) &&
            !string.IsNullOrEmpty(privateKey) &&
            !publicKey.Contains("GENERATE_YOUR_OWN"))
        {
            _vapidDetails = new VapidDetails(subject, publicKey, privateKey);
            _isConfigured = true;
            _logger.LogInformation("Push notifications configured with VAPID");
        }
        else
        {
            _isConfigured = false;
            _logger.LogWarning("Push notifications not configured - VAPID keys missing or placeholder values");
        }
    }

    /// <summary>
    /// Gets the VAPID public key for client-side subscription.
    /// </summary>
    public string? GetPublicKey() => _vapidDetails?.PublicKey;

    /// <summary>
    /// Whether push notifications are properly configured.
    /// </summary>
    public bool IsConfigured => _isConfigured;

    /// <summary>
    /// Number of push subscriptions registered for a user (diagnostic + Settings display).
    /// </summary>
    public int GetSubscriptionCount(string username) =>
        _subscriptionStore.GetSubscriptions(username).Count();

    /// <summary>
    /// Send a push notification to a specific user.
    /// </summary>
    public async Task<PushSendResult> SendToUserAsync(string username, PushPayload payload, bool bypassMute = false)
    {
        if (!_isConfigured || _vapidDetails == null)
        {
            _logger.LogDebug("Push not configured, skipping notification to {Username}", username);
            return new PushSendResult(0, 0, 0);
        }

        // Check if user has muted banner notifications (badge still sent).
        // bypassMute is used for the explicit "Send test notification" action so the user
        // can verify delivery even while muted.
        var user = _userService.GetByUsername(username);
        if (!bypassMute && user?.PushMuted == true)
        {
            payload = payload with { Muted = true };
            _logger.LogDebug("Push muted for {Username}, sending badge-only", username);
        }

        var subscriptions = _subscriptionStore.GetSubscriptions(username).ToList();
        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No push subscriptions for user {Username}", username);
            return new PushSendResult(0, 0, 0);
        }

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        int sent = 0, failed = 0;
        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await _webPushClient.SendNotificationAsync(pushSubscription, json, _vapidDetails);
                _logger.LogDebug("Push sent to {Username} at {Endpoint}", username, sub.Endpoint[..Math.Min(50, sub.Endpoint.Length)]);
                sent++;
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                                               ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Subscription expired or invalid - remove it
                _logger.LogInformation("Removing expired push subscription for {Username}", username);
                await _subscriptionStore.RemoveSubscriptionAsync(sub.Endpoint);
                failed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push to {Username}", username);
                failed++;
            }
        }

        _logger.LogDebug("Push to {Username}: sent={Sent} failed={Failed} total={Total}", username, sent, failed, subscriptions.Count);
        return new PushSendResult(sent, failed, subscriptions.Count);
    }

    /// <summary>
    /// Send a DM notification to a user.
    /// </summary>
    public Task<PushSendResult> SendDmNotificationAsync(string toUsername, string fromUsername, string messagePreview, int unreadCount)
    {
        _logger.LogDebug("SendDmNotification: to={To} from={From} unreadCount={UnreadCount} preview={Preview}",
            toUsername, fromUsername, unreadCount, messagePreview.Length > 30 ? messagePreview[..27] + "..." : messagePreview);

        var payload = new PushPayload
        {
            Title = $"DM from {fromUsername}",
            Body = messagePreview.Length > 100 ? messagePreview[..97] + "..." : messagePreview,
            Icon = "/icon-192.png",
            Badge = "/icon-192.png",
            Tag = $"dm-{fromUsername}",
            Url = $"/dm/{Uri.EscapeDataString(fromUsername)}",
            UnreadCount = unreadCount
        };

        return SendToUserAsync(toUsername, payload);
    }

    /// <summary>
    /// Sends a test notification to all of a user's devices. Used from Settings as an
    /// "is this subscription alive?" check — the device that buzzes is alive. Bypasses mute
    /// so the user sees the banner even while notifications are muted.
    /// </summary>
    public Task<PushSendResult> SendTestAsync(string username)
    {
        var payload = new PushPayload
        {
            Title = "Yap test notification",
            Body = "If you can see this, push notifications work on this device.",
            Icon = "/icon-192.png",
            Badge = "/icon-192.png",
            Tag = "yap-test",
            Url = "/",
            UnreadCount = 0
        };

        return SendToUserAsync(username, payload, bypassMute: true);
    }
}

/// <summary>
/// Result of a push send attempt across all of a user's devices.
/// </summary>
public record PushSendResult(int Sent, int Failed, int Total);

/// <summary>
/// Push notification payload sent to the service worker.
/// </summary>
public record PushPayload
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Icon { get; init; } = "/icon-192.png";
    public string Badge { get; init; } = "/icon-192.png";
    public string Tag { get; init; } = "chat";
    public string Url { get; init; } = "/";
    public int UnreadCount { get; init; }
    public bool Muted { get; init; }
}
