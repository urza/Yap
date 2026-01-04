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

    // Update badge count (this is the key for iOS!)
    if ('setAppBadge' in self.navigator && data.unreadCount > 0) {
        promises.push(
            self.navigator.setAppBadge(data.unreadCount)
                .catch(err => console.error('[SW] Badge error:', err))
        );
    }

    // Show notification
    const notificationOptions = {
        body: data.body,
        icon: data.icon,
        badge: data.badge,
        tag: data.tag,
        renotify: true,
        requireInteraction: false,
        data: {
            url: data.url
        }
    };

    promises.push(
        self.registration.showNotification(data.title, notificationOptions)
    );

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
