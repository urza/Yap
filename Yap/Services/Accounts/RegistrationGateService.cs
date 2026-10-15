using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yap.Services.Accounts;

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
    private bool _smartMode = true;   // on by default (AU-09); the settings file can turn it off

    // A request carries the token of the browser that made it (a cookie, SignInEndpoints). The admin approves a
    // username; only the browser with the token turns the approval into an account. Without the token, whoever typed
    // the username first after the approval got the account (AU-05).
    private sealed record Request(DateTime At, string Token);

    // Pending usernames waiting for admin approval
    private readonly ConcurrentDictionary<string, Request> _pendingUsers = new(StringComparer.OrdinalIgnoreCase);

    // Approved usernames (short-lived, consumed by the requesting browser's next sign-in)
    private readonly ConcurrentDictionary<string, Request> _approvedUsers = new(StringComparer.OrdinalIgnoreCase);

    // Rejected usernames (short-lived, consumed by the requesting browser's polling of GET /api/auth/pending)
    private readonly ConcurrentDictionary<string, Request> _rejectedUsers = new(StringComparer.OrdinalIgnoreCase);

    public RegistrationGateService(IWebHostEnvironment env, ILogger<RegistrationGateService> logger)
    {
        _env = env;
        _logger = logger;
        LoadSettings();
    }

    private string SettingsFilePath => Path.Combine(_env.ContentRootPath, "Data", "registration-settings.json");

    public bool RegistrationClosed => _registrationClosed;
    public bool RequireApproval => _requireApproval;
    public bool SmartMode => _smartMode;

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

    public async Task SetSmartModeAsync(bool enabled)
    {
        _smartMode = enabled;
        await SaveSettingsAsync();
        _logger.LogInformation("Smart mode set to {Enabled}", enabled);
    }

    /// <summary>
    /// Adds a username to the pending set. Returns false if already pending or already approved.
    /// </summary>
    /// <summary>The waiting list changed: a request arrived or was decided (AD-11).</summary>
    public event Action? OnPendingChanged;

    /// <summary>Puts the username on the waiting list and returns the request's token for the asking browser's cookie;
    /// null when the username waits already (asked a moment ago by another request).</summary>
    public string? AddPendingUser(string username)
    {
        // Clean up any stale approved/rejected state for this username
        _approvedUsers.TryRemove(username, out _);
        _rejectedUsers.TryRemove(username, out _);

        var request = new Request(DateTime.UtcNow, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        if (!_pendingUsers.TryAdd(username, request)) return null;
        OnPendingChanged?.Invoke();
        return request.Token;
    }

    /// <summary>
    /// Approves a pending user. Moves from pending to approved set.
    /// </summary>
    public void ApproveUser(string username)
    {
        if (_pendingUsers.TryRemove(username, out var request))
        {
            _approvedUsers[username] = request with { At = DateTime.UtcNow };
            _logger.LogInformation("Approved pending user '{Username}'", username);
            OnPendingChanged?.Invoke();
        }
    }

    /// <summary>
    /// Rejects a pending user. Moves from pending to rejected set.
    /// </summary>
    public void RejectUser(string username)
    {
        if (_pendingUsers.TryRemove(username, out var request))
        {
            _rejectedUsers[username] = request with { At = DateTime.UtcNow };
            _logger.LogInformation("Rejected pending user '{Username}'", username);
            OnPendingChanged?.Invoke();
        }
    }

    public bool IsPending(string username) => _pendingUsers.ContainsKey(username);

    /// <summary>
    /// Consumes the approval (one-time read) for the browser that holds the request's token; false for any other.
    /// </summary>
    public bool ConsumeApproval(string username, string? token) => Holds(_approvedUsers, username, token) && _approvedUsers.TryRemove(username, out _);

    /// <summary>
    /// Checks if approved without consuming.
    /// </summary>
    public bool IsApproved(string username) => _approvedUsers.ContainsKey(username);

    /// <summary>Approved, and this is the token of the browser that asked.</summary>
    public bool ApprovalBelongsTo(string username, string? token) => Holds(_approvedUsers, username, token);

    /// <summary>
    /// Consumes the rejection (one-time read) for the browser that holds the request's token; false for any other.
    /// </summary>
    public bool ConsumeRejection(string username, string? token) => Holds(_rejectedUsers, username, token) && _rejectedUsers.TryRemove(username, out _);

    // The browser that presents the request's token is the browser that asked.
    private static bool Holds(ConcurrentDictionary<string, Request> decided, string username, string? token) =>
        token is not null && decided.TryGetValue(username, out var request) && request.Token == token;

    public bool IsRejected(string username) => _rejectedUsers.ContainsKey(username);

    /// <summary>
    /// Returns all pending users ordered by request time.
    /// </summary>
    public List<(string Username, DateTime RequestedAt)> GetPendingUsers()
    {
        return _pendingUsers
            .Select(kv => (kv.Key, kv.Value.At))
            .OrderBy(x => x.At)
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
                    _smartMode = settings.SmartMode;
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
                RequireApproval = _requireApproval,
                SmartMode = _smartMode
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
        public bool SmartMode { get; set; }
    }

    #endregion
}
