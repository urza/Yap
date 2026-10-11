const watched = new WeakSet();

export function watchWorkerUpdates(registration) {
    if (!registration || watched.has(registration)) return;
    watched.add(registration);
    const watch = (worker) => {
        if (!worker) return;
        const activate = () => {
            // Activate only after installation has cached the complete shell.
            // Also cover a worker that was already waiting before this document opened.
            if (worker.state === 'installed') worker.postMessage({ type: 'SKIP_WAITING' });
        };
        worker.addEventListener('statechange', activate);
        activate();
    };
    registration.addEventListener('updatefound', () => watch(registration.installing));
    watch(registration.installing || registration.waiting);
}

// The installed shell can start without a navigation request. Registration/update checks
// activate a replacement in the background while durable local work stays intact.
if ('serviceWorker' in navigator)
    navigator.serviceWorker
        .getRegistration()
        .then(watchWorkerUpdates)
        .catch(() => {});

// No API reports module service worker support, so app.js classifies a failed
// registration by error name. Chromium 75-90 read `type` and reject 'module' with
// NotSupportedError. Firefox before 147 and older Chromium do not know the
// registration `type`: they load the module as a classic script, fail on `import`
// and reject with a TypeError, the same error as a failed download. Safari 14 reads
// the registration `type` and fails the same way, but it does not read
// WorkerOptions.type, which arrived with module workers in Safari 15.
// A getter records whether each dictionary reads `type` during argument conversion.
// The unparsable URL then fails before any fetch, so neither call starts a worker or
// a registration job (the usual `new Worker('data:,')` probe would start one).
function readsType(construct) {
    let read = false;
    try {
        construct('http://[', {
            get type() {
                read = true;
                return 'module';
            },
        });
    } catch {}
    return read;
}
let moduleWorkers;
function supportsModuleWorkers() {
    return (moduleWorkers ??=
        readsType((url, options) =>
            navigator.serviceWorker.register(url, options).catch(() => {}),
        ) && readsType((url, options) => new Worker(url, options)));
}

// A content-only release changes the registration URL even when worker source is identical.
// Registration performs the browser's normal atomic install/activate lifecycle.
let checking;
export function registerWorker() {
    return (checking ??= (async () => {
        const existing = await navigator.serviceWorker.getRegistration('/');
        await existing?.update().catch(() => {});
        // app.js shows the update-your-browser text for this name, not the download text.
        if (!supportsModuleWorkers())
            throw new DOMException('Module service workers are unavailable.', 'NotSupportedError');
        try {
            const response = await fetch('/chat-client/manifest.json', {
                cache: 'no-store',
                signal: AbortSignal.timeout(10000),
            });
            if (!response.ok) throw new Error('Shell manifest unavailable');
            const manifest = await response.json();
            const registration = await navigator.serviceWorker.register(
                '/service-worker-module.js?v=' + encodeURIComponent(manifest.version),
                {
                    type: 'module',
                    updateViaCache: 'none',
                    scope: '/',
                },
            );
            watchWorkerUpdates(registration);
            return registration;
        } catch (error) {
            if (!existing?.active?.scriptURL.includes('/service-worker-module.js')) throw error;
            watchWorkerUpdates(existing);
            return existing;
        }
    })().finally(() => {
        checking = undefined;
    }));
}
if ('serviceWorker' in navigator) {
    registerWorker().catch(() => {});
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) registerWorker().catch(() => {});
    });
}
