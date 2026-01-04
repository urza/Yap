# PWA Implementation Guide for Blazor Server Chat App (.NET 10)

## Overview

This guide covers implementing Progressive Web App (PWA) features for a Discord-inspired Blazor Server chat application. The focus is on:

- **Installability** - Add to home screen functionality
- **Push Notifications** - Server-sent notifications even when app is closed
- **Badge API** - Unread message count on app icon
- **iOS Compatibility** - Working within Safari/iOS limitations

## Important Constraints

### Blazor Server + PWA Reality

Blazor Server relies on a persistent SignalR connection, so **offline functionality is not applicable**. However, we can still get:

- ✅ Installable PWA (standalone window, home screen icon)
- ✅ Push notifications
- ✅ Badge API for unread counts
- ✅ Native app-like experience
- ❌ Offline mode (requires server connection)

### iOS Limitations

| Feature | iOS Requirement |
|---------|-----------------|
| PWA Install | Manual: Share → Add to Home Screen (no auto-prompt) |
| Push Notifications | iOS 16.4+, must be installed to home screen first |
| Badge API | iOS 16.4+, installed PWA, notification permission granted |
| Storage | 7-day eviction if PWA not accessed; ~50MB cache limit |

**Critical iOS Badge Quirk:** `setAppBadge(0)` and `setAppBadge()` (no args) clear the badge instead of showing an empty dot. Always use `clearAppBadge()` to clear, and only call `setAppBadge(n)` when n > 0.

---

## File Structure

After implementation, your wwwroot and project should have:

```
wwwroot/
├── manifest.json
├── service-worker.js
├── js/
│   └── pwa.js
├── icons/
│   ├── icon-72.png
│   ├── icon-96.png
│   ├── icon-128.png
│   ├── icon-144.png
│   ├── icon-152.png
│   ├── icon-192.png
│   ├── icon-384.png
│   ├── icon-512.png
│   └── badge-96.png (monochrome for notification badge)
│
Services/
├── PwaBadgeService.cs
├── PushNotificationService.cs
└── VapidConfiguration.cs

Components/
├── PwaInstallPrompt.razor
└── NotificationPermissionPrompt.razor
```

---

## Step 1: Web App Manifest

Create `wwwroot/manifest.json`:

```json
{
  "name": "Your Chat App Name",
  "short_name": "Chat",
  "description": "Discord-inspired minimalist chat",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#36393f",
  "theme_color": "#5865f2",
  "orientation": "portrait-primary",
  "icons": [
    {
      "src": "/icons/icon-72.png",
      "sizes": "72x72",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-96.png",
      "sizes": "96x96",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-128.png",
      "sizes": "128x128",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-144.png",
      "sizes": "144x144",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-152.png",
      "sizes": "152x152",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-192.png",
      "sizes": "192x192",
      "type": "image/png",
      "purpose": "any"
    },
    {
      "src": "/icons/icon-384.png",
      "sizes": "384x384",
      "type": "image/png"
    },
    {
      "src": "/icons/icon-512.png",
      "sizes": "512x512",
      "type": "image/png",
      "purpose": "any maskable"
    }
  ],
  "categories": ["social", "communication"],
  "shortcuts": [
    {
      "name": "New Message",
      "url": "/",
      "icons": [{ "src": "/icons/icon-96.png", "sizes": "96x96" }]
    }
  ]
}
```

---

## Step 2: Service Worker

Create `wwwroot/service-worker.js`:

