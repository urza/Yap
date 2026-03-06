using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Yap.Data;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Singleton service for user management with in-memory cache.
/// Handles user creation, authentication, and lookups.
/// </summary>
public class UserService
{
    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;
    private readonly ILogger<UserService> _logger;
    private readonly bool _persistenceEnabled;

    // In-memory cache (mirrors database, like ChatService pattern)
    private readonly ConcurrentDictionary<Guid, User> _users = new();
    private readonly ConcurrentDictionary<string, Guid> _usernameToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Guid> _tokenToId = new();

    // Admin tracking
    private Guid? _adminUserId;
    private readonly object _adminLock = new();

    public UserService(IServiceProvider serviceProvider, ILogger<UserService> logger)
    {
        _logger = logger;
        _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();
        _persistenceEnabled = _dbFactory != null;
    }

    /// <summary>
    /// Loads users from database on startup.
    /// </summary>
    public async Task LoadUsersAsync()
    {
        if (!_persistenceEnabled) return;

        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            var users = await db.Users.AsNoTracking().ToListAsync();

            foreach (var user in users)
            {
                _users[user.Id] = user;
                _usernameToId[user.Username] = user.Id;
                _tokenToId[user.Token] = user.Id;

                if (user.IsAdmin)
                {
                    _adminUserId = user.Id;
                }
            }

            _logger.LogInformation("Loaded {Count} users from database", users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load users from database");
        }
    }

    /// <summary>
    /// Creates a new user with a generated token.
    /// </summary>
    public async Task<User?> CreateUserAsync(string username)
    {
        // Generate a secure random token
        var token = GenerateToken();

        // Determine if this user should be admin (first user)
        // Single lock block to prevent two users both seeing null and both becoming admin
        bool isAdmin;
        lock (_adminLock)
        {
            isAdmin = _adminUserId == null;
        }

        var user = new User(username, token)
        {
            IsAdmin = isAdmin
        };

        // Atomic uniqueness check — TryAdd returns false if username already exists
        if (!_usernameToId.TryAdd(user.Username, user.Id))
        {
            _logger.LogWarning("Attempted to create user with existing username: {Username}", username);
            return null;
        }

        _users[user.Id] = user;
        _tokenToId[user.Token] = user.Id;

        if (isAdmin)
        {
            lock (_adminLock)
            {
                // Double-check: another thread may have set admin between our check and here
                if (_adminUserId == null)
                {
                    _adminUserId = user.Id;
                }
                else
                {
                    // Someone else became admin first — demote this user
                    isAdmin = false;
                    user.IsAdmin = false;
                }
            }
        }

        // Persist to database
        if (_persistenceEnabled)
        {
            try
            {
                await using var db = await _dbFactory!.CreateDbContextAsync();
                db.Users.Add(user);
                await db.SaveChangesAsync();
                _logger.LogInformation("Created new user: {Username} (Admin: {IsAdmin})", username, isAdmin);
            }
            catch (Exception ex)
            {
                // Rollback in-memory changes on failure
                _users.TryRemove(user.Id, out _);
                _usernameToId.TryRemove(user.Username, out _);
                _tokenToId.TryRemove(user.Token, out _);

                if (isAdmin)
                {
                    lock (_adminLock)
                    {
                        _adminUserId = null;
                    }
                }

                _logger.LogError(ex, "Failed to persist new user: {Username}", username);
                return null;
            }
        }

        return user;
    }

    /// <summary>
    /// Authenticates a user by their token.
    /// </summary>
    public User? AuthenticateByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        if (_tokenToId.TryGetValue(token, out var userId) && _users.TryGetValue(userId, out var user))
        {
            return user;
        }

