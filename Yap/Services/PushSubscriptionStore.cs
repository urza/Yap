using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// In-memory storage for push subscriptions with database persistence.
/// Uses endpoint as unique key.
/// </summary>
public class PushSubscriptionStore
{
    private readonly ChatPersistenceService _persistence;

    // Endpoint -> Subscription (endpoint is unique per device/browser)
    private ConcurrentDictionary<string, PushSubscription> _subscriptions = new();

    public PushSubscriptionStore(ChatPersistenceService persistence)
    {
        _persistence = persistence;

        // Load subscriptions from database
        _ = LoadAsync();
    }

    public void SaveSubscription(string username, PushSubscriptionInfo subscription)
    {
        var entry = new PushSubscription
        {
            Username = username,
            Endpoint = subscription.Endpoint,
            P256dh = subscription.P256dh,
            Auth = subscription.Auth,
            CreatedAt = DateTime.UtcNow
        };

        _subscriptions[subscription.Endpoint] = entry;
        _ = _persistence.SavePushSubscriptionAsync(entry);
    }

    public void RemoveSubscription(string endpoint)
    {
        if (_subscriptions.TryRemove(endpoint, out _))
        {
            _ = _persistence.RemovePushSubscriptionAsync(endpoint);
        }
    }

    public void RemoveUserSubscriptions(string username)
    {
        var toRemove = _subscriptions
            .Where(kvp => kvp.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        if (toRemove.Count > 0)
        {
            foreach (var endpoint in toRemove)
            {
                _subscriptions.TryRemove(endpoint, out _);
            }

            _ = _persistence.RemovePushSubscriptionsByUsernameAsync(username);
        }
    }

    public IEnumerable<PushSubscriptionInfo> GetSubscriptions(string username)
    {
        return _subscriptions.Values
            .Where(e => e.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
            .Select(e => new PushSubscriptionInfo
            {
                Endpoint = e.Endpoint,
                P256dh = e.P256dh,
                Auth = e.Auth
            })
            .ToList();
    }

    public bool HasSubscription(string username)
    {
        return _subscriptions.Values
            .Any(e => e.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadAsync()
    {
        try
        {
            var entries = await _persistence.GetAllPushSubscriptionsAsync();
            _subscriptions = new ConcurrentDictionary<string, PushSubscription>(
                entries.ToDictionary(e => e.Endpoint, e => e));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PushStore] Failed to load from database: {ex.Message}");
        }
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
