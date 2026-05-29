using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// In-memory storage for push subscriptions with pluggable persistence.
/// Uses endpoint as unique key.
/// </summary>
public class PushSubscriptionStore
{
    private readonly IPushSubscriptionPersistence _persistence;
    private readonly ILogger<PushSubscriptionStore> _logger;

    // Endpoint -> Subscription (endpoint is unique per device/browser)
    private readonly ConcurrentDictionary<string, PushSubscription> _subscriptions = new();

    public PushSubscriptionStore(IPushSubscriptionPersistence persistence, ILogger<PushSubscriptionStore> logger)
    {
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Loads subscriptions from persistence. Call during app startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var entries = await _persistence.LoadAllAsync();
            foreach (var entry in entries)
            {
                _subscriptions[entry.Endpoint] = entry;
            }
            _logger.LogInformation("Loaded {Count} push subscriptions", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load push subscriptions");
        }
    }

    public async Task SaveSubscriptionAsync(string username, PushSubscriptionInfo subscription)
    {
        var existing = _subscriptions.GetValueOrDefault(subscription.Endpoint);
        var isNew = existing == null;

        var entry = new PushSubscription
        {
            Username = username,
            Endpoint = subscription.Endpoint,
            P256dh = subscription.P256dh,
            Auth = subscription.Auth,
            // Preserve the original subscribe time on re-save (e.g. the silent re-subscribe that runs
            // on every load) so the Settings "Added X ago" stays stable instead of resetting each time.
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow
        };

        _subscriptions[subscription.Endpoint] = entry;

        _logger.LogDebug("SaveSubscription: {Action} subscription for {Username}, endpoint={Endpoint}, total={Total}",
            isNew ? "new" : "updated", username, subscription.Endpoint[..Math.Min(50, subscription.Endpoint.Length)], _subscriptions.Count);

        try
        {
            await _persistence.SaveAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist push subscription for {Username}", username);
        }
    }

    public async Task RemoveSubscriptionAsync(string endpoint)
    {
        if (_subscriptions.TryRemove(endpoint, out var removed))
        {
            _logger.LogDebug("RemoveSubscription: removed for {Username}, endpoint={Endpoint}, remaining={Total}",
                removed.Username, endpoint[..Math.Min(50, endpoint.Length)], _subscriptions.Count);

            try
            {
                await _persistence.RemoveAsync(endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove push subscription");
            }
        }
        else
        {
            _logger.LogDebug("RemoveSubscription: endpoint not found, endpoint={Endpoint}", endpoint[..Math.Min(50, endpoint.Length)]);
        }
    }

    public async Task RemoveUserSubscriptionsAsync(string username)
    {
        var toRemove = _subscriptions
            .Where(kvp => kvp.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        _logger.LogDebug("RemoveUserSubscriptions: removing {Count} subscriptions for {Username}", toRemove.Count, username);

        if (toRemove.Count > 0)
        {
            foreach (var endpoint in toRemove)
            {
                _subscriptions.TryRemove(endpoint, out _);
            }

            try
            {
                await _persistence.RemoveByUsernameAsync(username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove push subscriptions for {Username}", username);
            }
        }
    }

    public IEnumerable<PushSubscriptionInfo> GetSubscriptions(string username)
    {
        var subs = _subscriptions.Values
            .Where(e => e.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
            .Select(e => new PushSubscriptionInfo
            {
                Endpoint = e.Endpoint,
                P256dh = e.P256dh,
                Auth = e.Auth
            })
            .ToList();

        _logger.LogDebug("GetSubscriptions: {Count} subscriptions for {Username}", subs.Count, username);
        return subs;
    }

    /// <summary>
    /// Gets the full subscription records for a user (including Endpoint and CreatedAt),
    /// ordered oldest-first. Used by the Settings page to list a user's notification devices.
    /// </summary>
    public IReadOnlyList<PushSubscription> GetSubscriptionsDetailed(string username) =>
        _subscriptions.Values
            .Where(e => e.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CreatedAt)
            .ToList();

    public bool HasSubscription(string username)
    {
        return _subscriptions.Values
            .Any(e => e.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Push subscription info from browser (without username).
/// </summary>
public record PushSubscriptionInfo
{
    public string Endpoint { get; init; } = "";
    public string P256dh { get; init; } = "";
    public string Auth { get; init; } = "";
}