        return null;
    }

    /// <summary>
    /// Gets a user by their ID.
    /// </summary>
    public User? GetById(Guid userId)
    {
        return _users.TryGetValue(userId, out var user) ? user : null;
    }

    /// <summary>
    /// Gets a user by their username.
    /// </summary>
    public User? GetByUsername(string username)
    {
        if (_usernameToId.TryGetValue(username, out var userId))
        {
            return _users.TryGetValue(userId, out var user) ? user : null;
        }
        return null;
    }

    /// <summary>
    /// Checks if a username is already taken.
    /// </summary>
    public bool IsUsernameTaken(string username)
    {
        return _usernameToId.ContainsKey(username);
    }

    /// <summary>
    /// Gets all registered usernames.
    /// </summary>
    public List<string> GetAllUsernames() => _usernameToId.Keys.ToList();

    /// <summary>
    /// Gets all registered users.
    /// </summary>
    public List<User> GetAllUsers() => _users.Values.ToList();

    /// <summary>
    /// Updates the user's LastSeenAt timestamp.
    /// </summary>
    public async Task UpdateLastSeenAsync(Guid userId)
    {
        if (_users.TryGetValue(userId, out var user))
        {
            user.LastSeenAt = DateTime.UtcNow;

            if (_persistenceEnabled)
            {
                try
                {
                    await using var db = await _dbFactory!.CreateDbContextAsync();
                    await db.Users
                        .Where(u => u.Id == userId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(u => u.LastSeenAt, DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update LastSeenAt for user {UserId}", userId);
                }
            }
        }
    }

    /// <summary>
    /// Gets the admin user ID.
    /// </summary>
    public Guid? GetAdminUserId()
    {
        lock (_adminLock)
        {
            return _adminUserId;
        }
    }

    /// <summary>
    /// Checks if a user is the admin.
    /// </summary>
    public bool IsAdmin(Guid userId)
    {
        lock (_adminLock)
        {
            return _adminUserId == userId;
        }
    }

    /// <summary>
    /// Checks if a username is the admin.
    /// </summary>
    public bool IsAdmin(string username)
    {
        if (_usernameToId.TryGetValue(username, out var userId))
        {
            return IsAdmin(userId);
        }
        return false;
    }

    /// <summary>
    /// Gets the admin username.
    /// </summary>
    public string? GetAdminUsername()
    {
        lock (_adminLock)
        {
            if (_adminUserId.HasValue && _users.TryGetValue(_adminUserId.Value, out var user))
            {
                return user.Username;
            }
            return null;
        }
    }

    /// <summary>
    /// Deletes a user (used when signing out).
    /// </summary>
    public async Task DeleteUserAsync(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var user))
            return;

        // Remove from in-memory cache
        _users.TryRemove(userId, out _);
        _usernameToId.TryRemove(user.Username, out _);
        _tokenToId.TryRemove(user.Token, out _);

        // If this was the admin, clear admin status
        lock (_adminLock)
        {
            if (_adminUserId == userId)
            {
                _adminUserId = null;
            }
        }

        // Delete from database (including related read states)
        if (_persistenceEnabled)
        {
            try
            {
                await using var db = await _dbFactory!.CreateDbContextAsync();
                // Delete read states first (foreign key constraint)
                await db.ChannelReadStates.Where(r => r.UserId == userId).ExecuteDeleteAsync();
                // Delete user
                await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
                _logger.LogInformation("Deleted user: {Username}", user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete user {UserId}", userId);
            }
        }
    }

    /// <summary>
    /// Updates a user's profile (display name, profile picture, bio).
    /// </summary>
    public async Task UpdateProfileAsync(Guid userId, string? displayName, string? profilePictureUrl, string? bio, string? country)
    {
        if (!_users.TryGetValue(userId, out var user))
            return;

        // Update in-memory cache
        user.DisplayName = displayName;
        user.ProfilePictureUrl = profilePictureUrl;
        user.Bio = bio;
        user.Country = country;

        // Persist to database
        if (_persistenceEnabled)
        {
            try
            {
                await using var db = await _dbFactory!.CreateDbContextAsync();
                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.DisplayName, displayName)
                        .SetProperty(u => u.ProfilePictureUrl, profilePictureUrl)
                        .SetProperty(u => u.Bio, bio)
                        .SetProperty(u => u.Country, country));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update profile for user {UserId}", userId);
            }
        }
    }

    /// <summary>
    /// Sets the push notification muted state for a user.
    /// </summary>
    public async Task SetPushMutedAsync(Guid userId, bool muted)
    {
        if (!_users.TryGetValue(userId, out var user))
            return;

        user.PushMuted = muted;

        if (_persistenceEnabled)
        {
            try
            {
                await using var db = await _dbFactory!.CreateDbContextAsync();
                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.PushMuted, muted));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update PushMuted for user {UserId}", userId);
            }
        }
    }

    /// <summary>
    /// Updates the user's timezone and locale (detected from browser).
    /// </summary>
    public async Task UpdateLocaleAsync(Guid userId, string? timeZone, string? locale, string? dateFormat = null)
    {
        if (!_users.TryGetValue(userId, out var user))
            return;

        user.TimeZone = timeZone;
        user.Locale = locale;
        if (dateFormat != null)
            user.DateFormat = dateFormat;

        if (_persistenceEnabled)
        {
            try
            {
                await using var db = await _dbFactory!.CreateDbContextAsync();
                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.TimeZone, timeZone)
                        .SetProperty(u => u.Locale, locale)
                        .SetProperty(u => u.DateFormat, dateFormat ?? user.DateFormat));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update locale for user {UserId}", userId);
            }
        }
    }

    /// <summary>
    /// Sets a passphrase for the user (enables multi-device login).
    /// </summary>
    public async Task SetPasswordAsync(Guid userId, string? password)
    {
        if (!_users.TryGetValue(userId, out var user))
            return;

        user.Password = password;

        if (_persistenceEnabled)
        {
            try
            {
                await using var db = await _dbFactory!.CreateDbContextAsync();
                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.Password, password));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update password for user {UserId}", userId);
            }
        }
    }

    /// <summary>
    /// Verifies a password for an existing user. Returns the user if credentials match.
    /// </summary>
    public User? VerifyPassword(string username, string password)
    {
        var user = GetByUsername(username);
        if (user == null || user.Password == null)
            return null;

        return string.Equals(user.Password, password, StringComparison.Ordinal) ? user : null;
    }

    /// <summary>
    /// Checks if a user has a passphrase set.
    /// </summary>
    public bool HasPassword(string username)
    {
        var user = GetByUsername(username);
        return user?.Password != null;
    }

    /// <summary>
    /// Gets the stored passphrase for a user (for display in Settings).
    /// </summary>
    public string? GetPassword(Guid userId)
    {
        return _users.TryGetValue(userId, out var user) ? user.Password : null;
    }

    /// <summary>
    /// Generates a random passphrase in "color animal NN" format.
    /// </summary>
    public static string GeneratePassphrase()
    {
        var colors = new[] { "red", "blue", "green", "gold", "pink", "cyan", "lime", "teal",
            "plum", "mint", "ruby", "jade", "coral", "amber", "ivory", "peach", "sage", "navy", "rust", "wine" };
        var animals = new[] { "cat", "dog", "fox", "owl", "bee", "bat", "elk", "ant", "emu", "yak",
            "ape", "cod", "cow", "hen", "jay", "koi", "ram", "rat", "ray", "seal",
            "wolf", "bear", "deer", "frog", "hawk", "lion", "lynx", "moth", "puma", "swan" };

        var color = colors[RandomNumberGenerator.GetInt32(colors.Length)];
        var animal = animals[RandomNumberGenerator.GetInt32(animals.Length)];
        var number = RandomNumberGenerator.GetInt32(10, 100);

        return $"{color} {animal} {number}";
    }

    /// <summary>
    /// Generates a secure random token.
    /// </summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