```javascript
// Service Worker for Blazor Server PWA
// Handles: push notifications, badge updates, notification clicks
// Does NOT handle: offline caching (not applicable for Blazor Server)

const CACHE_NAME = 'blazor-chat-v1';

// Install - minimal caching for Blazor Server (just icons/manifest)
self.addEventListener('install', event => {
    console.log('[SW] Installing service worker...');
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache => {
            return cache.addAll([
                '/manifest.json',
                '/icons/icon-192.png',
                '/icons/icon-512.png',
                '/icons/badge-96.png'
            ]);
        })
    );
    self.skipWaiting();
});

// Activate - clean up old caches
self.addEventListener('activate', event => {
    console.log('[SW] Activating service worker...');
    event.waitUntil(
        caches.keys().then(cacheNames => {
            return Promise.all(
                cacheNames
                    .filter(name => name !== CACHE_NAME)
                    .map(name => caches.delete(name))
            );
        })
    );
    self.clients.claim();
});

// Fetch - pass through (no offline support for Blazor Server)
self.addEventListener('fetch', event => {
    // Let all requests pass through to the server
    // Blazor Server requires live connection
});

// Push notification received
self.addEventListener('push', event => {
    console.log('[SW] Push received:', event);
    
    let data = {
        title: 'New Message',
        body: 'You have a new message',
        icon: '/icons/icon-192.png',
        badge: '/icons/badge-96.png',
        tag: 'chat-message',
        url: '/',
        unreadCount: 0
    };
    
    // Parse push data if available
    if (event.data) {
        try {
            const payload = event.data.json();
            data = { ...data, ...payload };
        } catch (e) {
            console.error('[SW] Error parsing push data:', e);
            data.body = event.data.text();
        }
    }
    
    const promises = [];
    
    // Update badge count
    if ('setAppBadge' in self.navigator) {
        if (data.unreadCount && data.unreadCount > 0) {
            promises.push(
                self.navigator.setAppBadge(data.unreadCount)
                    .catch(err => console.error('[SW] Badge error:', err))
            );
        } else if (data.clearBadge) {
            promises.push(
                self.navigator.clearAppBadge()
                    .catch(err => console.error('[SW] Clear badge error:', err))
            );
        }
    }
    
    // Show notification (required for push on iOS)
    const notificationOptions = {
        body: data.body,
        icon: data.icon,
        badge: data.badge,
        tag: data.tag,
        renotify: true,
        requireInteraction: false,
        data: {
            url: data.url,
            messageId: data.messageId,
            channelId: data.channelId
        },
        actions: [
            { action: 'open', title: 'Open' },
            { action: 'dismiss', title: 'Dismiss' }
        ]
    };
    
    promises.push(
        self.registration.showNotification(data.title, notificationOptions)
    );
    
    event.waitUntil(Promise.all(promises));
});

// Notification click handler
self.addEventListener('notificationclick', event => {
    console.log('[SW] Notification clicked:', event);
    
    event.notification.close();
    
    if (event.action === 'dismiss') {
        return;
    }
    
    const urlToOpen = event.notification.data?.url || '/';
    
    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then(windowClients => {
                // Check if app is already open
                for (const client of windowClients) {
                    if (client.url.includes(self.location.origin) && 'focus' in client) {
                        // Send message to existing client to navigate
                        client.postMessage({
                            type: 'NOTIFICATION_CLICK',
                            url: urlToOpen,
                            data: event.notification.data
                        });
                        return client.focus();
                    }
                }
                // Open new window if app not open
                return clients.openWindow(urlToOpen);
            })
    );
});

// Notification close handler
self.addEventListener('notificationclose', event => {
    console.log('[SW] Notification closed:', event);
});

// Message handler (for communication with main app)
self.addEventListener('message', event => {
    console.log('[SW] Message received:', event.data);
    
    if (event.data.type === 'SKIP_WAITING') {
        self.skipWaiting();
    }
    
    if (event.data.type === 'UPDATE_BADGE') {
        if ('setAppBadge' in self.navigator) {
            const count = event.data.count;
            if (count > 0) {
                self.navigator.setAppBadge(count);
            } else {
                self.navigator.clearAppBadge();
            }
        }
    }
});
```

---

## Step 3: PWA JavaScript Interop

Create `wwwroot/js/pwa.js`:

