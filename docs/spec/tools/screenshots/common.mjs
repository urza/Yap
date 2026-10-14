// Shared by seed.mjs and capture.mjs: arguments, the Playwright module, the two
// device profiles, the database lookups, sign-in, which app is the target, and waits.
// See README.md in this folder for how to run.

import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);

// Same convention as tests/browser: PLAYWRIGHT_MODULE selects an existing install;
// the default resolves from this folder upward, which finds the repository's
// pinned node_modules/playwright.
export const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

// Fixed locale and time zone, so message times and number formats do not change
// with the machine that captures.
const COMMON = { locale: 'en-US', timezoneId: 'Europe/Prague', colorScheme: 'light' };

export const DEVICES = {
    desktop: { ...COMMON, viewport: { width: 1280, height: 800 } },
    phone: { ...COMMON, viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
};

// --origin and --db take a value; everything else that is not a flag is a name.
export function parseArgs(argv) {
    const args = { origin: process.env.YAP_ORIGIN || 'http://127.0.0.1:7651', db: process.env.YAP_DB, names: [] };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--origin') args.origin = argv[++i];
        else if (a === '--db') args.db = argv[++i];
        else if (a.startsWith('--')) throw new Error(`Unknown option ${a}`);
        else args.names.push(a);
    }
    args.origin = args.origin.replace(/\/+$/, '');
    return args;
}

// Login tokens come straight from the app's database: the refresh-token URL then
// sets the auth cookie without creating anything. node:sqlite needs no install.
export async function readTokens(dbPath, usernames) {
    const rows = await queryDb(dbPath, `SELECT Username, Token FROM Users WHERE Username IN (${usernames.map(() => '?').join(',')})`, usernames);
    return Object.fromEntries(rows.map(r => [r.Username, r.Token]));
}

// Read-only: the scripts only look things up (tokens, room ids). All writes go through the page.
export async function queryDb(dbPath, sql, params = []) {
    if (!dbPath) throw new Error('Pass --db <path to the app Data/yap.db> (or set YAP_DB).');
    // node:sqlite prints an ExperimentalWarning on first import; it is noise here.
    const emit = process.emitWarning;
    process.emitWarning = (w, ...rest) => (String(w).includes('SQLite') ? undefined : emit.call(process, w, ...rest));
    const { DatabaseSync } = await import('node:sqlite');
    process.emitWarning = emit;

    const db = new DatabaseSync(dbPath, { readOnly: true });
    try {
        return db.prepare(sql).all(...params);
    } finally {
        db.close();
    }
}

export async function signInWithToken(page, origin, token, path) {
    await page.goto(`${origin}/auth/refresh-token?token=${encodeURIComponent(token)}&returnUrl=${encodeURIComponent(path)}`);
    if (new URL(page.url()).pathname !== path) throw new Error(`Sign-in ended at ${page.url()}, expected ${path}`);
}

// Rows the server accepted. The old page keeps pending copies in .pending-echoes;
// the current client puts its rows in #timeline and marks pending ones .pending-message.
export const ROWS = '.messages-flow > .message-group, #timeline > .message-group:not(.pending-message)';

// Two apps can be the target: the old page (commit 0cb67422, the visual target of most
// images) and the current client (wwwroot/chat-client), which has the New messages pill
// and offline pending rows. Only the current client serves /chat-client/.
export async function detectApp(origin) {
    const res = await fetch(`${origin}/chat-client/app.js`, { method: 'HEAD' });
    return res.ok ? 'main' : 'old';
}

// A conversation page is ready when the composer is there and the visible images have
// loaded. The old page also mounts the pickers about 1.2 s after load (the picker
// states need them); the current client builds them on first open, so there it waits
// for the first sync instead.
export async function waitForConversation(page) {
    await page.locator('textarea.message-input').waitFor();
    if (await page.locator('#timeline').count()) {
        await page.locator('html[data-connection="synced"]').waitFor({ state: 'attached' });
    } else {
        await page.locator('.emoji-picker-container[data-picker-pane="emoji"]').waitFor({ state: 'attached' });
    }
    await waitForVisibleImages(page);
}

// The old page switches themes with its own applyTheme (what Settings calls); the
// current client with yapAppearance.apply (what its Settings calls). Either way the
// account keeps its theme. The current client applies the account's theme again with
// every server update that carries it, so a state there switches again after one.
export async function applyTheme(page, theme) {
    await page.evaluate(t => {
        if (window.applyTheme) window.applyTheme(t);
        else if (window.yapAppearance) window.yapAppearance.apply({ ...window.yapAppearance.current, theme: t });
        else document.documentElement.dataset.theme = t;
    }, theme);
}

// Only images on screen: lazy images outside the viewport, or inside a closed
// picker, never load and would hold the wait forever.
export async function waitForVisibleImages(page) {
    await page.waitForFunction(() => [...document.images]
        .filter(img => {
            const r = img.getBoundingClientRect();
            return img.checkVisibility() && r.bottom > 0 && r.top < innerHeight;
        })
        .every(img => img.complete));
    await page.evaluate(() => document.fonts.ready);
}
