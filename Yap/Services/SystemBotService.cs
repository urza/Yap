using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Singleton service that manages the system bot user.
/// The bot appears as a regular user in the sidebar, sends DMs to admin when users join,
/// and auto-replies with a placeholder when users DM it.
/// </summary>
public class SystemBotService
{
    private readonly UserService _userService;
    private readonly ChatService _chatService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemBotService> _logger;

    private Guid _botUserId;
    private string _botUsername = "";
    private bool _initialized;

    // Debounce auto-replies: username -> last reply time
    private readonly ConcurrentDictionary<string, DateTime> _lastReplyTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ReplyDebounce = TimeSpan.FromSeconds(30);

    private const string BotSessionId = "system-bot-session";
    private const string BotAvatarUrl = "/images/bot-avatar.svg";

    public SystemBotService(
        UserService userService,
        ChatService chatService,
        IConfiguration configuration,
        ILogger<SystemBotService> logger)
    {
        _userService = userService;
        _chatService = chatService;
        _configuration = configuration;
        _logger = logger;
    }

    private bool BotEnabled => _configuration.GetValue<bool>("ChatSettings:Bot:Enabled", true);
    private string BotUsernameConfig => _configuration["ChatSettings:Bot:Username"] ?? "ping";
    private string BotDisplayNameConfig => _configuration["ChatSettings:Bot:DisplayName"] ?? "Ping";

    /// <summary>
    /// Initializes the bot user and subscribes to events.
    /// Must be called after UserService.LoadUsersAsync() and ChatService initialization.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!BotEnabled)
        {
            _logger.LogInformation("System bot is disabled");
            return;
        }

        var botUsername = BotUsernameConfig;
        var botDisplayName = BotDisplayNameConfig;

        // Look up or create the bot user
        var botUser = _userService.GetByUsername(botUsername);
        if (botUser == null)
        {
            botUser = await _userService.CreateUserAsync(botUsername);
            if (botUser == null)
            {
                _logger.LogError("Failed to create bot user '{Username}'", botUsername);
                return;
            }

            // Bot should never be admin — if it was made admin (first user), revoke it
            if (botUser.IsAdmin)
            {
                botUser.IsAdmin = false;
                _logger.LogWarning("Bot was created as admin (first user) — revoking admin status");
            }

            await _userService.UpdateProfileAsync(
                botUser.Id,
                displayName: botDisplayName,
                profilePictureUrl: BotAvatarUrl,
                bio: "I'm a bot! 🤖",
                country: null);

            _logger.LogInformation("Created bot user '{Username}' with ID {UserId}", botUsername, botUser.Id);
        }
        else
        {
            // Update display name and avatar if changed
            if (botUser.DisplayName != botDisplayName || botUser.ProfilePictureUrl != BotAvatarUrl)
            {
                await _userService.UpdateProfileAsync(
                    botUser.Id,
                    displayName: botDisplayName,
                    profilePictureUrl: BotAvatarUrl,
                    bio: botUser.Bio ?? "I'm a bot! 🤖",
                    country: botUser.Country);
            }

            _logger.LogInformation("Found existing bot user '{Username}' with ID {UserId}", botUsername, botUser.Id);
        }

        _botUserId = botUser.Id;
        _botUsername = botUser.Username;
        _initialized = true;

        // Register bot as always-online
        await _chatService.AddUserAsync(BotSessionId, _botUserId, _botUsername);

        // Subscribe to events
        _chatService.OnUserChanged += HandleUserChanged;
        _chatService.OnMessageReceived += HandleMessageReceived;

        _logger.LogInformation("System bot initialized: {Username} ({UserId})", _botUsername, _botUserId);
    }

    /// <summary>
    /// Checks if the given username is the bot.
    /// </summary>
    public bool IsBotUser(string? username)
    {
        if (!_initialized || string.IsNullOrEmpty(username))
            return false;
        return _botUsername.Equals(username, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the given user ID is the bot.
    /// </summary>
    public bool IsBotUser(Guid userId)
    {
        if (!_initialized)
            return false;
        return _botUserId == userId;
    }

    /// <summary>
    /// Gets the bot's user ID, or null if not initialized.
    /// </summary>
    public Guid? BotUserId => _initialized ? _botUserId : null;

    /// <summary>
    /// Gets the bot's username, or null if not initialized.
    /// </summary>
    public string? BotUsername => _initialized ? _botUsername : null;

    /// <summary>
    /// Updates the bot's display name at runtime (from admin panel).
    /// </summary>
    public async Task UpdateDisplayNameAsync(string newDisplayName)
    {
        if (!_initialized) return;

        var botUser = _userService.GetById(_botUserId);
        if (botUser == null) return;

        await _userService.UpdateProfileAsync(
            _botUserId,
            displayName: newDisplayName,
            profilePictureUrl: botUser.ProfilePictureUrl,
            bio: botUser.Bio,
            country: botUser.Country);

        _logger.LogInformation("Bot display name updated to '{DisplayName}'", newDisplayName);
    }

    /// <summary>
    /// When a new user joins, DM the admin about it.
    /// </summary>
    private async void HandleUserChanged(string username, bool joined)
    {
        if (!joined) return;
        if (IsBotUser(username)) return;

        try
        {
            var adminUsername = _chatService.GetAdmin();
            if (adminUsername == null) return;

            // Don't DM admin about their own join
            if (adminUsername.Equals(username, StringComparison.OrdinalIgnoreCase)) return;

            var adminUser = _userService.GetByUsername(adminUsername);
            if (adminUser == null) return;

            // Get display name for the joining user
            var joiningUser = _userService.GetByUsername(username);
            var displayName = joiningUser?.EffectiveDisplayName ?? username;

            // Get or create DM channel between bot and admin
            var channel = _chatService.GetOrCreateDMChannel(_botUserId, _botUsername, adminUser.Id, adminUser.Username);

            // Send notification message
            await _chatService.SendMessageAsync(channel.Id, _botUserId, _botUsername,
                $"👋 {displayName} just joined the chat!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bot HandleUserChanged for {Username}", username);
        }
    }

    /// <summary>
    /// When someone DMs the bot, auto-reply with a placeholder (debounced).
    /// </summary>
    private async void HandleMessageReceived(ChatMessage message)
    {
        // Skip own messages
        if (IsBotUser(message.Username)) return;
        if (message.Username == "System") return;

        try
        {
            // Check if this message is in a DM channel where bot is a participant
            var channels = _chatService.GetDMChannels(_botUsername);
            var dmChannel = channels.FirstOrDefault(c => c.Id == message.ChannelId);
            if (dmChannel == null) return;

            // Debounce: max one reply per user per 30 seconds
            var now = DateTime.UtcNow;
            if (_lastReplyTimes.TryGetValue(message.Username, out var lastReply)
                && (now - lastReply) < ReplyDebounce)
            {
                return;
            }

            _lastReplyTimes[message.Username] = now;

            // Small delay to feel more natural
            await Task.Delay(800);

            await _chatService.SendMessageAsync(dmChannel.Id, _botUserId, _botUsername,
                "Beep boop! 🤖 I'm not interactive yet, but hopefully soon!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bot HandleMessageReceived");
        }
    }
}
