// The service worker: the shell for offline reads, the emoji and media caches, push and notification taps
// (offline.md, offline-design-notes.md). It never serves the API from a cache and never reloads a page.
//
// Registered as service-worker.js?v={build}: a new build is a new worker, which precaches the new shell
// into its own cache and deletes the old ones at activate. The shell and its scripts are network-first
// with the cache as the fallback, so a page online always runs the build the server has, and a page
// offline runs the last build it saw (OF-06). Emoji images and content-addressed media are cache-first:
// their names never change meaning.

const BUILD = new URL(self.location.href).searchParams.get('v') || 'dev';
const SHELL_CACHE = `yap-shell-${BUILD}`;
const EMOJI_CACHE = 'yap-emoji-v1';
const MEDIA_CACHE = 'yap-media-v1';

// What the shell needs to paint without a network. The page itself is stored under /login: every client
// route answers the same document, and /login is the one that never redirects.
const SHELL_URLS = [
    '/login',
    '/app.css', '/themes.css', '/themes/teahouse.css',
    '/css/frame.css', '/css/pickers.css', '/css/timeline.css', '/css/composer.css', '/css/message.css', '/css/pages.css',
    '/lib/signalr.min.js', '/lib/tus.min.js',
    '/js/main.js', '/js/store.js', '/js/api.js', '/js/hub.js', '/js/router.js', '/js/session.js', '/js/sender.js', '/js/upload.js', '/js/notify.js',
    '/js/render/dom.js', '/js/render/text.js', '/js/render/time.js', '/js/render/avatar.js',
    '/js/ui/frame.js', '/js/ui/timeline.js', '/js/ui/message.js', '/js/ui/composer.js', '/js/ui/overlays.js',
    '/js/ui/emoji-picker.js', '/js/ui/gif-picker.js', '/js/ui/gallery.js', '/js/ui/tooltip.js', '/js/ui/confirm.js', '/js/ui/voice.js', '/js/ui/recorder.js',
    '/js/pages/chat.js', '/js/pages/login.js', '/js/pages/verify.js', '/js/pages/invite.js', '/js/pages/people.js', '/js/pages/settings.js', '/js/pages/admin.js',
    '/js/push.js', '/js/pwa.js', '/js/ui/gif-library.js',
    '/js/offline/db.js', '/js/offline/snapshot.js', '/js/offline/outbox.js',
    '/fonts/InterVariable.woff2', '/icon.svg', '/icon-192.png', '/icon-512.png', '/notif.mp3',
    '/images/turqline01_3px.png', '/images/purpleline01_3px.png', '/emoji_selection_color.png', '/emoji_selection_greys.png',
];
const EMOJI_HOSTS = ['cdn.jsdelivr.net'];
const EMOJI_PATHS = ['/emoji-fallback/', '/emoji-packs/', '/custom-emojis/'];
const MEDIA_PATHS = ['/uploads/', '/gif-cache/', '/media-cache/'];

self.addEventListener('install', event => {
    event.waitUntil(caches.open(SHELL_CACHE).then(cache =>
        // One missing file must not stop the install: add what the server has.
        Promise.all(SHELL_URLS.map(url => cache.add(url).catch(() => {})))));
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(caches.keys()
        .then(names => Promise.all(names.filter(n => ![SHELL_CACHE, EMOJI_CACHE, MEDIA_CACHE].includes(n)).map(n => caches.delete(n))))
        .then(() => self.clients.claim()));
});

