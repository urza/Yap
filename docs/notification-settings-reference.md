# Reference: notification settings, what a user can set up

> Current-state map of the notification subsystem, written 2026-08-27. This is a feature
> reference, not a bug report. For the 2026-05-29 investigation into missing badges and
> unreliable push, see `unread-and-push-notifications-analysis.md`.

## What a user can set up

There are only **two real switches**, plus per-device cleanup. Everything else is derived from
browser permission and presence.

**1. Enable / Unsubscribe push** (`/settings` → Notifications, `Components/Pages/Settings.razor:331`)

- "Enable Notifications" asks for browser permission, subscribes with the server VAPID key, then
  saves the endpoint per username (`EnablePush`, `Settings.razor:1143`).
- "Unsubscribe" unsubscribes this browser and deletes **all** of the user's server subscriptions
  (`DisablePush`, `Settings.razor:1213`).
- The section shows push status, browser permission, and the device count.

**2. Mute / Unmute** (two places, one flag)

- In Settings (`ToggleMute`, `Settings.razor:1195`) and in the header dropdown, but the header item
  only renders inside an installed PWA (`ChatHeader.razor:77`).
- Both write `User.PushMuted` (`Services/UserService.cs:420`), a persisted per-user flag, not
  per-device.
- Mute keeps the subscription alive. The server still sends the push with `Muted = true`
  (`PushNotificationService.cs:210`), and the service worker updates the app badge but skips the
  banner (`service-worker.js:113`).

**3. Per-device subscription list**

- Each subscribed device is listed with its push host, when it was added, and the last confirmed
  delivery. "Remove" deletes that one endpoint (`RemovePushDevice`, `Settings.razor:1295`).
- "Send test notification" pushes to every device and reports sent/failed. It bypasses mute on
  purpose (`SendTestAsync`, `PushNotificationService.cs:314`).

**4. First-run prompt**

- `PushPermissionPrompt.razor` shows a full-page card, but only inside an installed PWA, only while
  permission is still `default`, and it stops after 3 dismissals (counter in `localStorage`,
  `chat.js:58`).
- If permission is already granted, it silently re-registers the subscription on every load to
  repair rotated endpoints.

## What is NOT configurable

- **Scope.** Push fires for direct messages only (`ChatService.cs:1008`). Room messages never push.
  The prompt text mentions "mentions", but no mention feature exists in the code.
- **In-app sound and tab title.** The `notif.mp3` sound plays for a DM when the tab is hidden, and
  the title gets an unread count (`ChatBase.cs:607`). This has no user setting and `PushMuted` does
  not affect it, because it is client-side and never passes through the push path.
- **Quiet hours, per-channel mute, per-device mute, sound choice or volume.** None exist. Volume is
  hardcoded at 0.5.
- **Badge.** Always updated when an unread count arrives, even while muted.

## The suppression rule

A push is skipped only when the recipient is `Online` **and** at least one session has a visible
page (`ChatService.cs:1027`). `Away` and `Invisible` still get pushed, which is deliberate: Away is
exactly when the phone should buzz. Every decision and result goes into a 200-entry ring buffer
(`Services/NotificationAudit.cs`) that feeds the admin Diagnostics tab.

## Server-side prerequisites

Push is off unless `Vapid:Subject/PublicKey/PrivateKey` are set in `appsettings.json`. Startup
verifies the public key is the true pair of the private key and disables push loudly if not
(`PushNotificationService.cs:118`). Subscriptions are stored as JSON or in the DB per
`ChatSettings:PushSubscriptionStorage`. Admins see each user's subscription count, a "Muted" tag,
and PWA install state in `/admin`, but they cannot change another user's settings.

## The unread dot next to rooms, in one sentence

A room shows an 8px themed dot when its persisted per-user unread counter is above zero and you are
not currently in that room; the counter goes up for every live session except the sender's and
resets when you open, resume, or leave the room, with the sidebar re-rendering on the
`OnUnreadChanged` event.
