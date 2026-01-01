// Minimal service worker for PWA installability
// Blazor Server requires live connection, so we don't cache aggressively

const CACHE_NAME = 'yap-v1';

// Install: just activate immediately
self.addEventListener('install', (event) => {
    console.log('[SW] Installing service worker');
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

// Fetch: network-first strategy (Blazor Server needs live connection)
// Only cache static assets like icons and sounds
self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Skip non-GET requests
    if (event.request.method !== 'GET') return;

    // Skip Blazor SignalR connections
    if (url.pathname.includes('_blazor')) return;

    // Cache static assets only
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
    // All other requests go directly to network (no caching)
});

// Handle badge updates from the main thread
self.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'SET_BADGE') {
        const count = event.data.count;
        if (navigator.setAppBadge) {
            if (count > 0) {
                navigator.setAppBadge(count);
            } else {
                navigator.clearAppBadge();
            }
        }
    }
});
