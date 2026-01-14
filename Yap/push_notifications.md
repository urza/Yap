# Push Notifications Implementation

## Status: Partially Complete

The infrastructure is in place but needs VAPID keys configured and testing.

## What's Been Implemented

### Server-Side

1. **PushSubscriptionStore.cs** - Persistent storage for push subscriptions
   - Stores in `Data/pushsubscriptions.json`
   - Endpoint as unique key (one per device/browser)
   - Username associated with each endpoint
   - Loads on startup, persists on every change

2. **PushNotificationService.cs** - Sends push notifications via WebPush
   - Reads VAPID config from appsettings.json
   - `SendDmNotificationAsync()` - sends DM notification with message preview and unread count
   - Handles failed/expired subscriptions (removes them from store)

3. **Program.cs** - API endpoints
   - `GET /api/push/vapid-public-key` - returns public key for browser subscription
   - `POST /api/push/subscribe` - registers subscription with username
   - `POST /api/push/unsubscribe` - removes subscription by endpoint

4. **ChatService.cs** - Integration
   - Calls `PushNotificationService.SendDmNotificationAsync()` when DM is sent
   - Sends to recipient (not sender)

### Client-Side

1. **chat.js** - Push subscription functions
   - `isPushSupported()` - checks browser support
   - `requestNotificationPermission()` - requests permission
   - `subscribeToPush(vapidPublicKey)` - subscribes and returns subscription JSON
   - `unsubscribeFromPush()` - unsubscribes
   - `getPushSubscription()` - gets existing subscription

2. **service-worker.js** - Push event handling
   - Receives push, parses payload
   - Updates iOS badge via `setAppBadge()`
   - Shows notification
   - Handles notification click (focuses app or opens URL)

3. **ChatHeader.razor** - UI toggle
   - "Notifications" button in status dropdown
   - Shows enabled/disabled state with checkmark
   - Handles subscribe/unsubscribe flow

## What Remains To Do

### 1. Generate VAPID Keys (Required)

Generate keys at https://vapidkeys.com/ or run:
```bash
npx web-push generate-vapid-keys
```

Update `appsettings.json`:
```json
"Vapid": {
  "Subject": "mailto:your-email@example.com",
  "PublicKey": "YOUR_GENERATED_PUBLIC_KEY",
  "PrivateKey": "YOUR_GENERATED_PRIVATE_KEY"
}
```

### 2. Sign-Out Cleanup (Recommended)

Currently, when a user signs out, their push subscription remains. This could cause issues if another user logs in on the same device.

In `ChatHeader.razor` `SignOut()` method, add:
```csharp
// Before clearing UserState
var existingSub = await JS.InvokeAsync<string?>("getPushSubscription");
if (existingSub != null)
{
    await JS.InvokeAsync<bool>("unsubscribeFromPush");
    // Also call API to remove from server
}
```

### 3. Username Reuse (Consider)

If "Bob" leaves and a new person joins as "Bob", they inherit old subscriptions. Options:
- Clear subscriptions when username is registered (in Login.razor)
- Accept this behavior (old devices won't receive notifications anyway if browser cleared)

### 4. Testing Checklist

- [ ] Deploy with real VAPID keys
- [ ] Test on iOS Safari (install as PWA first)
- [ ] Test on Android Chrome
- [ ] Test on Desktop browsers
- [ ] Verify badge updates on iOS when app is backgrounded
- [ ] Verify notification click opens correct DM
- [ ] Test multiple devices for same user
- [ ] Test app restart (subscriptions should persist)

## Architecture Overview

```
User clicks "Notifications" in header
         │
         ▼
Browser requests notification permission
         │
         ▼
GET /api/push/vapid-public-key
         │
         ▼
Browser subscribes via PushManager (service worker)
         │
         ▼
POST /api/push/subscribe { username, endpoint, p256dh, auth }
         │
         ▼
PushSubscriptionStore saves to Data/pushsubscriptions.json


When DM is sent:
         │
         ▼
ChatService.SendMessageAsync()
         │
         ▼
PushNotificationService.SendDmNotificationAsync()
         │
         ▼
WebPush sends to all recipient's endpoints
         │
         ▼
Service worker receives push event
         │
         ▼
Shows notification + updates badge
```

## Files Reference

| File | Purpose |
|------|---------|
| `Services/PushSubscriptionStore.cs` | Persistent subscription storage |
| `Services/PushNotificationService.cs` | WebPush sending logic |
| `Services/ChatService.cs` | Integration point (SendMessageAsync) |
| `Program.cs` | API endpoints |
| `Components/ChatHeader.razor` | UI toggle |
| `wwwroot/js/chat.js` | Browser push functions |
| `wwwroot/service-worker.js` | Push event handler |
| `appsettings.json` | VAPID configuration |
| `Data/pushsubscriptions.json` | Persisted subscriptions (created at runtime) |

## Dependencies

- **WebPush** NuGet package (v1.0.14) - for sending push notifications