```javascript
// PWA functionality for Blazor interop
window.pwa = {
    // ==========================================
    // Installation Detection
    // ==========================================
    
    deferredPrompt: null,
    
    init: function() {
        // Capture the install prompt for later use
        window.addEventListener('beforeinstallprompt', (e) => {
            console.log('[PWA] beforeinstallprompt fired');
            e.preventDefault();
            window.pwa.deferredPrompt = e;
            
            // Notify Blazor that install is available
            if (window.pwa.onInstallAvailable) {
                window.pwa.onInstallAvailable.invokeMethodAsync('OnInstallAvailable');
            }
        });
        
        // Detect successful installation
        window.addEventListener('appinstalled', () => {
            console.log('[PWA] App installed');
            window.pwa.deferredPrompt = null;
            
            if (window.pwa.onInstalled) {
                window.pwa.onInstalled.invokeMethodAsync('OnInstalled');
            }
        });
    },
    
    isInstalled: function() {
        // Check if running as installed PWA
        return window.matchMedia('(display-mode: standalone)').matches ||
               window.navigator.standalone === true || // iOS Safari
               document.referrer.includes('android-app://');
    },
    
    isInstallable: function() {
        return window.pwa.deferredPrompt !== null;
    },
    
    promptInstall: async function() {
        if (!window.pwa.deferredPrompt) {
            console.log('[PWA] No deferred prompt available');
            return { outcome: 'unavailable' };
        }
        
        window.pwa.deferredPrompt.prompt();
        const result = await window.pwa.deferredPrompt.userChoice;
        console.log('[PWA] Install prompt result:', result);
        
        window.pwa.deferredPrompt = null;
        return result;
    },
    
    // Register callback for install availability
    setInstallCallback: function(dotNetRef) {
        window.pwa.onInstallAvailable = dotNetRef;
    },
    
    setInstalledCallback: function(dotNetRef) {
        window.pwa.onInstalled = dotNetRef;
    },
    
    // ==========================================
    // Platform Detection
    // ==========================================
    
    getPlatform: function() {
        const ua = navigator.userAgent;
        
        if (/iPad|iPhone|iPod/.test(ua)) {
            return 'ios';
        }
        if (/android/i.test(ua)) {
            return 'android';
        }
        if (/Mac/.test(ua)) {
            return 'macos';
        }
        if (/Win/.test(ua)) {
            return 'windows';
        }
        if (/Linux/.test(ua)) {
            return 'linux';
        }
        return 'unknown';
    },
    
    isIOS: function() {
        return /iPad|iPhone|iPod/.test(navigator.userAgent);
    },
    
    getIOSVersion: function() {
        const match = navigator.userAgent.match(/OS (\d+)_(\d+)/);
        if (match) {
            return parseFloat(`${match[1]}.${match[2]}`);
        }
        return 0;
    },
    
    // ==========================================
    // Badge API
    // ==========================================
    
    badge: {
        isSupported: function() {
            return 'setAppBadge' in navigator;
        },
        
        set: async function(count) {
            if (!('setAppBadge' in navigator)) {
                console.warn('[PWA] Badge API not supported');
                return false;
            }
            
            try {
                if (count > 0) {
                    await navigator.setAppBadge(count);
                    console.log('[PWA] Badge set to:', count);
                } else {
                    await navigator.clearAppBadge();
                    console.log('[PWA] Badge cleared');
                }
                return true;
            } catch (error) {
                console.error('[PWA] Badge error:', error);
                return false;
            }
        },
        
        clear: async function() {
            if (!('clearAppBadge' in navigator)) {
                return false;
            }
            
            try {
                await navigator.clearAppBadge();
                console.log('[PWA] Badge cleared');
                return true;
            } catch (error) {
                console.error('[PWA] Clear badge error:', error);
                return false;
            }
        }
    },
    
    // ==========================================
    // Notifications
    // ==========================================
    
    notifications: {
        isSupported: function() {
            return 'Notification' in window && 'serviceWorker' in navigator;
        },
        
        getPermission: function() {
            if (!('Notification' in window)) {
                return 'unsupported';
            }
            return Notification.permission;
        },
        
        requestPermission: async function() {
            if (!('Notification' in window)) {
                return 'unsupported';
            }
            
            try {
                const result = await Notification.requestPermission();
                console.log('[PWA] Notification permission:', result);
                return result;
            } catch (error) {
                console.error('[PWA] Permission request error:', error);
                return 'error';
            }
        },
        
        // Show local notification (not push)
        show: async function(title, options) {
            if (Notification.permission !== 'granted') {
                console.warn('[PWA] Notification permission not granted');
                return false;
            }
            
            try {
                const registration = await navigator.serviceWorker.ready;
                await registration.showNotification(title, options);
                return true;
            } catch (error) {
                console.error('[PWA] Show notification error:', error);
                return false;
            }
        }
    },
    
    // ==========================================
    // Push Subscription
    // ==========================================
    
    push: {
        isSupported: function() {
            return 'PushManager' in window && 'serviceWorker' in navigator;
        },
        
        getSubscription: async function() {
            try {
                const registration = await navigator.serviceWorker.ready;
                const subscription = await registration.pushManager.getSubscription();
                return subscription ? JSON.stringify(subscription.toJSON()) : null;
            } catch (error) {
                console.error('[PWA] Get subscription error:', error);
                return null;
            }
        },
        
        subscribe: async function(vapidPublicKey) {
            try {
                const registration = await navigator.serviceWorker.ready;
                
                // Convert VAPID key to Uint8Array
                const convertedKey = window.pwa.push.urlBase64ToUint8Array(vapidPublicKey);
                
                const subscription = await registration.pushManager.subscribe({
                    userVisibleOnly: true,
                    applicationServerKey: convertedKey
                });
                
                console.log('[PWA] Push subscribed:', subscription);
                return JSON.stringify(subscription.toJSON());
            } catch (error) {
                console.error('[PWA] Subscribe error:', error);
                return null;
            }
        },
        
        unsubscribe: async function() {
            try {
                const registration = await navigator.serviceWorker.ready;
                const subscription = await registration.pushManager.getSubscription();
                
                if (subscription) {
                    await subscription.unsubscribe();
                    console.log('[PWA] Push unsubscribed');
                    return true;
                }
                return false;
            } catch (error) {
                console.error('[PWA] Unsubscribe error:', error);
                return false;
            }
        },
        
        // Helper to convert VAPID key
        urlBase64ToUint8Array: function(base64String) {
            const padding = '='.repeat((4 - base64String.length % 4) % 4);
            const base64 = (base64String + padding)
                .replace(/-/g, '+')
                .replace(/_/g, '/');
            
            const rawData = window.atob(base64);
            const outputArray = new Uint8Array(rawData.length);
            
            for (let i = 0; i < rawData.length; ++i) {
                outputArray[i] = rawData.charCodeAt(i);
            }
            return outputArray;
        }
    },
    
    // ==========================================
    // Service Worker
    // ==========================================
    
    serviceWorker: {
        register: async function() {
            if (!('serviceWorker' in navigator)) {
                console.warn('[PWA] Service workers not supported');
                return false;
            }
            
            try {
                const registration = await navigator.serviceWorker.register('/service-worker.js');
                console.log('[PWA] Service worker registered:', registration);
                return true;
            } catch (error) {
                console.error('[PWA] Service worker registration failed:', error);
                return false;
            }
        },
        
        getRegistration: async function() {
            if (!('serviceWorker' in navigator)) {
                return null;
            }
            return await navigator.serviceWorker.ready;
        },
        
        update: async function() {
            try {
                const registration = await navigator.serviceWorker.ready;
                await registration.update();
                console.log('[PWA] Service worker updated');
                return true;
            } catch (error) {
                console.error('[PWA] Service worker update failed:', error);
                return false;
            }
        },
        
        // Send message to service worker
        postMessage: function(message) {
            if (navigator.serviceWorker.controller) {
                navigator.serviceWorker.controller.postMessage(message);
            }
        }
    }
};

// Initialize on load
window.pwa.init();
```

