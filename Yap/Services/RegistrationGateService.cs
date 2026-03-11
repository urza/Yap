using System.Collections.Concurrent;
using System.Text.Json;

namespace Yap.Services;

/// <summary>
/// Singleton service that controls new user registration.
/// Two independent toggles:
/// - RegistrationClosed: blocks all new signups (login page shows "temporarily closed")
/// - RequireApproval: new users enter username, wait for admin approval before account creation
/// When both are on, closed takes precedence.
/// Settings persisted to Data/registration-settings.json. Pending users are in-memory only.
/// </summary>
public class RegistrationGateService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RegistrationGateService> _logger;

    private bool _registrationClosed;
    private bool _requireApproval;

    // Pending usernames waiting for admin approval: username -> request time
    private readonly ConcurrentDictionary<string, DateTime> _pendingUsers = new(StringComparer.OrdinalIgnoreCase);

    // Approved usernames (short-lived, consumed by Login.razor polling): username -> approval time
    private readonly ConcurrentDictionary<string, DateTime> _approvedUsers = new(StringComparer.OrdinalIgnoreCase);

    // Rejected usernames (short-lived, consumed by Login.razor polling): username -> rejection time
    private readonly ConcurrentDictionary<string, DateTime> _rejectedUsers = new(StringComparer.OrdinalIgnoreCase);

    public RegistrationGateService(IWebHostEnvironment env, ILogger<RegistrationGateService> logger)
    {
        _env = env;
        _logger = logger;
        LoadSettings();
    }

    private string SettingsFilePath => Path.Combine(_env.ContentRootPath, "Data", "registration-settings.json");

    public bool RegistrationClosed => _registrationClosed;
    public bool RequireApproval => _requireApproval;

    public async Task SetRegistrationClosedAsync(bool closed)
    {
        _registrationClosed = closed;
        await SaveSettingsAsync();
        _logger.LogInformation("Registration closed set to {Closed}", closed);
    }

    public async Task SetRequireApprovalAsync(bool require)
    {
        _requireApproval = require;
        await SaveSettingsAsync();
        _logger.LogInformation("Require approval set to {Require}", require);
    }

    /// <summary>
    /// Adds a username to the pending set. Returns false if already pending or already approved.
    /// </summary>
    public bool AddPendingUser(string username)
    {
        // Clean up any stale approved/rejected state for this username
        _approvedUsers.TryRemove(username, out _);
        _rejectedUsers.TryRemove(username, out _);

        return _pendingUsers.TryAdd(username, DateTime.UtcNow);
    }

    /// <summary>
    /// Approves a pending user. Moves from pending to approved set.
    /// </summary>
    public void ApproveUser(string username)
    {
        if (_pendingUsers.TryRemove(username, out _))
        {
            _approvedUsers[username] = DateTime.UtcNow;
            _logger.LogInformation("Approved pending user '{Username}'", username);
        }
    }

    /// <summary>
    /// Rejects a pending user. Moves from pending to rejected set.
    /// </summary>
    public void RejectUser(string username)
    {
        if (_pendingUsers.TryRemove(username, out _))
        {
            _rejectedUsers[username] = DateTime.UtcNow;
            _logger.LogInformation("Rejected pending user '{Username}'", username);
        }
    }

    public bool IsPending(string username) => _pendingUsers.ContainsKey(username);

    /// <summary>
    /// Checks if approved and consumes the approval (one-time read).
    /// </summary>
    public bool ConsumeApproval(string username) => _approvedUsers.TryRemove(username, out _);

    /// <summary>
    /// Checks if approved without consuming.
    /// </summary>
    public bool IsApproved(string username) => _approvedUsers.ContainsKey(username);

    /// <summary>
    /// Checks if rejected and consumes the rejection (one-time read).
    /// </summary>
    public bool ConsumeRejection(string username) => _rejectedUsers.TryRemove(username, out _);

    public bool IsRejected(string username) => _rejectedUsers.ContainsKey(username);

    /// <summary>
    /// Returns all pending users ordered by request time.
    /// </summary>
    public List<(string Username, DateTime RequestedAt)> GetPendingUsers()
    {
        return _pendingUsers
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(x => x.Value)
            .ToList();
    }

    #region JSON Persistence

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<RegistrationSettings>(json);
                if (settings != null)
                {
                    _registrationClosed = settings.RegistrationClosed;
                    _requireApproval = settings.RequireApproval;
                }
                _logger.LogInformation("Loaded registration settings: Closed={Closed}, Approval={Approval}",
                    _registrationClosed, _requireApproval);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load registration settings from {Path}", SettingsFilePath);
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new RegistrationSettings
            {
                RegistrationClosed = _registrationClosed,
                RequireApproval = _requireApproval
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save registration settings to {Path}", SettingsFilePath);
        }
    }

    private class RegistrationSettings
    {
        public bool RegistrationClosed { get; set; }
        public bool RequireApproval { get; set; }
    }

    #endregion
}
