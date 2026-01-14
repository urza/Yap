using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Yap.Configuration;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Persistent storage for push subscriptions.
/// When persistence is enabled, uses database. Otherwise, falls back to JSON file.
/// Uses endpoint as unique key.
/// </summary>
public class PushSubscriptionStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private readonly ChatPersistenceService _persistence;
    private readonly bool _useDatabase;

    // Endpoint -> Subscription (endpoint is unique per device/browser)
    private ConcurrentDictionary<string, PushSubscription> _subscriptions = new();

    public PushSubscriptionStore(
        IWebHostEnvironment env,
        ChatPersistenceService persistence,
        IOptions<PersistenceSettings> settings)
    {
        _persistence = persistence;
        _useDatabase = settings.Value.Enabled;

        // Keep file path for fallback
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "pushsubscriptions.json");

        // Load subscriptions (from DB or file)
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

        if (_useDatabase)
            _ = _persistence.SavePushSubscriptionAsync(entry);
        else
            Persist();
    }

    public void RemoveSubscription(string endpoint)
    {
        if (_subscriptions.TryRemove(endpoint, out _))
        {
            if (_useDatabase)
                _ = _persistence.RemovePushSubscriptionAsync(endpoint);
            else
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

            if (_useDatabase)
                _ = _persistence.RemovePushSubscriptionsByUsernameAsync(username);
            else
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

    private async Task LoadAsync()
    {
        if (_useDatabase)
        {
            await LoadFromDatabaseAsync();
        }
        else
        {
            LoadFromFile();
        }
    }

    private async Task LoadFromDatabaseAsync()
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
            // Fall back to file
            LoadFromFile();
        }
    }

    private void LoadFromFile()
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
                        _subscriptions = new ConcurrentDictionary<string, PushSubscription>(
                            entries.ToDictionary(
                                e => e.Endpoint,
                                e => new PushSubscription
                                {
                                    Username = e.Username,
                                    Endpoint = e.Endpoint,
                                    P256dh = e.P256dh,
                                    Auth = e.Auth,
                                    CreatedAt = DateTime.UtcNow
                                }));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PushStore] Failed to load from file: {ex.Message}");
        }
    }

    private void Persist()
    {
        try
        {
            lock (_fileLock)
            {
                var entries = _subscriptions.Values
                    .Select(s => new PushSubscriptionEntry
                    {
                        Username = s.Username,
                        Endpoint = s.Endpoint,
                        P256dh = s.P256dh,
                        Auth = s.Auth
                    })
                    .ToList();

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_filePath, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PushStore] Failed to persist to file: {ex.Message}");
        }
    }
}

/// <summary>
/// Legacy push subscription entry for JSON file compatibility.
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
