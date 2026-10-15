using Yap.Helpers;
using Yap.Models.Accounts;
using Yap.Models.Admin;
using Yap.Services.Admin;
using Yap.Services.Chat;
using Yap.Services.Presence;

namespace Yap.Services.Accounts;

/// <summary>What a typed username can do on this device right now (AU-05, AU-08, AU-09).</summary>
public enum SignInState { Invalid, Free, Closed, Pending, Smart, Verify, Guide }

public sealed record SignInCheck(SignInState State, string? Reason);

/// <summary>The answer to a sign-in: a signed-in account, a wait for approval, or a refusal with its reason and status.</summary>
public sealed record SignInOutcome(int Status, User? User = null, string? Reason = null)
{
    public static SignInOutcome SignedIn(User user) => new(200, user);
    /// <summary>202: the request waits for the admin. The token names the browser that asked; the endpoint puts it
    /// in a cookie, and only that browser turns the approval into an account (AU-05).</summary>
    public static SignInOutcome Waiting(string? pendingToken = null) => new(202) { PendingToken = pendingToken };
    public string? PendingToken { get; init; }
    public static SignInOutcome Refused(int status, string reason) => new(status, null, reason);
}

/// <summary>The six views of the invite page (AU-16, AU-17).</summary>
public enum InviteState { Join, Continue, Self, Switch, Invalid, Busy }

public sealed record InviteView(InviteState State, string? Owner, string? Current);

