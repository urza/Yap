# Sign-in flows

This page specifies the routes behind the login page, the verification page, the invite page and sign-out. The spec is [Sign-in and sessions](../spec/sessions.md) (AU-01 to AU-26). The [authentication page](auth-and-program.md) specifies the cookie and the principal. This page specifies how a device gets the cookie.

## Today, and what changes

Today the three pages are Blazor components. They call the services directly, then they navigate to `GET /auth/signin?username=` or they post a hidden form to `POST /auth/signin`, because a Blazor circuit cannot set a cookie. The redirect routes and the hidden form exist for that reason only. They are not in the spec's route list (SH-42). They go away.

The new pages are client pages. They call JSON routes. The sign-in route sets the cookie in its response. The client then navigates. One route replaces four. `GET /auth/refresh-token` goes away for the same reason: Settings used it to set a rotated token, and `sign-out-others` now sets the cookie in its own response.

What stays: `/pwa-launch?lt=` and the manifest. Installed apps have these URLs baked in.

## One service holds the decision

`Services/Accounts/SignInService` decides what a username can do and performs the sign-in. The `check` route and the `signin` route use the same decision. The client never decides, it only shows.

```cs
// Services/Accounts/SignInService.cs (sketch)
public enum SignInState { Invalid, Free, Closed, Pending, Smart, Verify, Guide }

public SignInCheck Check(string username, string ip)
{
    if (UsernameRules.Validate(username) is { } rule) return new(Invalid, rule);                 // AU-02
    if (bot.IsBotUser(username)) return new(Invalid, "Nice try! 🤖");                             // AU-03
    var user = users.ByUsername(username);
    if (user is null)
    {
        if (users.ByDisplayName(username) is not null) return new(Invalid, DisplayNameTaken(username)); // AU-03
        if (gate.IsPending(username)) return new(Pending, null);
        if (gate.RegistrationClosed) return new(Closed, null);                                   // AU-05
        return new(Free, null);
    }
    if (SmartLoginApplies(user, ip)) return new(Smart, null);                                    // AU-09
    if (user.HasPassphrase || links.HasActiveLink(user.Id)) return new(Verify, null);           // AU-08
    return new(Guide, null);
}

bool SmartLoginApplies(User user, string ip)
    => gate.SmartLoginAllowed && !user.SmartLoginOptOut
       && (presence.HasConnectedSessionFromIp(user.Id, ip) || users.HasKnownIp(user.Id, ip, within: TimeSpan.FromDays(14)));
```

```cs
public SignInResult SignIn(string username, string? code, string ip, string userAgent)
{
    if (links.BrakeTripped) return Refused(429, "Too many attempts. Try again in a few minutes.");   // AU-14
    var check = Check(username, ip);
    switch (check.State)
    {
        case Free:    return Created(users.Create(username, ip, userAgent), method: "new");          // first human is admin (AU-04); link minted (AU-11)
        case Pending: return gate.IsApproved(username) ? Created(users.Create(...), "approved") : Waiting();
        case Smart:   return SignedIn(user, method: "smart login (same network)");
        case Verify:
            if (code is null) return Refused(401, "Code required");
            if (users.VerifyPassphrase(user, code)) return SignedIn(user, "secret code");           // exact match (AU-14)
            if (links.VerifyCodeForUser(user.Id, code)) return SignedIn(user, "your login link");    // any case, any separators (AU-12)
            links.RecordFailure(ip); visitors.LoginFailure(ip, userAgent, "Wrong passphrase");
            return Refused(401, "Wrong code");
        case Closed:  visitors.LoginFailure(ip, userAgent, "Registration closed"); return Refused(403, ClosedText);
        default:      return Refused(400, check.Reason ?? "Cannot sign in");
    }
}
```

`SignedIn` records the known address (AU-09), logs the action, and asks the bot to send the new-device message to the account (AU-26). `Created` also starts the one-minute welcome timer and sends the join message to the admin (AU-26, PR-18). The endpoint sets the cookie from the result. The service never sees the response.

## Routes

All routes on this page are anonymous except sign-out.

| Route | Request | Response |
| --- | --- | --- |
| `GET /api/auth/check?username=` | | `{ state, reason? }`. The login page calls it while the person types, after a 300 ms pause. |
| `POST /api/auth/signin` | `{ username, code? }` | 200 `{ username }` and the cookie. 202 `{ state: "pending" }` when approval is required; the first such call puts the name on the admin's list, sends the bot message (AU-05, AD-11) and sets the `yap_pending` cookie: the token of this browser's request. The admin approves a name; only the browser with the token turns the approval into an account, so nobody else can claim it by typing the name first. 4xx with a reason otherwise. |
| `GET /api/auth/pending/{username}` | | `{ state: "waiting" \| "approved" \| "rejected" }` for the browser that holds the request's cookie; any other browser sees `waiting`. The login page polls every 2 s while it waits. On `approved` it calls `signin` again. |
| `GET /api/invite/{code}` | | `{ state: "join" \| "continue" \| "self" \| "switch" \| "invalid" \| "busy", owner?, current? }`. `self` is the owner, already signed in; the client opens the lobby. An invalid code counts as a failure for the brake (AU-14, AU-17). |
| `POST /api/invite/{code}` | `{ username? }` | 200 `{ username }` and the cookie. `username` is required for an open invite and ignored for a login link. 4xx with the reasons of the spec's link page. |
| `POST /api/auth/signout` | `{ pushEndpoint?, deviceId? }` | 204, the cleared cookie and the sign-out mark (a second cookie, removed by the next sign-in). Removes the device's push subscription (NT-24) and ends the hub connections of `deviceId`, so the other tabs of the device get 401 and show the login page (GEN-05). The token stays valid for the other devices (AU-22). |
| `GET /pwa-launch?lt=` | | kept: plants the cookie when the jar is empty, then 302 `/` (AU-23). A jar with the sign-out mark gets no cookie: a sign-out ends the automatic first-launch sign-in (AU-22). |
| `GET /manifest.webmanifest` | | kept: `start_url` carries the link token for a signed-in browser. |