---

## Step 4: App Layout Integration

In your `App.razor` or main layout, add the necessary head elements and scripts:

```html
<!-- In <head> section -->
<link rel="manifest" href="/manifest.json" />
<meta name="theme-color" content="#5865f2" />
<meta name="apple-mobile-web-app-capable" content="yes" />
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
<meta name="apple-mobile-web-app-title" content="Chat" />

<!-- iOS icons -->
<link rel="apple-touch-icon" href="/icons/icon-152.png" />
<link rel="apple-touch-icon" sizes="180x180" href="/icons/icon-192.png" />
<link rel="apple-touch-icon" sizes="167x167" href="/icons/icon-192.png" />

<!-- At end of <body>, before closing tag -->
<script src="/js/pwa.js"></script>
<script>
    // Register service worker
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register('/service-worker.js')
            .then(reg => console.log('SW registered:', reg.scope))
            .catch(err => console.error('SW registration failed:', err));
    }
</script>
```

---

## Step 5: C# Services

### PwaService.cs

```csharp
using Microsoft.JSInterop;

namespace YourApp.Services;

public class PwaService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private DotNetObjectReference<PwaService>? _dotNetRef;
    
    public event Func<Task>? OnInstallAvailable;
    public event Func<Task>? OnInstalled;
    
    public PwaService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }
    
    public async Task InitializeAsync()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        await _jsRuntime.InvokeVoidAsync("pwa.setInstallCallback", _dotNetRef);
        await _jsRuntime.InvokeVoidAsync("pwa.setInstalledCallback", _dotNetRef);
    }
    
    [JSInvokable]
    public async Task OnInstallAvailable()
    {
        if (OnInstallAvailable != null)
            await OnInstallAvailable.Invoke();
    }
    
    [JSInvokable]
    public async Task OnInstalled()
    {
        if (OnInstalled != null)
            await OnInstalled.Invoke();
    }
    
    // Installation
    public async Task<bool> IsInstalledAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.isInstalled");
        }
        catch { return false; }
    }
    
    public async Task<bool> IsInstallableAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.isInstallable");
        }
        catch { return false; }
    }
    
    public async Task<string> PromptInstallAsync()
    {
        try
        {
            var result = await _jsRuntime.InvokeAsync<JsonElement>("pwa.promptInstall");
            return result.GetProperty("outcome").GetString() ?? "unknown";
        }
        catch { return "error"; }
    }
    
    // Platform detection
    public async Task<string> GetPlatformAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string>("pwa.getPlatform");
        }
        catch { return "unknown"; }
    }
    
    public async Task<bool> IsIOSAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.isIOS");
        }
        catch { return false; }
    }
    
    // Badge
    public async Task<bool> IsBadgeSupportedAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.badge.isSupported");
        }
        catch { return false; }
    }
    
    public async Task<bool> SetBadgeAsync(int count)
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.badge.set", count);
        }
        catch { return false; }
    }
    
    public async Task<bool> ClearBadgeAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.badge.clear");
        }
        catch { return false; }
    }
    
    // Notifications
    public async Task<bool> IsNotificationSupportedAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.notifications.isSupported");
        }
        catch { return false; }
    }
    
    public async Task<string> GetNotificationPermissionAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string>("pwa.notifications.getPermission");
        }
        catch { return "unsupported"; }
    }
    
    public async Task<string> RequestNotificationPermissionAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string>("pwa.notifications.requestPermission");
        }
        catch { return "error"; }
    }
    
    // Push
    public async Task<bool> IsPushSupportedAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.push.isSupported");
        }
        catch { return false; }
    }
    
    public async Task<string?> GetPushSubscriptionAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string?>("pwa.push.getSubscription");
        }
        catch { return null; }
    }
    
    public async Task<string?> SubscribeToPushAsync(string vapidPublicKey)
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string?>("pwa.push.subscribe", vapidPublicKey);
        }
        catch { return null; }
    }
    
    public async Task<bool> UnsubscribeFromPushAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("pwa.push.unsubscribe");
        }
        catch { return false; }
    }
    
    public async ValueTask DisposeAsync()
    {
        _dotNetRef?.Dispose();
    }
}
```