/// <summary>
/// Decides what a username can do and performs the sign-in. The check route and the sign-in route share the
/// decision, so the client never decides, it only shows (docs/architecture/sign-in.md). The endpoint sets the
/// cookie from the outcome; this class never sees a response.
/// </summary>
public sealed class SignInService(UserService users, AccessLinkService links, RegistrationGateService gate, PresenceService presence,
    SystemBotService bot, UserActionLogService log)
{
    public const string TooManyAttempts = "Too many attempts. Try again in a few minutes.";   // AU-14
    public const string RegistrationClosed = "Registration is temporarily closed";               // AU-05

    public SignInCheck Check(string? raw, string? ip)
    {
        if (UsernameRules.Check(raw, out var username) is { } rule) return new(SignInState.Invalid, rule);   // AU-02
        if (bot.IsBotUser(username)) return new(SignInState.Invalid, "Nice try! 🤖");                          // AU-03

        var user = users.GetByUsername(username);
        if (user is null)
        {
            if (users.FindByDisplayName(username) is not null) return new(SignInState.Invalid, DisplayNameTaken(username));   // AU-03
            if (gate.IsRejected(username)) return new(SignInState.Invalid, "Your previous request was not approved. You can try a different username.");
            if (gate.IsPending(username) || gate.IsApproved(username)) return new(SignInState.Pending, null);
            if (gate.RegistrationClosed) return new(SignInState.Closed, null);
            return new(SignInState.Free, null);
        }

        if (SmartLoginApplies(user, ip)) return new(SignInState.Smart, null);                                     // AU-09
        if (users.HasPassword(username) || links.HasActiveLink(user.Id)) return new(SignInState.Verify, null);   // AU-08
        return new(SignInState.Guide, null);
    }

    /// <summary>A new account, a smart login, or a code. The code is the secret code (exact) or the code of the login
    /// link (any case, any separators).</summary>
    public SignInOutcome SignIn(string? raw, string? code, string? ip, string? userAgent, string? pendingToken)
    {
        var check = Check(raw, ip);
        if (links.IsBraked && check.State == SignInState.Verify) return Fail(null, "braked", ip, userAgent, 429, TooManyAttempts);   // AU-14: code sign-ins only
        var username = (raw ?? "").Trim().ToLowerInvariant();
        switch (check.State)
        {
            case SignInState.Free:
                if (gate.RequireApproval)
                {
                    if (gate.AddPendingUser(username) is not { } token) return SignInOutcome.Waiting();   // asked a moment ago by another request
                    _ = bot.NotifyAdminOfPendingUserAsync(username);   // AU-05, AD-11
                    return SignInOutcome.Waiting(token);
                }
                return Create(username, ip, userAgent);

            case SignInState.Pending:
                return gate.ConsumeApproval(username, pendingToken) ? Create(username, ip, userAgent) : SignInOutcome.Waiting();

            case SignInState.Smart:
            {
                var user = users.GetByUsername(username)!;
                var live = presence.HasConnectedSessionFromIp(user.Id, ip);
                log.Log(user.Id.ToString(), UserActionLog.KnownActions.SMART_LOGIN, info: live ? username : $"known_ip:{username}", ip: ip ?? "unknown");
                return SignedIn(user, "smart", ip, userAgent);
            }

            case SignInState.Verify:
            {
                if (string.IsNullOrWhiteSpace(code)) return SignInOutcome.Refused(401, "Code required");
                if (users.VerifyPassword(username, code) is { } byPassphrase) return SignedIn(byPassphrase, "passphrase", ip, userAgent);
                var owner = users.GetByUsername(username)!;
                if (links.VerifyCodeForUser(owner.Id, code) is { } link)
                {
                    links.RecordUse(link);
                    return SignedIn(owner, "link", ip, userAgent);
                }
                links.RecordFailure();
                return Fail(null, $"wrong_passphrase:{username}", ip, userAgent, 401, "Wrong code");
            }

            case SignInState.Closed:
                return Fail(null, $"registration_closed:{username}", ip, userAgent, 403, RegistrationClosed);

            case SignInState.Guide:
                return Fail(null, $"ip_mismatch:{username}", ip, userAgent, 403, "This device cannot sign in to that account by itself.");

            default:
                return Fail(null, $"invalid:{username}", ip, userAgent, 400, check.Reason ?? "Cannot sign in");
        }
    }

    /// <summary>What the requesting browser's polling sees. Another browser sees "waiting" whatever happened: the
    /// decision belongs to the request (AU-05).</summary>
    public string PendingState(string username, string? pendingToken) =>
        gate.ApprovalBelongsTo(username, pendingToken) ? "approved" : gate.ConsumeRejection(username, pendingToken) ? "rejected" : "waiting";

    // ---- invites and login links (AU-10, AU-16, AU-17) ---------------------------------------------------

    public InviteView LookupInvite(string? code, Guid? currentUserId)
    {
        if (links.IsBraked) return new(InviteState.Busy, null, null);
        var link = links.Find(code);
        if (link is not { IsActive: true })
        {
            links.RecordFailure();   // dead links count towards the brake (AU-14)
            return new(InviteState.Invalid, null, null);
        }
        if (link.UserId is null) return new(InviteState.Join, null, null);

        var owner = users.GetById(link.UserId.Value);
        var current = currentUserId is { } id ? users.GetById(id) : null;
        if (owner is null) return new(InviteState.Invalid, null, null);
        if (current is null) return new(InviteState.Continue, owner.Username, null);
        if (current.Id == owner.Id) return new(InviteState.Self, owner.Username, current.Username);
        return new(InviteState.Switch, owner.Username, current.Username);
    }

    /// <summary>An open invite creates the account and bypasses the registration switches: the admin vouched by
    /// minting it. A claimed link signs its owner in. The link is the credential, not the typed name.</summary>
    public SignInOutcome Redeem(string? code, string? rawUsername, string? ip, string? userAgent)
    {
        if (links.IsBraked) return Fail(null, "invite_braked", ip, userAgent, 429, TooManyAttempts);
        var link = links.Find(code);
        if (link is not { IsActive: true })
        {
            links.RecordFailure();
            return Fail(null, "invite_invalid", ip, userAgent, 404, "This link no longer works");
        }

        if (link.UserId is { } ownerId)
        {
            var owner = users.GetById(ownerId);
            if (owner is null) return Fail(null, "invite_orphan", ip, userAgent, 404, "This link no longer works");
            links.RecordUse(link);
            return SignedIn(owner, "link", ip, userAgent);
        }

        if (UsernameRules.Check(rawUsername, out var username) is not null)
            return SignInOutcome.Refused(400, "That username is not allowed. Pick another one.");
        if (bot.IsBotUser(username)) return SignInOutcome.Refused(400, "That username is not allowed. Pick another one.");
        if (users.FindByDisplayName(username) is not null) return SignInOutcome.Refused(400, "A member already goes by that name. Pick a different one.");
        var user = users.CreateUser(username);
        if (user is null) return SignInOutcome.Refused(409, "That username is already taken. Pick another one.");

        // Lost a race for the same invite: the account exists now, so it gets a link of its own (AU-17).
        if (!links.Claim(link, user.Id)) links.CreateForUser(user.Id, user.Id);
        log.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: $"invite_join:{username}", ip: ip ?? "unknown", userAgent: userAgent);
        users.RecordKnownIp(user.Id, ip);
        return SignInOutcome.SignedIn(user);
    }

    // ---- inside ----------------------------------------------------------------------------------------------

    private bool SmartLoginApplies(User user, string? ip) =>
        gate.SmartMode && !user.SmartLoginOptOut
        && (presence.HasConnectedSessionFromIp(user.Id, ip) || users.HasRecentKnownIp(user.Username, ip));

    private SignInOutcome Create(string username, string? ip, string? userAgent)
    {
        var user = users.CreateUser(username);
        if (user is null) return SignInOutcome.Refused(409, "That username is already taken");
        links.CreateForUser(user.Id, user.Id);   // every account is born with a login link (AU-11)
        log.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: username, ip: ip ?? "unknown", userAgent: userAgent);
        users.RecordKnownIp(user.Id, ip);
        return SignInOutcome.SignedIn(user);
    }

    private SignInOutcome SignedIn(User user, string method, string? ip, string? userAgent)
    {
        log.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: user.Username, ip: ip ?? "unknown", userAgent: userAgent);
        users.RecordKnownIp(user.Id, ip);
        _ = bot.NotifyNewDeviceLoginAsync(user.Username, method, ip ?? "unknown");   // AU-26
        return SignInOutcome.SignedIn(user);
    }

    private SignInOutcome Fail(string? userId, string tag, string? ip, string? userAgent, int status, string reason)
    {
        log.Log(userId, UserActionLog.KnownActions.LOGIN_FAIL, info: tag, ip: ip ?? "unknown", userAgent: userAgent);   // AD-20
        return SignInOutcome.Refused(status, reason);
    }

    private static string DisplayNameTaken(string name) =>
        $"\"{name}\" is a member's display name. If that's you, log in with your original username — changing your display name never changes your login. Otherwise, pick a different name.";
}
