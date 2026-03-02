using System.Text.Json;
using WebPush;

namespace Yap.Services;

/// <summary>
/// Delegating handler that logs HTTP request/response details for push notifications.
/// </summary>
public class PushLogHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public PushLogHandler(ILogger logger) : base(new HttpClientHandler())
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

        var httpClient = new HttpClient(new PushLogHandler(logger));
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
    /// Send a push notification to a specific user.
    /// </summary>
    public async Task SendToUserAsync(string username, PushPayload payload)
    {
        if (!_isConfigured || _vapidDetails == null)
        {
            _logger.LogDebug("Push not configured, skipping notification to {Username}", username);
            return;
        }

        // Check if user has muted banner notifications (badge still sent)
        var user = _userService.GetByUsername(username);
        if (user?.PushMuted == true)
        {
            payload = payload with { Muted = true };
            _logger.LogDebug("Push muted for {Username}, sending badge-only", username);
        }

        var subscriptions = _subscriptionStore.GetSubscriptions(username).ToList();
        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No push subscriptions for user {Username}", username);
            return;
        }

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await _webPushClient.SendNotificationAsync(pushSubscription, json, _vapidDetails);
                _logger.LogDebug("Push sent to {Username} at {Endpoint}", username, sub.Endpoint[..Math.Min(50, sub.Endpoint.Length)]);
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                                               ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Subscription expired or invalid - remove it
                _logger.LogInformation("Removing expired push subscription for {Username}", username);
                await _subscriptionStore.RemoveSubscriptionAsync(sub.Endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push to {Username}", username);
            }
        }
    }

    /// <summary>
    /// Send a DM notification to a user.
    /// </summary>
    public Task SendDmNotificationAsync(string toUsername, string fromUsername, string messagePreview, int unreadCount)
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
}

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