Approve and reject are admin routes, on the admin page. The admin's connections also receive the `pending` hub event when the list changes.

## The client pages

**Login** (`pages/login.js`, AU-07). One box, one button, the configured playful texts from the bootstrap's anonymous part (see below). The page lowercases and trims the text while the person types (AU-02). It validates the rules locally for the instant error text, then calls `check` for the server states. Each state maps to one view:

| State | View |
| --- | --- |
| `invalid` | the reason under the box; the button disabled |
| `free` | "New here? Joining creates a fresh account named {name}."; the button creates the account |
| `closed` | "Registration is temporarily closed" and its text |
| `pending` | "Waiting for approval"; the poll runs |
| `smart` | the button signs in at once |
| `verify` | the button goes to `/verify/{username}` |
| `guide` | the three-step guide; the installed-app variant inside the installed app (AU-24) |

A successful `signin` navigates to the `returnUrl` of the link the person came for, or to the lobby (SH-42).

**Verify** (`pages/verify.js`, AU-14). "Welcome back, {username}", one box for the code, "Sign In". It posts `signin` with the code. "Wrong code" and "Too many attempts" come from the response.

**Invite** (`pages/invite.js`, AU-16, AU-17). It calls `GET /api/invite/{code}` and shows one of five views: join with a username box, continue as the owner, switch account, invalid, busy. Join and continue post to the same route. Opening the link alone never signs the device in (AU-10).

**Sign out** (status menu, SH-15, AU-22). The client shows the confirmations when they apply, posts `signout` with the device id, unsubscribes push, tells the other tabs of the device over a BroadcastChannel (they stop their hub and their outbox and show the login page, GEN-05), removes the drafts, the outbox, the snapshots and the cached bootstrap of the account, stops the hub, and navigates to `/login`. Without an answer from the server the sign-out does not happen and the menu shows "Needs a connection": the cookie is HttpOnly, and a local clean-up alone would bring the account back at the next reload (GEN-10). An account switch on the link page sends the same note to the other tabs before the cookie changes, and a note after the sign-in that makes them load the new account (AU-25).

## Texts for a signed-out page

The login, verify and invite pages need the project name and the configured login texts (AU-07) before any sign-in. `GET /api/bootstrap` serves both states (decided 2026-10-14). The route is anonymous. With a valid cookie it returns the full document. Without one it returns 200 with `config.projectName` and the login text lists only, and no `user`. The client makes the same first call in both states and branches on `user`. A signed-out visit paints the login page after one round trip. The [wire contract](wire-contract.md#bootstrap) shows both shapes.

## Failures and the brake

`AccessLinkService` keeps the brake: 20 failures in 10 minutes pause every code sign-in and every link for 10 minutes, and the bot tells the admin (AU-14). Wrong codes, dead invite links and dead login links count. The `check` route does not count, so a person who types slowly is never counted. Every failure also goes to the visitors log with its reason, for the admin's Visitors tab (AD-20): "Wrong passphrase", "Registration closed", "Bot username", "IP mismatch", "Approval pending".

## The services around it

| Service | Used for |
| --- | --- |
| `UserService` | find by username and display name, create, verify passphrase, known addresses, token |
| `AccessLinkService` | find a code, verify a code for a user, mint on create, claim an invite, the brake |
| `RegistrationGateService` | the closed and approval switches, the waiting list, approved and rejected names; memory only, lost on restart (AD-11) |
| `PresenceService` | a connected session from this address, for smart login. The address is the connection's own, or a trusted proxy's word; a forwarded header from anyone else makes it unknown (see the authentication page, "The client's address") |
| `SystemBotService` | the welcome message after one minute, the new-device message, the admin messages |
| `LinkTokenService` | the installed app's first launch |
| `UserActionLogService`, the visitors log | LOGIN and LOGIN_FAIL rows |

## Spec notes met here

- AU-05 says a person with a new name "waits on the login page while the admin gets a message" and "approval signs the person in". Today the page polls every second over the circuit. The new page polls the `pending` route every two seconds and posts `signin` once the state is `approved`. The visible behaviour is the same.
- The spec has no `GET /auth/signin` and no hidden form. Both were Blazor workarounds. Removing them changes nothing the user sees.
