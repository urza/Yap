# Analysis: Missing unread DM badges & unreliable PWA notifications

> Code review of the unread-tracking and push-notification subsystems, prompted by intermittent
> missing unread badges and unreliable PWA push on a multi-device setup (PC-home, PC-work, phone PWA).
> Date: 2026-05-29.

## Summary

There are **two independent root causes** (they also compound each other):

| # | Symptom | Root cause | Certainty |
|---|---------|-----------|-----------|
| 1 | No unread badge on phone for a DM that has new messages | Read-state is **per-user, not per-device**, and any *open* DM page marks messages read for the whole account — with **no page-visibility check** | Certain |
| 2 | PWA push / badge unreliable | Push is suppressed whenever **any** device reports a "visible" page (`IsPageVisible` is an OR across all sessions); worsened by stale retained sessions and no re-subscription path | Certain (multiple contributing factors) |

---

## Issue #1 — Cross-device "mark as read" steals the unread badge (CRITICAL)

Unread counts are keyed by **user**, not by device/session:

- `ChatService.cs:33` — `_readStates` is `(UserId, ChannelId) -> ChannelReadState`. There is exactly **one** unread count per user per channel, shared across PC-home, PC-work, and phone.

When a DM arrives while that conversation is open *anywhere* — `DmChat.razor:182`:

```csharp
private async void HandleMessageReceived(ChatMessage message)
{
    if (message.ChannelId == channelId)   // this device is ON that DM
    {
        await InvokeAsync(() => { messages.Add(message); StateHasChanged(); });
        // Mark as read since we're viewing this channel
        await ChatService.MarkChannelAsReadAsync(UserId, channelId, silent: true);  // line 182
        ...
```

`MarkChannelAsReadAsync` (`ChatService.cs:1158`) sets `UnreadCount = 0` for `(UserId, channelId)` and fires `OnUnreadChanged`. Because the count is per-user, **zeroing it on PC-work zeroes it for the phone too.** When the phone is opened later, the sidebar reads `GetUnreadCount` (`ChatSidebar.razor:146`) → `0` → no badge.

### The actual bug

The mark-as-read in `HandleMessageReceived` has **no page-visibility / focus / status gate**. It fires whenever the `DmChat` component is mounted on that channel and the circuit is alive — even if the tab is backgrounded, the PC is locked, or the circuit is merely **disconnected-but-retained** (retention is **4 hours**, `Program.cs:68`). The same passive pattern exists in `RoomChat.razor:189`, and both pages also mark-read on dispose (`DmChat.razor:269`, `RoomChat.razor:349`).

Cross-device read **sync** (read on one device → cleared on all) is the desired Discord-style behavior and is kept. The fix is to only advance read state on a device that is actually **foreground and not Away**.

---

## Issue #2 — Push suppressed by any "visible" session; subscriptions decay (CRITICAL)

### 2a. `IsPageVisible` is an OR across all of the user's sessions

`ChatService.SendMessageAsync` push decision (`ChatService.cs:799`):

```csharp
var recipientStatus = GetUserStatus(recipient);
var pageVisible = IsPageVisible(recipient);                                  // line 805
if (recipientStatus is UserStatus.Online or UserStatus.Away && pageVisible) // line 807
{
    // Push DM skipped
}
else { /* send push */ }
```

`IsPageVisible` (`ChatService.cs:566`) returns true if **any** session has `PageVisible == true`. So if a desktop tab is open and focused (on any channel), push to the phone is skipped. With the user's everyday multi-device setup, this alone suppresses most phone pushes. Including `Away` in the suppressed set is also self-defeating: Away means idle-for-5-min (`ChatCircuitHandler.cs:13`) — exactly when the phone push is wanted.

### 2b. Stale retained sessions keep `PageVisible = true` for hours

