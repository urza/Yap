using System.Collections.Concurrent;
using System.Text.Json;

namespace Yap.Services;

/// <summary>
/// Persistent storage for push subscriptions.
/// Uses endpoint as unique key, persists to JSON file.
/// </summary>
public class PushSubscriptionStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();

    // Endpoint -> Subscription (endpoint is unique per device/browser)
    private ConcurrentDictionary<string, PushSubscriptionEntry> _subscriptions = new();

    public PushSubscriptionStore(IWebHostEnvironment env)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "pushsubscriptions.json");
        Load();
    }

    public void SaveSubscription(string username, PushSubscriptionInfo subscription)
    {
        var entry = new PushSubscriptionEntry
        {
            Username = username,
            Endpoint = subscription.Endpoint,
            P256dh = subscription.P256dh,
            Auth = subscription.Auth
        };

        _subscriptions[subscription.Endpoint] = entry;
        Persist();
    }

    public void RemoveSubscription(string endpoint)
    {
        if (_subscriptions.TryRemove(endpoint, out _))
        {
            Persist();
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
            Persist();
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

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                lock (_fileLock)
                {
                    var json = File.ReadAllText(_filePath);
                    var entries = JsonSerializer.Deserialize<List<PushSubscriptionEntry>>(json);
                    if (entries != null)
                    {
                        _subscriptions = new ConcurrentDictionary<string, PushSubscriptionEntry>(
                            entries.ToDictionary(e => e.Endpoint, e => e));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PushStore] Failed to load subscriptions: {ex.Message}");
        }
    }

    private void Persist()
    {
        try
        {
            lock (_fileLock)
            {
                var entries = _subscriptions.Values.ToList();
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_filePath, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PushStore] Failed to persist subscriptions: {ex.Message}");
        }
    }
}

/// <summary>
/// Push subscription entry stored in JSON.
/// </summary>
public record PushSubscriptionEntry
{
    public string Username { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string P256dh { get; init; } = "";
    public string Auth { get; init; } = "";
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