### PushNotificationService.cs (Server-side)

```csharp
using System.Text.Json;
using WebPush;

namespace YourApp.Services;

public class PushNotificationService
{
    private readonly VapidDetails _vapidDetails;
    private readonly WebPushClient _webPushClient;
    private readonly ILogger<PushNotificationService> _logger;
    
    public PushNotificationService(
        IConfiguration configuration,
        ILogger<PushNotificationService> logger)
    {
        _logger = logger;
        _webPushClient = new WebPushClient();
        
        // Load VAPID keys from configuration
        var vapidSubject = configuration["Vapid:Subject"] 
            ?? throw new InvalidOperationException("Vapid:Subject not configured");
        var vapidPublicKey = configuration["Vapid:PublicKey"] 
            ?? throw new InvalidOperationException("Vapid:PublicKey not configured");
        var vapidPrivateKey = configuration["Vapid:PrivateKey"] 
            ?? throw new InvalidOperationException("Vapid:PrivateKey not configured");
        
        _vapidDetails = new VapidDetails(vapidSubject, vapidPublicKey, vapidPrivateKey);
    }
    
    public string GetPublicKey() => _vapidDetails.PublicKey;
    
    public async Task SendNotificationAsync(
        PushSubscriptionInfo subscription,
        PushPayload payload)
    {
        try
        {
            var pushSubscription = new PushSubscription(
                subscription.Endpoint,
                subscription.Keys.P256dh,
                subscription.Keys.Auth);
            
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            await _webPushClient.SendNotificationAsync(
                pushSubscription, 
                json, 
                _vapidDetails);
            
            _logger.LogInformation("Push notification sent to {Endpoint}", subscription.Endpoint);
        }
        catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            // Subscription expired - should remove from database
            _logger.LogWarning("Push subscription expired: {Endpoint}", subscription.Endpoint);
            throw new SubscriptionExpiredException(subscription.Endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send push notification");
            throw;
        }
    }
    
    public async Task SendToUserAsync(
        string userId,
        string title,
        string body,
        int unreadCount = 0,
        string? url = null,
        IPushSubscriptionStore subscriptionStore = null!)
    {
        var subscriptions = await subscriptionStore.GetSubscriptionsForUserAsync(userId);
        
        var payload = new PushPayload
        {
            Title = title,
            Body = body,
            UnreadCount = unreadCount,
            Url = url ?? "/",
            Tag = $"chat-{DateTime.UtcNow.Ticks}"
        };
        
        var tasks = subscriptions.Select(sub => SendNotificationAsync(sub, payload));
        
        await Task.WhenAll(tasks);
    }
}

// DTOs
public record PushSubscriptionInfo
{
    public string Endpoint { get; init; } = "";
    public PushSubscriptionKeys Keys { get; init; } = new();
}

public record PushSubscriptionKeys
{
    public string P256dh { get; init; } = "";
    public string Auth { get; init; } = "";
}

public record PushPayload
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Icon { get; init; } = "/icons/icon-192.png";
    public string Badge { get; init; } = "/icons/badge-96.png";
    public string Tag { get; init; } = "chat-notification";
    public string Url { get; init; } = "/";
    public int UnreadCount { get; init; }
    public string? MessageId { get; init; }
    public string? ChannelId { get; init; }
    public bool ClearBadge { get; init; }
}

public class SubscriptionExpiredException : Exception
{
    public string Endpoint { get; }
    public SubscriptionExpiredException(string endpoint) : base($"Subscription expired: {endpoint}")
    {
        Endpoint = endpoint;
    }
}

// Interface for subscription storage
public interface IPushSubscriptionStore
{
    Task SaveSubscriptionAsync(string userId, PushSubscriptionInfo subscription);
    Task RemoveSubscriptionAsync(string endpoint);
    Task<IEnumerable<PushSubscriptionInfo>> GetSubscriptionsForUserAsync(string userId);
}
```

