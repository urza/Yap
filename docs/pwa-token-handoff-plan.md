# PWA Token Hand-off (Fix 1) — Implementation Plan

**Goal:** an installed PWA starts logged in even when its cookie jar is empty — the iOS
Add-to-Home-Screen case, where the home-screen app gets an isolated data container and the
browser's auth cookie can never carry over. (Android/desktop are already covered by the
SameSite=Lax fix; this adds the only mechanism that works on iOS: identity in the URL.)

**Mechanism in one paragraph:** the manifest is served by an endpoint instead of a static
file. When the manifest is fetched by a logged-in browser (which is when installs happen —
the bot nudges after signup), its `start_url` is `/pwa-launch?lt=<link token>` instead of
`/`. The installed app's first launch opens that URL in its own empty-cookie context; the
endpoint validates the token, sets the auth cookie *inside the PWA's jar*, and redirects
to `/`. Every launch after that rides the cookie like a normal browser.

---

## 1. Link tokens — stateless HMAC, no storage, no migration

New `Services/LinkTokenService.cs` (singleton):

- **Secret:** 32 random bytes, generated on first use and persisted to
  `Data/link-token.key` (same Data-folder pattern as other runtime state). Persisting it
  means tokens survive server restarts/deploys — critical, because the install→first-launch
  gap can span a restart.
- **Mint(user):** `payload = userId(16B) + expiryUnixSeconds(8B)`;
  `mac = HMACSHA256(secret, payload ∥ SHA256(user.Token))`;
  token = base64url(payload ∥ mac) ≈ 75 chars.
- **Validate(token) → User?:** parse payload → look up user → recompute mac with the
  user's *current* auth token hash → `CryptographicOperations.FixedTimeEquals` → check expiry.
- **Binding to `user.Token` is the elegant part:** "Sign out other devices" rotates the
  auth token, which automatically invalidates every outstanding link token for that user —
  zero revocation plumbing.
- **TTL: 72 h** (const). Multi-use within TTL *by design*: every PWA launch hits the same
  `start_url`, so the token must stay redeemable; after the first redemption the PWA has
  its own cookie, so later expiry is harmless. 72 h covers the realistic install→launch gap
  without leaving a months-long bearer URL in the wild.

## 2. Manifest endpoint — `Endpoints/PwaEndpoints.cs` (`MapPwaEndpoints`)

`GET /manifest.webmanifest`:

1. **Base JSON:** if `Data/branding/manifest.webmanifest` exists, parse it (JsonNode) so
   deployments' custom branding is preserved; else use the built-in default (the current
   static file's content moves here; nicety: `name`/`short_name` can come from
   `ChatConfig.ProjectName` instead of hardcoded "Yap").
2. **Identity:** read the auth cookie directly (`UserService.AuthenticateByToken`).
   Authenticated → `start_url = "/pwa-launch?lt=" + Mint(user)`; anonymous → `start_url = "/"`.
3. Always set `"scope": "/"` explicitly (default scope would be derived from start_url's
   directory; make it deterministic).
4. Headers: `Content-Type: application/manifest+json`, `Cache-Control: no-store`
   (a token must never be cached, and an anonymous cached copy must not survive login).

`GET /pwa-launch?lt=...`:

- Request already has a valid auth cookie → `Redirect("/")` (Welcome.razor's logged-in
  path already restores the last PWA route). This is the every-subsequent-launch branch.
- Else `Validate(lt)` → success: `AuthMiddleware.SetAuthCookie(user.Token)` +
  `UserService.RecordKnownIp(user.Id, ip)` + action-log `LOGIN` with
  `info: "pwa_handoff:{username}"` → `Redirect("/")`.
- Invalid/expired → `Redirect("/")` — falls into the normal flow, where the secret-code
  path now exists as recovery.

## 3. Wiring changes

| File | Change |
|---|---|
| `Components/App.razor:15` | `<link rel="manifest" href="manifest.webmanifest" crossorigin="use-credentials" />` — **without `use-credentials` the manifest fetch omits cookies** and every install would get the anonymous manifest. |
| `wwwroot/manifest.webmanifest` | **Delete** (git rm). It would shadow the endpoint via `app.UseStaticFiles()` (Program.cs:307) and collide with `MapStaticAssets`. Content becomes the endpoint's built-in default. |
| Branding middleware (Program.cs:250) | Skip when path == `/manifest.webmanifest` (one condition + comment). It runs before everything and would otherwise serve a raw branding manifest with an untokenized start_url. Branding customization keeps working — the endpoint reads `Data/branding/manifest.webmanifest` as its base. |
| `Program.cs` | Register `LinkTokenService` singleton; `app.MapPwaEndpoints();` with the other endpoint groups. |
| `Middleware/RequestLoggingMiddleware.cs` | Redact the `lt` query value for `/pwa-launch` before logging — the link token is a bearer credential and must not sit in the request log. |

No DB migration. No service-worker changes (its fetch handler doesn't touch the manifest).

## 4. Behavior matrix

| Scenario | Result |
|---|---|
| Logged-in user installs (normal funnel: bot DM → install) | Manifest fetched with cookie → tokenized start_url → **iOS first launch redeems token, lands logged in** |
| Subsequent PWA launches | Cookie present → already-authed branch, token ignored (even if expired) |
| Anonymous visitor installs | start_url `/` → exactly today's behavior |
| "Sign out other devices" between install and first launch | Auth token rotated → link token invalid → login screen + secret code (correct posture) |
| Server restart between install and launch | Secret persisted in Data/ → token still valid |
| Android/desktop installs | Redundant with the Lax cookie but harmless; WebAPK bakes the tokenized URL, expires in 72 h |
| Deployment with `Data/branding/manifest.webmanifest` | Branding fields preserved; start_url still tokenized |

Known caveat (accept): Chrome sometimes reuses a previously-fetched manifest for the
install prompt. If that copy was fetched pre-login, the install gets `start_url: "/"` —
irrelevant on Android (Lax cookie covers it); iOS Safari fetches the manifest at
Add-to-Home-Screen time, so the funnel that matters always gets a fresh token.

## 5. Security notes

- The link token is a bearer credential in a URL. Exposure surfaces: the PWA's (hidden)
  history, WebAPK minting metadata, request logs (redacted per §3). Bounded by: 72 h TTL,
  HTTPS-only transport, HMAC-SHA256 unforgeability, constant-time comparison, and
  invalidation on auth-token rotation.
- `no-store` on the manifest prevents cached-token reuse across users of a shared machine.
- No new login oracle: `/pwa-launch` with a bad token reveals nothing and redirects home.

## 6. Test checklist

1. Desktop DevTools → Network: manifest request sends the cookie; response `start_url`
   contains `lt=`; response is `no-store`.
2. **iOS end-to-end (the headline):** log in in Safari → Add to Home Screen → launch →
   lands in lobby logged in, no login screen. Relaunch after force-quit → still logged in.
3. Anonymous incognito install → start_url `/`, old behavior.
4. Install, then Settings → "sign out other devices", then first PWA launch → login screen
   (token correctly dead), secret code works.
5. Install → restart server → first launch → still logged in (persisted secret).
6. Drop a custom `Data/branding/manifest.webmanifest` → custom name/icons served, start_url
   still tokenized.
7. `RequestLog` shows `/pwa-launch?lt=REDACTED`.

## 7. Estimated scope

~90 lines `LinkTokenService`, ~110 lines `PwaEndpoints`, one-line changes in App.razor +
branding middleware + Program.cs registrations, small logging redaction, one file deleted.
