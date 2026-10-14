// Shared by seed.mjs and capture.mjs: arguments, the Playwright module, the two
// device profiles, and sign-in. See README.md in this folder for how to run.

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
    if (!dbPath) throw new Error('Pass --db <path to the app Data/yap.db> (or set YAP_DB).');
    // node:sqlite prints an ExperimentalWarning on first import; it is noise here.
    const emit = process.emitWarning;
    process.emitWarning = (w, ...rest) => (String(w).includes('SQLite') ? undefined : emit.call(process, w, ...rest));
    const { DatabaseSync } = await import('node:sqlite');
    process.emitWarning = emit;

    const db = new DatabaseSync(dbPath, { readOnly: true });
    try {
        const rows = db.prepare(`SELECT Username, Token FROM Users WHERE Username IN (${usernames.map(() => '?').join(',')})`).all(...usernames);
        return Object.fromEntries(rows.map(r => [r.Username, r.Token]));
    } finally {
        db.close();
    }
}

export async function signInWithToken(page, origin, token, path) {
    await page.goto(`${origin}/auth/refresh-token?token=${encodeURIComponent(token)}&returnUrl=${encodeURIComponent(path)}`);
    if (new URL(page.url()).pathname !== path) throw new Error(`Sign-in ended at ${page.url()}, expected ${path}`);
}

// A conversation page is ready when the composer is there, the pickers are mounted
// (the old page mounts them about 1.2 s after load) and the visible images have loaded.
export async function waitForConversation(page) {
    await page.locator('textarea.message-input').waitFor();
    await page.locator('.emoji-picker-container[data-picker-pane="emoji"]').waitFor({ state: 'attached' });
    await waitForVisibleImages(page);
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