---

## Step 6: Registration in Program.cs

```csharp
// Add services
builder.Services.AddScoped<PwaService>();
builder.Services.AddSingleton<PushNotificationService>();

// Add your subscription store implementation
builder.Services.AddScoped<IPushSubscriptionStore, YourPushSubscriptionStore>();
```

---

## Step 7: Configuration (appsettings.json)

```json
{
  "Vapid": {
    "Subject": "mailto:admin@yourapp.com",
    "PublicKey": "YOUR_VAPID_PUBLIC_KEY",
    "PrivateKey": "YOUR_VAPID_PRIVATE_KEY"
  }
}
```

### Generate VAPID Keys

Use the `web-push` npm package or online generator:

```bash
# Using npm
npm install -g web-push
web-push generate-vapid-keys
```

Or in C#:
```csharp
var keys = VapidHelper.GenerateVapidKeys();
Console.WriteLine($"Public: {keys.PublicKey}");
Console.WriteLine($"Private: {keys.PrivateKey}");
```

---

## Step 8: UI Components

### PwaInstallPrompt.razor

```razor
@inject PwaService PwaService
@implements IAsyncDisposable

@if (_showPrompt)
{
    <div class="pwa-install-prompt">
        @if (_isIOS)
        {
            <div class="ios-install-instructions">
                <p>To install this app:</p>
                <ol>
                    <li>Tap the <strong>Share</strong> button <span class="share-icon">⬆️</span></li>
                    <li>Scroll down and tap <strong>Add to Home Screen</strong></li>
                </ol>
                <button @onclick="DismissPrompt">Got it</button>
            </div>
        }
        else if (_isInstallable)
        {
            <div class="install-prompt">
                <p>Install our app for the best experience!</p>
                <button @onclick="InstallApp">Install</button>
                <button @onclick="DismissPrompt">Not now</button>
            </div>
        }
    </div>
}

@code {
    private bool _showPrompt;
    private bool _isIOS;
    private bool _isInstalled;
    private bool _isInstallable;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await PwaService.InitializeAsync();
            
            _isInstalled = await PwaService.IsInstalledAsync();
            _isIOS = await PwaService.IsIOSAsync();
            _isInstallable = await PwaService.IsInstallableAsync();
            
            // Show prompt if not installed
            if (!_isInstalled)
            {
                // Check if user previously dismissed
                var dismissed = await GetDismissedStateAsync();
                _showPrompt = !dismissed;
            }
            
            PwaService.OnInstallAvailable += OnInstallAvailable;
            PwaService.OnInstalled += OnInstalled;
            
            StateHasChanged();
        }
    }
    
    private async Task OnInstallAvailable()
    {
        _isInstallable = true;
        _showPrompt = true;
        await InvokeAsync(StateHasChanged);
    }
    
    private async Task OnInstalled()
    {
        _isInstalled = true;
        _showPrompt = false;
        await InvokeAsync(StateHasChanged);
    }
    
    private async Task InstallApp()
    {
        var result = await PwaService.PromptInstallAsync();
        if (result == "accepted")
        {
            _showPrompt = false;
        }
    }
    
    private async Task DismissPrompt()
    {
        _showPrompt = false;
        await SaveDismissedStateAsync();
    }
    
    private Task<bool> GetDismissedStateAsync()
    {
        // Implement with local storage or your preference store
        return Task.FromResult(false);
    }
    
    private Task SaveDismissedStateAsync()
    {
        // Implement with local storage or your preference store
        return Task.CompletedTask;
    }
    
    public async ValueTask DisposeAsync()
    {
        PwaService.OnInstallAvailable -= OnInstallAvailable;
        PwaService.OnInstalled -= OnInstalled;
        await PwaService.DisposeAsync();
    }
}
```