self.addEventListener('fetch', event => {
    const { request } = event;
    if (request.method !== 'GET') return;
    const url = new URL(request.url);
    const sameOrigin = url.origin === self.location.origin;

    if (sameOrigin && (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hub'))) return;   // live data, never cached

    // A navigation: the shell. Online the server's current build, offline the stored one (OF-06).
    if (request.mode === 'navigate' && sameOrigin) {
        event.respondWith(networkFirst(request, SHELL_CACHE, '/login'));
        return;
    }

    if (sameOrigin && isShellAsset(url.pathname)) {
        event.respondWith(networkFirst(request, SHELL_CACHE));
        return;
    }

    if (EMOJI_HOSTS.includes(url.host) || (sameOrigin && EMOJI_PATHS.some(p => url.pathname.startsWith(p)))) {
        event.respondWith(cacheFirst(request, EMOJI_CACHE));
        return;
    }

    // Content-addressed media. Profile pictures keep a stable URL and are left to the HTTP cache; range
    // requests cannot be served from the Cache API.
    if (sameOrigin && MEDIA_PATHS.some(p => url.pathname.startsWith(p)) && !url.pathname.startsWith('/uploads/profiles/') && !request.headers.has('range')) {
        event.respondWith(cacheFirst(request, MEDIA_CACHE));
    }
});

function isShellAsset(pathname) {
    return /\.(js|css|woff2|svg|png|mp3)$/.test(pathname) && !MEDIA_PATHS.some(p => pathname.startsWith(p)) && !EMOJI_PATHS.some(p => pathname.startsWith(p));
}

// The network, and a copy into the cache for later; the cache when the network fails. For a navigation
// the fallback is the stored shell whatever the path was.
async function networkFirst(request, cacheName, fallbackKey = null) {
    const cache = await caches.open(cacheName);
    try {
        const response = await fetch(request);
        if (response.status === 200) cache.put(fallbackKey ?? request, response.clone());   // not a 206: a range answer (audio seeks) cannot be cached
        return response;
    } catch {
        return (await cache.match(fallbackKey ?? request)) || Response.error();
    }
}

async function cacheFirst(request, cacheName) {
    const cache = await caches.open(cacheName);
    const cached = await cache.match(request);
    // An opaque entry (a script tag's no-cors fetch) is no answer for a CORS request such as an emoji <img
    // crossorigin>: the browser would show a broken picture. Such a pair fetches again and the real answer wins.
    if (cached && !(cached.type === 'opaque' && request.mode === 'cors')) return cached;
    try {
        const response = await fetch(request);
        if (response.status === 200 || response.type === 'opaque') cache.put(request, response.clone());   // a 206 range answer cannot be cached
        return response;
    } catch {
        return Response.error();
    }
}

// ---- push (NT-23 to NT-29) ----

self.addEventListener('push', event => {
    let data = { title: 'New Message', body: 'You have a new message', icon: '/icon-192.png', badge: '/icon-192.png', tag: 'chat-message', url: '/', unreadCount: 0 };
    if (event.data) {
        try { data = { ...data, ...event.data.json() }; }
        catch { data.body = event.data.text(); }
    }
    const work = [];
    // The badge: the server already left out the muted conversations (NT-11).
    if ('setAppBadge' in self.navigator && data.unreadCount > 0) work.push(self.navigator.setAppBadge(data.unreadCount).catch(() => {}));
    // A new notification of a conversation replaces the previous one and alerts again (NT-27).
    work.push(self.registration.showNotification(data.title, {
        body: data.body, icon: data.icon, badge: data.badge, tag: data.tag, renotify: true, requireInteraction: false, data: { url: data.url },
    }));
    // A delivery receipt, best effort: Settings shows the last confirmed delivery per device.
    work.push(self.registration.pushManager.getSubscription()
        .then(sub => sub && fetch('/api/push/delivered', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ endpoint: sub.endpoint, tag: data.tag, shown: true }) }))
        .catch(() => {}));
    event.waitUntil(Promise.all(work));
});

// A tap opens the conversation in an open window, or in a new one (NT-28).
self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url || '/';
    event.waitUntil(clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windows => {
        for (const client of windows) {
            if (client.url.startsWith(self.location.origin) && 'focus' in client) {
                client.postMessage({ type: 'NOTIFICATION_CLICK', url });
                return client.focus();
            }
        }
        return clients.openWindow(url);
    }));
});

self.addEventListener('message', event => {
    if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});

// Browsers rotate push subscriptions (iOS often, and after the server prunes a dead one). The worker
// subscribes again and tells the server, so notifications keep working without a new permission prompt.
self.addEventListener('pushsubscriptionchange', event => { event.waitUntil(resubscribe()); });

async function resubscribe() {
    try {
        const keyResponse = await fetch('/api/push/vapid-public-key', { credentials: 'include' });
        if (!keyResponse.ok) return;
        const { publicKey } = await keyResponse.json();
        if (!publicKey) return;
        const subscription = await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(publicKey) });
        const sub = subscription.toJSON();
        await fetch('/api/push/subscribe', {   // the cookie names the account
            method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ endpoint: sub.endpoint, p256dh: sub.keys?.p256dh, auth: sub.keys?.auth }),
        });
    } catch { /* best effort */ }
}

function base64UrlToBytes(value) {
    const padded = value + '='.repeat((4 - value.length % 4) % 4);
    const raw = atob(padded.replace(/-/g, '+').replace(/_/g, '/'));
    return Uint8Array.from(raw, c => c.charCodeAt(0));
}
