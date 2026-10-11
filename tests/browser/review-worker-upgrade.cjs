const { poll } = require('./support/wait.cjs');
// Serve previous/candidate static packages on one disposable origin; APIs use the isolated fixture.
const { chromium, firefox } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs'),
    path = require('node:path'),
    http = require('node:http');
const assert = require('node:assert/strict');
const oldPackage = process.env.YAP_PREVIOUS_PACKAGE;
const newPackage = process.env.YAP_REWRITE_PACKAGE;
if (!oldPackage || !newPackage)
    throw new Error(
        'Set YAP_PREVIOUS_PACKAGE and YAP_REWRITE_PACKAGE to compatible chat releases.',
    );
const { manifest } = require('./support/manifest.cjs');
const manifests = new Map(
    [oldPackage, newPackage].map((p) => [p, manifest(path.join(p, 'wwwroot'))]),
);
// Releases before the update notice cannot show it; upgrades from them stay testable.
const announcesUpdates = fs
    .readFileSync(path.join(oldPackage, 'wwwroot/chat-client/app.js'), 'utf8')
    .includes('Yap was updated. Reload when convenient.');
// The open tab keeps the incumbent's stylesheet, so the styled-control check needs it there.
const stylesNotice = fs
    .readFileSync(path.join(oldPackage, 'wwwroot/chat-client/chat.css'), 'utf8')
    .includes('#notice button');
let previousCache, candidateCache;
const backend = new URL(process.env.YAP_TEST_ORIGIN || 'http://127.0.0.1:7643');
if (backend.hostname !== '127.0.0.1' || backend.port === '7543')
    throw new Error('Isolated localhost fixture required');
const origin = 'http://127.0.0.1:7844',
    sockets = new Set();
let packagePath = oldPackage,
    holdWrites = false,
    browser;
const proxy = http.createServer((req, res) => {
    const pathname = new URL(req.url, origin).pathname;
    if (pathname === '/chat-client/manifest.json') {
        res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify(manifests.get(packagePath)));
        return;
    }
    if (holdWrites && req.method === 'POST' && pathname.endsWith('/messages')) {
        res.writeHead(503, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Held by upgrade fixture' }));
        return;
    }
    const asset = pathname === '/lobby' ? '/chat-client/index.html' : pathname;
    const root = path.join(packagePath, 'wwwroot');
    const file = path.resolve(root, '.' + asset);
    if (
        req.method === 'GET' &&
        file.startsWith(root + path.sep) &&
        fs.existsSync(file) &&
        fs.statSync(file).isFile()
    ) {
        const types = {
            '.js': 'text/javascript',
            '.html': 'text/html',
            '.css': 'text/css',
            '.json': 'application/json',
            '.webmanifest': 'application/manifest+json',
        };
        res.writeHead(200, {
            'Content-Type': types[path.extname(file)] || 'application/octet-stream',
            'Cache-Control': 'no-cache',
        });
        fs.createReadStream(file).pipe(res);
        return;
    }
    const upstream = http.request(
        {
            hostname: backend.hostname,
            port: backend.port,
            path: req.url,
            method: req.method,
            headers: req.headers,
        },
        (response) => {
            res.writeHead(response.statusCode, response.headers);
            response.pipe(res);
        },
    );
    res.on('close', () => upstream.destroy());
    upstream.on('error', () => {
        if (res.destroyed) return;
        res.writeHead(503);
        res.end();
    });
    req.pipe(upstream);
});
proxy.on('connection', (socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
});
proxy.on('upgrade', (req, socket, head) => {
    const upstream = http.request({
        hostname: backend.hostname,
        port: backend.port,
        path: req.url,
        method: req.method,
        headers: req.headers,
    });
    socket.on('close', () => upstream.destroy());
    upstream.on('upgrade', (response, back, extra) => {
        socket.write(
            `HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\n` +
                response.rawHeaders.reduce(
                    (text, value, index) => text + (index % 2 ? value + '\r\n' : value + ': '),
                    '',
                ) +
                '\r\n',
        );
        if (extra.length) socket.write(extra);
        if (head.length) back.write(head);
        socket.pipe(back);
        back.pipe(socket);
        socket.on('error', () => back.destroy());
        socket.on('close', () => back.destroy());
        back.on('error', () => socket.destroy());
        back.on('close', () => socket.destroy());
    });
    upstream.on('response', () =>
        socket.end('HTTP/1.1 503 Unavailable\r\nConnection: close\r\n\r\n'),
    );
    upstream.on('error', () => socket.destroy());
    upstream.end();
});