`PageVisible` defaults to `true` (`ChatService.cs:76`) and is only set false by the JS `visibilitychange` event (`ChatBase.cs:566` → `SetPageVisibility`). A network drop never fires it; a closed laptop lid often doesn't. The session is not removed on disconnect — `OnConnectionDownAsync` (`ChatCircuitHandler.cs:102`) only starts a 30 s grace timer; removal happens in `OnCircuitClosedAsync` (`ChatCircuitHandler.cs:143`) after the 4-hour retention. So a PC closed hours ago can linger as a ghost "visible" session, suppressing push.

### 2c. The PWA badge depends on push

The home-screen badge is set in only two places: the service-worker `push` handler (`service-worker.js:94`) and `ChatHeader.UpdateBadgeAsync` (`ChatHeader.razor:298`, live circuit only). When the PWA is closed, the badge is entirely push-driven — suppress the push and there is no badge.

### 2d. No re-subscription path — expired subscriptions stay dead

A subscription is created only when the user taps "Enable" in `PushPermissionPrompt.razor:92` (or the equivalent in Settings), and that prompt only appears while `Notification.permission === 'default'` (`chat.js:430`). Once granted, **nothing re-subscribes or re-validates** on later loads. The service worker has **no `pushsubscriptionchange` handler**. When the browser rotates/expires the endpoint (routine on iOS, and after the server prunes a `410 Gone`/`404` at `PushNotificationService.cs:134`), the device is silently left with no subscription.

---

## Scoped fixes implemented

- **Issue #1:** gate the *passive* auto-mark-read (and dispose mark-read) on `foreground AND status != Away` (Online + Invisible may mark read; Away must not). Explicit navigation-open stays unconditional. Cross-device sync is preserved.
- **Issue #2:**
  - Clear per-session `PageVisible` on circuit disconnect (and re-assert on reconnect) so stale sessions stop suppressing push (shrinks the stale-visible window from ~4 h to ~seconds).
  - Silent re-subscribe on load for already-granted users (`PushPermissionPrompt`), plus a `pushsubscriptionchange` service-worker handler that re-subscribes and re-registers via the existing `/api/push/subscribe` + `/api/push/vapid-public-key` endpoints.
  - Preserve `CreatedAt` on re-save in `PushSubscriptionStore`.
- **Diagnostics (Debug level):** caller-session id on every `MarkChannelAsReadAsync`; per-session visibility breakdown + subscription count at each push decision.
- **Settings:** per-device subscription list (added time, "this device" badge, remove) + a "Send test notification" button.

## Explicitly out of scope (by product decision)

- Changing the overall "suppress push when a device is foreground" policy (i.e. *not* switching to "push to every non-foreground device").
- Persisting last-push history / status (would require new `PushSubscription` fields and an EF migration). The Settings view uses a transient "send test" alive-check instead.

## Follow-up finding (production, 2026-05-29)

After deploy, dead subscriptions self-healed via re-subscribe and badges returned, but badges arrived **late**. Logs showed:

```
fail: Yap.Services.PushNotificationService — Failed to send push to <user>
System.Threading.Tasks.TaskCanceledException: ... HttpClient.Timeout of 100 seconds elapsing
  ... HttpConnection.CheckUsabilityOnScavenge ... ReadAheadWithZeroByteReadAsync ...
```

**Root cause:** `PushNotificationService` is a singleton holding one `HttpClient` (`PushNotificationService.cs:60`) with the **default 100s timeout** and **infinite `PooledConnectionLifetime`**. A keep-alive connection to a push endpoint got silently dropped; the next send reused the stale pooled connection and hung up to 100s before timing out. Because the badge value rides inside the push payload, the badge only appeared once a *later* send used a fresh connection.

**Fix:** `PushLogHandler` now uses a `SocketsHttpHandler` with `PooledConnectionLifetime = 2min`, `PooledConnectionIdleTimeout = 30s`, `ConnectTimeout = 10s`, and the `HttpClient.Timeout` is dropped to **20s**. Connections recycle so stale ones can't accumulate, and a hung send fails fast instead of blocking the notification for 100s.