### NotificationSetup.razor

```razor
@inject PwaService PwaService
@inject PushNotificationService PushService

<div class="notification-setup">
    @if (!_isSupported)
    {
        <p class="not-supported">Push notifications are not supported on this device.</p>
    }
    else if (_requiresInstall)
    {
        <p>Install this app to enable notifications.</p>
    }
    else if (_permission == "granted")
    {
        <p class="enabled">✓ Notifications enabled</p>
        <button @onclick="Unsubscribe">Disable notifications</button>
    }
    else if (_permission == "denied")
    {
        <p class="denied">Notifications are blocked. Please enable them in your browser settings.</p>
    }
    else
    {
        <button @onclick="EnableNotifications">Enable Notifications</button>
    }
</div>

@code {
    private bool _isSupported;
    private bool _requiresInstall;
    private string _permission = "default";
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _isSupported = await PwaService.IsNotificationSupportedAsync();
            
            var isIOS = await PwaService.IsIOSAsync();
            var isInstalled = await PwaService.IsInstalledAsync();
            
            // iOS requires PWA to be installed for push
            _requiresInstall = isIOS && !isInstalled;
            
            if (_isSupported && !_requiresInstall)
            {
                _permission = await PwaService.GetNotificationPermissionAsync();
            }
            
            StateHasChanged();
        }
    }
    
    private async Task EnableNotifications()
    {
        _permission = await PwaService.RequestNotificationPermissionAsync();
        
        if (_permission == "granted")
        {
            // Subscribe to push
            var publicKey = PushService.GetPublicKey();
            var subscription = await PwaService.SubscribeToPushAsync(publicKey);
            
            if (subscription != null)
            {
                // Save subscription to server
                // await SaveSubscriptionToServer(subscription);
            }
        }
        
        StateHasChanged();
    }
    
    private async Task Unsubscribe()
    {
        await PwaService.UnsubscribeFromPushAsync();
        _permission = "default";
        StateHasChanged();
    }
}
```

---

## Step 9: Integration with Chat Hub

When a message is received, update the badge and optionally send push:

```csharp
// In your ChatHub or message handling service
public class ChatService
{
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly PushNotificationService _pushService;
    private readonly IPushSubscriptionStore _subscriptionStore;
    
    public async Task SendMessageAsync(ChatMessage message, string recipientUserId)
    {
        // Save message to database...
        
        // Send via SignalR if user is connected
        await _hubContext.Clients.User(recipientUserId)
            .SendAsync("ReceiveMessage", message);
        
        // Send push notification if user is not active
        var isUserActive = await IsUserActiveAsync(recipientUserId);
        if (!isUserActive)
        {
            var unreadCount = await GetUnreadCountAsync(recipientUserId);
            
            await _pushService.SendToUserAsync(
                recipientUserId,
                title: $"Message from {message.SenderName}",
                body: message.Content.Length > 100 
                    ? message.Content[..97] + "..." 
                    : message.Content,
                unreadCount: unreadCount,
                url: $"/channel/{message.ChannelId}",
                subscriptionStore: _subscriptionStore);
        }
    }
}
```

