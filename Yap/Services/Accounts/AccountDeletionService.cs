using Yap.Models.Accounts;
using Yap.Services.Chat;
using Yap.Services.Gifs;
using Yap.Services.Media;
using Yap.Services.Notifications;
using Yap.Services.Presence;

namespace Yap.Services.Accounts;

/// <summary>
/// The admin's account deletion (AD-38 to AD-40): one call that reaches every service that knows a person, in the
/// order the data depends on. The user leaves memory last, after its sessions are ended and its conversations are
/// gone, so a request that races the delete still finds a consistent world. The row delete goes to the database
/// behind every other write of the purge, so the cascades meet nothing the queue still had to store.
/// </summary>
public sealed class AccountDeletionService(UserService users, ChatService chat, PresenceService presence, AccessLinkService links,
    PasskeyService passkeys, PushSubscriptionStore push, GifService gifs, NotificationSettingsService mutes, SystemBotService bot,
    ImageService images, ILogger<AccountDeletionService> log)
{
    public const int DefaultInactiveDays = 90;   // AD-39

    /// <summary>The account is gone; the broadcaster tells everyone, and clients drop the person and the direct message.</summary>
    public event Action<string>? OnAccountDeleted;

    /// <summary>The name a deleted account's messages carry (AD-40). A space and parentheses are outside the username
    /// alphabet, so no account can register it and a newcomer with the old name inherits nothing.</summary>
    public static string GhostName(string username) => $"{username} (deleted)";

    /// <summary>The newest sign of life the account row keeps: the last connect or disconnect, else the creation.</summary>
    public static DateTime LastActiveAt(User user) => user.LastSeenAt is { } seen && seen > user.CreatedAt ? seen : user.CreatedAt;

    /// <summary>Why this account cannot be deleted, or null. The admin's removal would make the next sign-up the
    /// admin (AU-06); the bot's would come back at the next start, without its conversations.</summary>
    public string? Refusal(User user) =>
        users.IsAdmin(user.Id) ? "The admin cannot be deleted." :
        bot.IsBotUser(user.Id) ? "The bot cannot be deleted." : null;

    /// <summary>The accounts whose last sign of life is older than <paramref name="days"/>, oldest first (AD-39). A person
    /// with a live session is active now, whatever the row says; the admin and the bot are never listed.</summary>
    public List<User> Inactive(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return users.GetAllUsers()
            .Where(u => Refusal(u) is null && !presence.HasSession(u.Id) && LastActiveAt(u) < cutoff)
            .OrderBy(LastActiveAt).ToList();
    }

    /// <summary>Deletes the account. Returns the refusal, or null when it is gone.</summary>
    public async Task<string?> DeleteAsync(Guid userId)
    {
        if (users.GetById(userId) is not { } user) return "Unknown user.";
        if (Refusal(user) is { } refusal) return refusal;

        // Sessions first: an open connection keeps its claims, and a reaction or a read mark from a dead account
        // would write rows for nobody. The cookie dies with the dictionaries, at the end.
        foreach (var session in presence.SessionsOf(user.Id)) presence.End(session);
        presence.ForgetUser(user.Id);

        var ghost = GhostName(user.Username);
        var dms = chat.RemoveAccount(user.Id, ghost);
        mutes.ClearOverridesForUser(user.Id);
        gifs.RemoveAccount(user.Id);
        links.ForgetUser(user.Id);
        passkeys.ForgetUser(user.Id);
        await push.RemoveAccountAsync(user.Username);
        images.DeleteProfilePicture(user.Id);
        bot.Forget(user.Username);

        users.Remove(user.Id);
        log.LogInformation("Account {Username} deleted: {Dms} direct message(s) gone, messages relabelled {Ghost}", user.Username, dms.Count, ghost);
        OnAccountDeleted?.Invoke(user.Username);
        return null;
    }
}
