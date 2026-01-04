using System.Collections.Concurrent;

namespace Yap.Services;

/// <summary>
/// In-memory storage for push subscriptions.
/// Maps username to their push subscription(s).
/// </summary>
public class PushSubscriptionStore
{
    // Username -> List of subscriptions (user may have multiple devices)
    private readonly ConcurrentDictionary<string, List<PushSubscriptionInfo>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public void SaveSubscription(string username, PushSubscriptionInfo subscription)
    {
        _subscriptions.AddOrUpdate(
            username,
            _ => new List<PushSubscriptionInfo> { subscription },
            (_, existing) =>
            {
                // Remove existing subscription with same endpoint (re-subscription)
                existing.RemoveAll(s => s.Endpoint == subscription.Endpoint);
                existing.Add(subscription);
                return existing;
            });
    }

    public void RemoveSubscription(string endpoint)
    {
        foreach (var kvp in _subscriptions)
        {
            kvp.Value.RemoveAll(s => s.Endpoint == endpoint);
        }
    }

    public void RemoveUserSubscriptions(string username)
    {
        _subscriptions.TryRemove(username, out _);
    }

    public IEnumerable<PushSubscriptionInfo> GetSubscriptions(string username)
    {
        return _subscriptions.TryGetValue(username, out var subs)
            ? subs.ToList()
            : Enumerable.Empty<PushSubscriptionInfo>();
    }

    public bool HasSubscription(string username)
    {
        return _subscriptions.TryGetValue(username, out var subs) && subs.Count > 0;
    }
}

/// <summary>
/// Push subscription information from the browser.
/// </summary>
public record PushSubscriptionInfo
{
    public string Endpoint { get; init; } = "";
    public string P256dh { get; init; } = "";
    public string Auth { get; init; } = "";
}
