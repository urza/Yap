// Service Worker for Yap PWA
// Handles: push notifications, badge updates, notification clicks
// Blazor Server requires live connection, so minimal caching

const CACHE_NAME = 'yap-v2';

// Install: cache essential assets and activate immediately
self.addEventListener('install', (event) => {
    console.log('[SW] Installing service worker');
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => {
            return cache.addAll([
                '/icon.svg',
                '/icon-192.png',
                '/icon-512.png',
                '/notif.mp3'
            ]).catch(() => {
                // Ignore cache failures for missing files
            });
        })
    );
    self.skipWaiting();
});

// Activate: clean up old caches and take control
self.addEventListener('activate', (event) => {
    console.log('[SW] Activating service worker');
    event.waitUntil(
        caches.keys().then((cacheNames) => {
            return Promise.all(
                cacheNames
                    .filter((name) => name !== CACHE_NAME)
                    .map((name) => caches.delete(name))
            );
        }).then(() => self.clients.claim())
    );
});

// Fetch: network-first (Blazor Server needs live connection)
self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);
    if (event.request.method !== 'GET') return;
    if (url.pathname.includes('_blazor')) return;

    const staticAssets = ['/icon.svg', '/icon-192.png', '/icon-512.png', '/notif.mp3'];
    const isStaticAsset = staticAssets.some(asset => url.pathname.endsWith(asset));

    if (isStaticAsset) {
        event.respondWith(
            caches.match(event.request).then((cached) => {
                if (cached) return cached;
                return fetch(event.request).then((response) => {
                    if (response.ok) {
                        const clone = response.clone();
                        caches.open(CACHE_NAME).then((cache) => cache.put(event.request, clone));
                    }
                    return response;
                });
            })
        );
    }
});

// ==========================================
// Push Notification Handler
// ==========================================
self.addEventListener('push', (event) => {
    console.log('[SW] Push received:', event);

    let data = {
        title: 'New Message',
        body: 'You have a new message',
        icon: '/icon-192.png',
        badge: '/icon-192.png',
        tag: 'chat-message',
        url: '/',
        unreadCount: 0
    };

    // Parse push payload
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

    // Update badge count (always, even when muted)
    if ('setAppBadge' in self.navigator && data.unreadCount > 0) {
        promises.push(
            self.navigator.setAppBadge(data.unreadCount)
                .catch(err => console.error('[SW] Badge error:', err))
        );
    }

    // Show notification banner (skip if muted)
    if (data.muted) {
        console.log('[SW] Muted — badge only, no banner');
    } else {
        promises.push(
            self.registration.showNotification(data.title, {
                body: data.body,
                icon: data.icon,
                badge: data.badge,
                tag: data.tag,
                renotify: true,
                requireInteraction: false,
                data: { url: data.url }
            })
        );
    }

    event.waitUntil(Promise.all(promises));
});

// ==========================================
// Notification Click Handler
// ==========================================
self.addEventListener('notificationclick', (event) => {
    console.log('[SW] Notification clicked:', event);
    event.notification.close();

    const urlToOpen = event.notification.data?.url || '/';

    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then((windowClients) => {
                // Check if app is already open
                for (const client of windowClients) {
                    if (client.url.includes(self.location.origin) && 'focus' in client) {
                        // Navigate existing window
                        client.postMessage({
                            type: 'NOTIFICATION_CLICK',
                            url: urlToOpen
                        });
                        return client.focus();
                    }
                }
                // Open new window
                return clients.openWindow(urlToOpen);
            })
    );
});

// ==========================================
// Message Handler (from main app)
// ==========================================
self.addEventListener('message', (event) => {
    console.log('[SW] Message received:', event.data);

    if (event.data?.type === 'SKIP_WAITING') {
        self.skipWaiting();
    }

    if (event.data?.type === 'SET_BADGE') {
        const count = event.data.count;
        if ('setAppBadge' in self.navigator) {
            if (count > 0) {
                self.navigator.setAppBadge(count);
            } else {
                self.navigator.clearAppBadge();
            }
        }
    }

    if (event.data?.type === 'CLEAR_BADGE') {
        if ('clearAppBadge' in self.navigator) {
            self.navigator.clearAppBadge();
        }
    }
});

// ==========================================
// Subscription Change Handler
// ==========================================
// Browsers rotate/expire push subscriptions (common on iOS, and after the server prunes a 410/404).
// When that happens the browser fires `pushsubscriptionchange`; we re-subscribe and re-register with
// the server so notifications keep working WITHOUT the user re-granting permission. Best-effort —
// support is limited on iOS Safari but present on Chromium/Android.
self.addEventListener('pushsubscriptionchange', (event) => {
    console.log('[SW] pushsubscriptionchange — re-subscribing');
    event.waitUntil(resubscribeToPush());
});

async function resubscribeToPush() {
    try {
        // Service workers can't use Blazor services, so fetch the VAPID key over HTTP.
        const keyResp = await fetch('/api/push/vapid-public-key', { credentials: 'include' });
        if (!keyResp.ok) {
            console.warn('[SW] resubscribe: VAPID key unavailable', keyResp.status);
            return;
        }
        const { publicKey } = await keyResp.json();
        if (!publicKey) return;

        const subscription = await self.registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: urlBase64ToUint8Array(publicKey)
        });

        const sub = subscription.toJSON();
        // /api/push/subscribe authenticates via the auth cookie and reads the username from it.
        const resp = await fetch('/api/push/subscribe', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                endpoint: sub.endpoint,
                p256dh: sub.keys?.p256dh,
                auth: sub.keys?.auth
            })
        });
        console.log('[SW] resubscribe: server responded', resp.status);
    } catch (e) {
        console.error('[SW] resubscribe failed:', e);
    }
}

// Helper: Convert a base64url VAPID key to Uint8Array (mirrors urlBase64ToUint8Array in chat.js).
function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const rawData = atob(base64);
    const outputArray = new Uint8Array(rawData.length);
    for (let i = 0; i < rawData.length; ++i) {
        outputArray[i] = rawData.charCodeAt(i);
    }
    return outputArray;
}