**Visibility:** `SendToUserAsync` now returns `PushSendResult(Sent, Failed, Total)` and logs `Push to {user}: sent=X failed=Y total=Z`. The Settings "Send test notification" button reports the counts ("Sent to 2/3 device(s); 1 failed"). Note the test payload carries `UnreadCount = 0`, so it shows a **banner** but intentionally does not change the home-screen **badge** (real DMs carry `unreadCount > 0`, which drives the badge in `service-worker.js`).

---

## Follow-up: presence heartbeat & audit trail (2026-07-31)

A second incident (no iOS push, no icon badge, AND no in-app unread badge) traced back to "ghost-foreground" sessions, amplified by a regression: the 2026-07-22 latency probe pinged the circuit every 10s from every **visible** tab, and `CreateInboundActivityHandler` counted that as user activity — so a visible-but-unattended desktop never went auto-Away, kept `ShouldMarkReadOnReceive()` true, and both suppressed push and silently cleared unread. Two more holes fed the same failure: `OnConnectionUpAsync` blindly asserted `PageVisible=true` (re-poisoning every hidden tab after each deploy/blip, since an already-hidden tab fires no `visibilitychange` to correct it), and there was no initial-visibility report on load.

### Changes

- **Probe → presence heartbeat.** `chat.js` tracks last real input (pointerdown/pointermove/keydown/wheel/touchstart) and `ProbePing` now carries `(rtt, visible, idleSeconds)`; it fires **always** (hidden tabs throttled to ~60s; RTT measured only while visible; the 3-failure give-up is gone — reporting is load-bearing now and must survive outages). `ChatService.ReportClientStateAsync` is the single presence authority: updates `PageVisible`/`LastActivity`/`LastReportAt`, restores from auto-away on fresh input (<30s), applies auto-away when reported idle ≥5 min.
- **Auto-away rules moved to ChatService** (single applier): *heartbeat rule* — Away when every session is input-idle ≥5 min or heartbeat-stale >90s; *disconnect rule* (30s grace timer, still armed by `ChatCircuitHandler`) — Away unless another session is a live foreground client (visible + heartbeat ≤35s), so a reading-but-not-typing desktop no longer flaps Away when the phone locks. Circuit inbound activity no longer counts as user activity; `TouchSessionActivity`/`AreAllSessionsIdle` deleted.
- **Visibility is never assumed.** The connection-up `PageVisible=true` assert is removed; `setupVisibilityListener` returns the real initial state (and installs its document listener once — it used to stack one per page navigation, invoking the handler N times per flip); the heartbeat reconciles every ~10s.
- **`Away` dropped from the push-suppress condition** — now `Online && anyVisible` suppresses (§2a called Away-suppression self-defeating; with trustworthy visibility it's finally safe to fix).
- **Two stuck-Away guards:** the page-navigation session re-join only re-asserts status on drift (an unconditional `SetUserStatusAsync` counted as a *manual* change and wiped `_statusBeforeAutoAway`); foregrounding calls `TryRestoreFromAutoAway` before the tab-resume mark-read (which is Away-gated).
- **Observability (Admin → Diagnostics):** a **Sessions presence truth table** (per session: device, transport/RTT via a `CircuitId` join to `CircuitTracker`, connected, visible, status, viewing label, last input, heartbeat age; warning tint for visible-but-circuit-gone ghosts) and **`NotificationAudit`** in-memory ring buffers (cap 200): push decisions with per-session snapshots, send results, and DM unread increments/clears with a `source` tag (`open`/`receive`/`resume`/`dispose`) plus the clearing device's state. The TEMPORARY `callerSessionId` debug log is retired. The service worker POSTs delivery receipts to `POST /api/push/delivered` (anonymous — the subscription endpoint URL is the bearer); Settings shows "Delivered X ago" per device.

### Accepted behavior changes

Watching a video >5 min in a visible tab now reads as Away (input-based idle, Discord-like). A hidden connected tab keeps the user Online for up to 5 min after their last input (previously 30s after another device disconnected). For ≤10s after a reconnect a visible tab counts as not-visible until its first heartbeat — worst case one extra push while actively looking. Settings "Active X ago" now means last user input.