---

## Step 10: API Endpoint for Push Subscription

```csharp
// PushController.cs or minimal API endpoints
app.MapPost("/api/push/subscribe", async (
    PushSubscriptionDto dto,
    IPushSubscriptionStore store,
    HttpContext context) =>
{
    var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrEmpty(userId))
        return Results.Unauthorized();
    
    var subscription = new PushSubscriptionInfo
    {
        Endpoint = dto.Endpoint,
        Keys = new PushSubscriptionKeys
        {
            P256dh = dto.Keys.P256dh,
            Auth = dto.Keys.Auth
        }
    };
    
    await store.SaveSubscriptionAsync(userId, subscription);
    return Results.Ok();
});

app.MapPost("/api/push/unsubscribe", async (
    UnsubscribeDto dto,
    IPushSubscriptionStore store) =>
{
    await store.RemoveSubscriptionAsync(dto.Endpoint);
    return Results.Ok();
});

app.MapGet("/api/push/vapid-public-key", (PushNotificationService pushService) =>
{
    return Results.Ok(new { publicKey = pushService.GetPublicKey() });
});

// DTOs
public record PushSubscriptionDto(string Endpoint, PushSubscriptionKeysDto Keys);
public record PushSubscriptionKeysDto(string P256dh, string Auth);
public record UnsubscribeDto(string Endpoint);
```

---

## NuGet Packages Required

```xml
<PackageReference Include="WebPush" Version="1.0.14" />
```

---

## Testing Checklist

### Desktop (Chrome/Edge)
- [ ] Manifest loads correctly (DevTools > Application > Manifest)
- [ ] Service worker registers (DevTools > Application > Service Workers)
- [ ] Install prompt appears
- [ ] App installs and opens in standalone window
- [ ] Push notifications work
- [ ] Badge updates on app icon

### Android (Chrome)
- [ ] Install banner appears
- [ ] App installs to home screen
- [ ] Push notifications work
- [ ] Badge shows as dot (Android auto-badges from notifications)

### iOS (Safari)
- [ ] Manual install instructions show
- [ ] App installs via Share > Add to Home Screen
- [ ] App opens in standalone mode (no Safari UI)
- [ ] Notification permission can be requested (after install)
- [ ] Push notifications work (iOS 16.4+)
- [ ] Badge shows unread count (iOS 16.4+, after notification permission)

---

## Common Issues & Solutions

### Badge not showing on iOS
1. Ensure iOS 16.4+
2. PWA must be installed to home screen
3. User must grant notification permission
4. Don't call `setAppBadge(0)` - use `clearAppBadge()` instead

### Push not working on iOS
1. PWA must be installed first (Share > Add to Home Screen)
2. Must be iOS 16.4+
3. Permission must be requested via user interaction (button click)
4. Check that manifest has `"display": "standalone"`

### Service worker not updating
```javascript
// Force update in browser console
navigator.serviceWorker.getRegistration().then(reg => reg.update());
```

### Install prompt not showing (Desktop)
- Must be served over HTTPS (or localhost)
- manifest.json must be valid
- Must have service worker registered
- Some criteria vary by browser

---

## iOS-Specific CSS Considerations

```css
/* Safe area insets for notched iPhones */
.app-container {
    padding-top: env(safe-area-inset-top);
    padding-bottom: env(safe-area-inset-bottom);
    padding-left: env(safe-area-inset-left);
    padding-right: env(safe-area-inset-right);
}

/* Prevent iOS bounce/overscroll */
html, body {
    overscroll-behavior: none;
    -webkit-overflow-scrolling: touch;
}

/* Prevent text selection on long press */
.no-select {
    -webkit-user-select: none;
    user-select: none;
}

/* Disable iOS tap highlight */
* {
    -webkit-tap-highlight-color: transparent;
}
```

---

## Summary

This implementation gives you:

1. **Installable PWA** on all platforms
2. **Push notifications** that work even when app is closed
3. **Badge API** showing unread count on app icon
4. **iOS support** with appropriate fallbacks and user guidance
5. **Integration points** for your Blazor Server chat app

The main limitation remains that Blazor Server requires connectivity, so offline mode isn't possible. But for a chat app, that's expected anyway - you can't chat offline!
