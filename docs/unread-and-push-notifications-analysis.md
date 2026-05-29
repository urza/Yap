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
