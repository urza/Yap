using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Yap.Models;
using Yap.Models.Accounts;
using Yap.Services.Accounts;
using Yap.Services.Persistence;
using Yap.Models.Notifications;

namespace Yap.Services.Notifications;

/// <summary>
/// Answers "should this channel notify this user right now?" for every notification sink:
/// sidebar badges, the app-wide unread total, the tab title and sound, and web push.
/// </summary>
/// <remarks>
/// Muting is a display-and-delivery filter, not a counting filter. Unread counts keep
/// incrementing for muted channels so the sidebar can still show the small dot, and so
/// unmuting reveals a true count instead of starting from zero.
///
/// Deliberately does NOT depend on ChatService: ChatService is the caller (push decisions,
/// unread totals), and a mutual dependency would not resolve. Callers therefore pass
/// <c>isDirectMessage</c>, which they always know from the channel they already hold.
/// </remarks>
public class NotificationSettingsService
{
    private readonly UserService _userService;
    private readonly PersistenceWriter _writer;
    private readonly IDbContextFactory<ChatDbContext>? _db;
    private readonly ILogger<NotificationSettingsService> _logger;

    /// <summary>A mute of the person changed on some device; the others merge the new mutes (NT-22).</summary>
    public event Action<Guid>? OnChanged;

    // (userId, channelId) -> muted. Only channels the user explicitly flipped appear here.
    private readonly ConcurrentDictionary<(Guid UserId, Guid ChannelId), bool> _overrides = new();

    public NotificationSettingsService(
        UserService userService,
        PersistenceWriter writer,
        IServiceProvider services,
        ILogger<NotificationSettingsService> logger)
    {
        _userService = userService;
        _writer = writer;
        _db = services.GetService<IDbContextFactory<ChatDbContext>>();   // not registered when persistence is off
        _logger = logger;
    }

    /// <summary>
    /// Loads the per-channel overrides from the database. Called once at startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_db is null) return;
        var settings = await Loads.ChannelNotificationSettingsAsync(_db);
        foreach (var s in settings)
            _overrides[(s.UserId, s.ChannelId)] = s.Muted;

        _logger.LogInformation("Loaded {Count} channel notification overrides", settings.Count);
    }

    #region Evaluation

    /// <summary>
    /// Whether the user muted the whole server. An elapsed timed mute reads as unmuted.
    /// </summary>
    public bool IsServerMuted(User user)
    {
        if (!user.NotifServerMuted) return false;

        // A timed mute expires on its own. The stored flag is left alone (this runs on hot read
        // paths and must not write); the settings page reads this method, not the flag, and the
        // next SetServerMute overwrites it.
        return user.NotifServerMuteUntil is not { } until || until > DateTime.UtcNow;
    }

    /// <summary>
    /// Whether a channel notifies this user. True means: no badge, no unread total, no push,
    /// no sound. The unread dot still appears.
    /// </summary>
    public bool IsMuted(User user, Guid channelId, bool isDirectMessage)
    {
        if (IsServerMuted(user)) return true;

        var mode = isDirectMessage ? user.NotifDmMode : user.NotifRoomMode;
        return mode switch
        {
            NotificationMode.AllowAll => false,
            NotificationMode.MuteAll => true,
            _ => _overrides.TryGetValue((user.Id, channelId), out var muted)
                    ? muted
                    : DefaultForUntouchedChannel(user, isDirectMessage)
        };
    }

    /// <summary>
    /// The fallback for a channel the user has no override row for, in Individual mode.
    /// DMs follow the user's "New DMs" choice; a room nobody opted into stays muted.
    /// </summary>
    private static bool DefaultForUntouchedChannel(User user, bool isDirectMessage) =>
        isDirectMessage ? user.NotifNewDmsMuted : true;

    #endregion

    #region Mutations

    /// <summary>
    /// Sets one channel's override for one user.
    /// </summary>
    public void SetChannelMuted(Guid userId, Guid channelId, bool muted)
    {
        _overrides[(userId, channelId)] = muted;
        _writer.Enqueue(Writes.ChannelNotificationSetting(new ChannelNotificationSetting
        {
            UserId = userId,
            ChannelId = channelId,
            Muted = muted
        }));
        OnChanged?.Invoke(userId);
    }

    public void SetServerMute(Guid userId, bool muted, DateTime? until)   // NT-17
    {
        _userService.SetServerMute(userId, muted, until);
        OnChanged?.Invoke(userId);
    }

    public void SetDmMode(Guid userId, NotificationMode mode)   // NT-18
    {
        _userService.SetDmNotificationMode(userId, mode);
        OnChanged?.Invoke(userId);
    }

    public void SetRoomMode(Guid userId, NotificationMode mode)   // NT-18
    {
        _userService.SetRoomNotificationMode(userId, mode);
        OnChanged?.Invoke(userId);
    }

    public void SetNewDmsMuted(Guid userId, bool muted)   // NT-18
    {
        _userService.SetNewDmsMuted(userId, muted);
        OnChanged?.Invoke(userId);
    }

    /// <summary>The person's mutes as the client needs them: the three levels of NT-16 and the per-conversation choices.</summary>
    public MutesDto For(Guid userId)
    {
        var user = _userService.GetById(userId);
        if (user is null) return new MutesDto(new ServerMuteDto(false, null), NotificationMode.AllowAll, NotificationMode.MuteAll, false, new());
        var channels = _overrides.Where(o => o.Key.UserId == userId).ToDictionary(o => o.Key.ChannelId, o => o.Value);
        return new MutesDto(new ServerMuteDto(IsServerMuted(user), user.NotifServerMuted ? user.NotifServerMuteUntil : null),
            user.NotifDmMode, user.NotifRoomMode, user.NotifNewDmsMuted, channels);
    }

    /// <summary>
    /// Drops the overrides for a deleted channel, so a recycled channel id cannot inherit them.
    /// </summary>
    public void ClearOverridesForChannel(Guid channelId)
    {
        foreach (var key in _overrides.Keys.Where(k => k.ChannelId == channelId).ToList())
            _overrides.TryRemove(key, out _);
    }

    /// <summary>Drops the overrides of a deleted account (AD-40). The database cascades its rows.</summary>
    public void ClearOverridesForUser(Guid userId)
    {
        foreach (var key in _overrides.Keys.Where(k => k.UserId == userId).ToList())
            _overrides.TryRemove(key, out _);
    }

    #endregion
}
