using System.Text.Json;
using WebPush;

namespace Yap.Services;

/// <summary>
/// Delegating handler that logs HTTP request/response details for push notifications.
/// </summary>
public class PushLogHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public PushLogHandler(ILogger logger) : base(new SocketsHttpHandler
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
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Push HTTP {Method} {Url}", request.Method, request.RequestUri);

        var response = await base.SendAsync(request, cancellationToken);

        _logger.LogDebug("Push HTTP {StatusCode} ({StatusInt}) from {Url}",
            response.StatusCode, (int)response.StatusCode, request.RequestUri);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Push HTTP error body: {Body}", body);
        }

        return response;
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

        var httpClient = new HttpClient(new PushLogHandler(logger))
        {
            // Push sends should be fast (push services respond in ~1s). The 100s default let a single
            // stale connection delay a notification — and the badge it carries — by up to 100 seconds.
            Timeout = TimeSpan.FromSeconds(20)
        };
        _webPushClient = new WebPushClient(httpClient);

        // Load VAPID keys from configuration
        var subject = configuration["Vapid:Subject"];
        var publicKey = configuration["Vapid:PublicKey"];
        var privateKey = configuration["Vapid:PrivateKey"];

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