(async () => {
    try {
        const { SHELL_CACHE_PREFIX } = await import(
            path.resolve(__dirname, '../../Yap/wwwroot/chat-client/constants.js')
        );
        previousCache = SHELL_CACHE_PREFIX + manifests.get(oldPackage).version;
        candidateCache = SHELL_CACHE_PREFIX + manifests.get(newPackage).version;
        assert.notEqual(previousCache, candidateCache, 'An upgrade requires changed assets');
        for (const dir of [oldPackage, newPackage])
            assert(fs.existsSync(path.join(dir, 'wwwroot/chat-client/worker.js')));
        await new Promise((resolve, reject) => {
            proxy.once('error', reject);
            proxy.listen(7844, '127.0.0.1', resolve);
        });
        browser = await (process.env.YAP_BROWSER === 'firefox' ? firefox : chromium).launch();
        // Authenticate separately so this lifecycle fixture has exactly one registration.
        // A backend login page also installs a worker; it is not part of this upgrade.
        const login = await browser.newContext({ serviceWorkers: 'block' });
        const entry = await login.newPage();
        await entry.goto(backend.origin + '/login');
        await entry.locator('.username-input').fill('workerupgrade' + Date.now().toString(36));
        await entry.locator('.join-button').click();
        await entry.waitForURL('**/lobby');
        const context = await browser.newContext({ storageState: await login.storageState() });
        await login.close();
        const page = await context.newPage();
        await page.goto(origin + '/lobby');
        await page.waitForFunction(
            () =>
                document.querySelector('#connection')?.textContent === 'Synced · available offline',
        );
        // The first worker claims this page too; only a replaced shell is an update.
        assert(!(await page.locator('#notice').innerText()).includes('Yap was updated'));
        previousCache = (await page.evaluate(() => caches.keys())).find((name) =>
            name.startsWith(SHELL_CACHE_PREFIX),
        );
        assert(previousCache, 'Incumbent shell is installed');
        assert.notEqual(previousCache, candidateCache);
        await context.setOffline(true);
        const text = 'Queued across shell update ' + Date.now();
        await page.locator('#draft').fill(text);
        await page.locator('#send').click();
        await page.locator('#pending [data-operation]').waitFor();
        await page.locator('#draft').fill('Unsent draft survives shell update');
        await poll(page, async () => {
            const store = await import('/chat-client/storage.js'),
                state = await store.readState();
            return (
                (await store.draft(state.snapshot.conversations.find((c) => c.isDefault).id)) ===
                document.querySelector('#draft').value
            );
        });
        const id = await page.locator('#pending [data-operation]').getAttribute('data-operation');
        holdWrites = true;
        packagePath = newPackage;
        await context.setOffline(false);
        // Finish the reconnect before testing navigation-triggered registration.
        await page.waitForFunction(() =>
            document.querySelector('#connection')?.textContent.startsWith('Synced'),
        );
        if (process.env.YAP_PROTOCOL_UPGRADE === '1') {
            // Use a current-code incumbent with a distinct asset manifest to exercise 426
            // while it is already controlling an open editor, rather than navigation updates.
            await context.route('**/api/chat/protocol-fixture', (route) =>
                route.fulfill({
                    status: 426,
                    contentType: 'application/json',
                    body: JSON.stringify({
                        code: 'update_required',
                        error: 'Client update required.',
                    }),
                }),
            );
            const navigation = page.waitForEvent(
                'framenavigated',
                (frame) => frame === page.mainFrame(),
            );
            await page.evaluate(async () => {
                try {
                    await (await import('/chat-client/api.js')).get('protocol-fixture');
                } catch {}
            });
            await navigation;
            console.log(
                'PASS protocol 426 triggers worker update and reload on controller activation',
            );
        } else {
            await page.reload();
            await page.evaluate(() => (window.incumbentDocument = true));
            // Ordinary updates preserve the running document. Installation can complete
            // while the incumbent still controls it; there is no update prompt/reload.
            await poll(
                page,
                async (version) => {
                    const registration = await navigator.serviceWorker.getRegistration('/');
                    return [registration?.waiting, registration?.active].some(
                        (worker) =>
                            worker &&
                            ['installed', 'activated'].includes(worker.state) &&
                            new URL(worker.scriptURL).searchParams.get('v') === version,
                    );
                },
                manifests.get(newPackage).version,
            );
            if (announcesUpdates) {
                // The open tab still runs the incumbent modules once the candidate controls it.
                await page.waitForFunction(
                    (version) =>
                        navigator.serviceWorker.controller &&
                        new URL(navigator.serviceWorker.controller.scriptURL).searchParams.get(
                            'v',
                        ) === version,
                    manifests.get(newPackage).version,
                );
                await page
                    .locator('#notice:not([hidden])')
                    .filter({ hasText: 'Yap was updated. Reload when convenient.' })
                    .locator('button', { hasText: 'Reload' })
                    .waitFor();
                // The control carries the client's inline button style; a bare <button>
                // shows the browser's grey bevel (border-style outset) inside the strip.
                if (stylesNotice) {
                    const reloadStyle = await page
                        .locator('#notice button', { hasText: 'Reload' })
                        .evaluate((b) => {
                            const s = getComputedStyle(b);
                            return {
                                border: s.borderStyle,
                                radius: s.borderRadius,
                                font: s.fontFamily,
                            };
                        });
                    assert.notEqual(reloadStyle.border, 'outset', JSON.stringify(reloadStyle));
                    assert.notEqual(reloadStyle.radius, '0px', JSON.stringify(reloadStyle));
                }
                assert.equal(await page.evaluate(() => window.incumbentDocument), true);
                assert.equal(
                    await page.locator('#draft').inputValue(),
                    'Unsent draft survives shell update',
                );
                console.log(
                    'PASS open tab announces the adopted update without reloading or clearing the draft',
                );
            }
            // A normal departure releases the incumbent document/transport before reentry.
            await page.goto('about:blank');
            await page.goto(origin + '/lobby');
        }
        // Verify the real candidate controller, not just downloaded assets.
        await poll(
            page,
            async (version) => {
                const registration = await navigator.serviceWorker.getRegistration('/');
                const controller = navigator.serviceWorker.controller;
                return (
                    registration?.active?.state === 'activated' &&
                    controller?.scriptURL === registration.active.scriptURL &&
                    new URL(controller.scriptURL).searchParams.get('v') === version
                );
            },
            manifests.get(newPackage).version,
        );
        await poll(
            page,
            async ({ previousCache, candidateCache }) => {
                const names = await caches.keys();
                return names.includes(candidateCache) && !names.includes(previousCache);
            },
            { previousCache, candidateCache },
        );
        await page.goto(origin + '/lobby');
        await page.waitForFunction(
            () =>
                document.querySelector('#connection')?.textContent === 'Synced · available offline',
        );
        assert.equal(
            await page.locator('#draft').inputValue(),
            'Unsent draft survives shell update',
        );
        assert.equal(
            await page.locator('#pending [data-operation]').getAttribute('data-operation'),
            id,
        );
        holdWrites = false;
        await page.locator('#timeline .message-text').filter({ hasText: text }).waitFor();
        await poll(
            page,
            async () => (await (await import('/chat-client/storage.js')).outbox()).length === 0,
        );
        assert.equal(
            await page.locator('#timeline .message-text').filter({ hasText: text }).count(),
            1,
        );
        await context.setOffline(true);
        await page.reload();
        await page.locator('#timeline .message-text').filter({ hasText: text }).waitFor();
        await page.locator('#draft:not([disabled])').waitFor();
        assert.equal(
            await page.locator('#draft').inputValue(),
            'Unsent draft survives shell update',
        );
        console.log(
            `PASS actual ${previousCache} → ${candidateCache} activation, retained draft/outbox, exactly-once recovery and offline reload; ` +
                browser.version(),
        );
    } finally {
        // Close forwarded sockets before browser teardown; an offline page can leave
        // a forwarded stream otherwise, keeping the proxy (and the test) alive.
        for (const context of browser?.contexts() || []) await context.setOffline(false);
        const closed = new Promise((resolve) => proxy.close(resolve));
        for (const socket of sockets) socket.destroy();
        await browser?.close();
        await closed;
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
