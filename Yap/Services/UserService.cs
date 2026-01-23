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
        // Validate username isn't already taken
        if (_usernameToId.ContainsKey(username))
        {
            _logger.LogWarning("Attempted to create user with existing username: {Username}", username);
            return null;
        }

        // Generate a secure random token
        var token = GenerateToken();

        // Determine if this user should be admin (first user)
        bool isAdmin;
        lock (_adminLock)
        {
            isAdmin = _adminUserId == null;
        }

        var user = new User(username, token)
        {
            IsAdmin = isAdmin
        };

        // Add to in-memory cache
        _users[user.Id] = user;
        _usernameToId[user.Username] = user.Id;
        _tokenToId[user.Token] = user.Id;

        if (isAdmin)
        {
            lock (_adminLock)
            {
                _adminUserId = user.Id;
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
    /// Generates a secure random token.
    /// </summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
