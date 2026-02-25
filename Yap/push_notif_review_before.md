# Push Notifications Code Review (Before Fixes)

## How It Works (Flow Overview)

1. **Subscribe**: User clicks "Notifications" in ChatHeader dropdown → browser requests permission → JS calls `subscribeToPush()` which gets a `PushSubscription` from the browser's `PushManager` → subscription details (endpoint, p256dh, auth) are POSTed to `/api/push/subscribe` with the username → stored in `PushSubscriptionStore` (in-memory `ConcurrentDictionary` + persisted via JSON file or DB).

2. **Send**: When a DM is sent in `ChatService.SendMessageAsync()` → fire-and-forget call to `PushNotificationService.SendDmNotificationAsync()` → sends WebPush to all of the recipient's registered endpoints.

3. **Receive**: Service worker catches the `push` event → parses payload → calls `showNotification()` + `setAppBadge()`.

4. **Click**: Service worker `notificationclick` → focuses existing window (posts `NOTIFICATION_CLICK` message) or opens new window → `chat.js` listener navigates to the DM URL.

## What Looks Good

- **Clean architecture** — persistence abstracted via `IPushSubscriptionPersistence` with JSON and DB implementations, easy to swap.
- **Expired subscription cleanup** — `WebPushException` with 410 Gone / 404 removes stale subscriptions automatically.
- **Fire-and-forget sending** — `_ = _pushService.SendDmNotificationAsync(...)` doesn't block message delivery.
- **VAPID guard** — `_isConfigured` check prevents crashes when keys aren't set; placeholder detection (`GENERATE_YOUR_OWN`) is nice.
- **Tag-based coalescing** — `tag: "dm-{fromUsername}"` means multiple messages from the same sender replace the notification instead of stacking.
- **Icons exist** — `icon-192.png` and `icon-512.png` are both present in `wwwroot/`.

## Potential Problems

### 1. Unread count is always hardcoded to 1 (Medium severity)
`ChatService.cs:570`:
```csharp
_ = _pushService.SendDmNotificationAsync(recipient, username, preview, 1);
```
The `unreadCount` parameter is always `1`, regardless of how many unread messages the user actually has. The badge on iOS/Android will show "1" even if there are 5 unread DMs. You should compute the real unread count for the recipient.

### 2. No authentication on push API endpoints (Medium-High severity)
`Program.cs:224-248` — The subscribe endpoint accepts any username in the POST body with zero authentication:
```csharp
app.MapPost("/api/push/subscribe", async (HttpContext context, PushSubscriptionStore store) =>
```
Anyone can call `POST /api/push/subscribe` with `{"username": "victimUser", "endpoint": "attacker-endpoint", ...}` and start receiving that user's DM notifications. Similarly, anyone can unsubscribe other users' endpoints.

At minimum, validate that the requesting user matches the username in the subscription (e.g., check the auth cookie/session).

### 3. Sign-out doesn't clean up push subscriptions (Medium severity)
`ChatHeader.razor:267-289` — The `SignOut()` method removes the user from ChatService, deletes the user record, and redirects, but never unsubscribes from push or removes the server-side subscription. Your own `push_notifications.md` doc calls this out as a TODO. This means:
- After sign-out, the browser still has an active push subscription
- The server still sends notifications to that endpoint
- If another user logs in on the same device, they won't get notifications (different username), but the old user's subscription hangs around

### 4. `HttpClient` created with `new HttpClient()` inside Blazor components (Medium severity)
`ChatHeader.razor:202,217`:
```csharp
using var http = new HttpClient();
await http.PostAsJsonAsync($"{Navigation.BaseUri}api/push/unsubscribe", ...);
```
This creates a new `HttpClient` instance per toggle click. In Blazor Server, the component runs on the server, so this HTTP call goes server → server (localhost). Issues:
- `new HttpClient()` in a hot path causes socket exhaustion over time
- The localhost call includes no auth cookies, so if you add auth to the endpoints, this will break
- **Better approach**: inject `PushSubscriptionStore` directly into the component (it's a singleton) and call it without HTTP. No need for HTTP roundtrip when the code is already running on the server.

### 5. Service worker scope and caching (Low severity)
The service worker registers at root (`service-worker.js`) which is correct. However, the fetch handler caches icon/audio assets but uses a hardcoded list — if filenames change (e.g., `.NET 10` fingerprinted static assets via `MapStaticAssets`), the cache list won't match. The `endsWith` check should handle this, but worth verifying that fingerprinted paths like `/icon-192.abc123.png` still match `/icon-192.png`.

### 6. No notification suppression when user is active (Low-Medium severity)
`ChatService.cs:563-571` — Push notifications are sent for every DM regardless of whether the recipient is currently viewing that DM conversation. If the user is actively chatting in the DM, they still get a push notification on their phone/other devices. You may want to check if the user has the tab visible and is on the DM page before sending.

### 7. `navigator.serviceWorker.ready` may hang if SW fails (Low severity)
`chat.js:364,398,417` — `navigator.serviceWorker.ready` returns a promise that never rejects — it waits indefinitely for a service worker. If the SW registration fails (line 89 in App.razor catches this), `subscribeToPush()` will hang forever. There's no timeout.

### 8. Missing `renotify` consideration (Very Low)
The notification options include `renotify: true` with `tag`, which means the user gets re-notified (vibration/sound) even when a notification with the same tag already exists. This is fine for chat but could be annoying if they get rapid-fire DMs from the same person.

## Summary

The architecture is solid. The most impactful things to address before testing on a deployed system:

| Priority | Issue | Fix effort |
|----------|-------|------------|
| **High** | Unauthenticated push API endpoints | Small — validate auth cookie |
| **Medium** | Hardcoded unread count = 1 | Small — compute real count |
| **Medium** | Sign-out doesn't unsubscribe | Small — add cleanup in SignOut() |
| **Medium** | `new HttpClient()` from server-side Blazor | Small — call store directly |
| **Low** | No suppression when user is active | Medium — check circuit/page state |
