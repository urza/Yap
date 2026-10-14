// Captures the named screenshot states of the spec pages into docs/spec/img/.
// Run seed.mjs once first. See README.md in this folder.
//
//   node capture.mjs --origin http://127.0.0.1:7651 --db <app>/Data/yap.db [state ...]
//
// A state is <page>-<device>-<name>, which is also its file name. With no names,
// every state is captured.

import { fileURLToPath } from 'node:url';
import { chromium, DEVICES, parseArgs, readTokens, signInWithToken, waitForConversation, waitForVisibleImages } from './common.mjs';

const OUT_DIR = fileURLToPath(new URL('../../img/', import.meta.url));

// The spec shows the light theme. The page's own applyTheme (what Settings calls)
// switches it, so the seeded accounts keep whatever theme they have.
const THEME = 'light';

// How much of the conversation column a desktop input box shot shows, from the
// bottom of the window: the box plus the last two or three messages above it.
const DESKTOP_COMPOSER_HEIGHT = 260;

const STATES = [
    {
        page: 'input-box', device: 'desktop', name: 'empty',
        // Room open, empty draft: placeholder, GIF badge, grey emoji icon, Send hidden.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await expectWidth(page, '.send-button', w => w === 0, 'Send hidden');
            return composerClip(page);
        },
    },
    {
        page: 'input-box', device: 'desktop', name: 'text',
        // Draft "hello": Send shown, the GIF toggle collapsed. (Send's glow is the
        // theme's --glow-accent; the light theme of this page sets it to none.)
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('textarea.message-input').click();
            await page.keyboard.type('hello');
            await expectWidth(page, '.send-button', w => w > 0, 'Send shown');
            await expectWidth(page, '.gif-toggle-button', w => w === 0, 'GIF toggle collapsed');
            return composerClip(page);
        },
    },
    {
        page: 'input-box', device: 'phone', name: 'sheet',
        // Emoji sheet open from the toggle over an empty draft. Whole screen.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('.emoji-toggle-button').tap();
            await page.locator('.message-input-container[data-picker="emoji"]').waitFor();
            await page.locator('.emoji-picker-container[data-picker-pane="emoji"]').waitFor({ state: 'visible' });
            await waitForVisibleImages(page);
            return null;
        },
    },
];

const { origin, db, names } = parseArgs(process.argv.slice(2));
const fileName = s => `${s.page}-${s.device}-${s.name}`;
const unknown = names.filter(n => !STATES.some(s => fileName(s) === n));
if (unknown.length) {
    throw new Error(`Unknown state(s): ${unknown.join(', ')}. Known: ${STATES.map(fileName).join(', ')}`);
}
const selected = names.length ? STATES.filter(s => names.includes(fileName(s))) : STATES;

const tokens = await readTokens(db, ['alice', 'bob']);
if (!tokens.alice || !tokens.bob) throw new Error('alice or bob is missing: run seed.mjs first.');

const browser = await chromium.launch();
try {
    for (const state of selected) {
        // A fresh context per state: no draft, picker or hover left over from the last one.
        const context = await browser.newContext(DEVICES[state.device]);
        const page = await context.newPage();
        page.signIn = (user, path) => signInWithToken(page, origin, tokens[user], path);
        const clip = await state.drive(page);
        const path = `${OUT_DIR}${fileName(state)}.png`;
        // animations: 'disabled' runs transitions to their end, so Send is fully
        // shown or fully collapsed rather than caught halfway.
        await page.screenshot({ path, clip: clip ?? undefined, animations: 'disabled' });
        console.log(`${fileName(state)}.png`);
        await context.close();
    }
} finally {
    await browser.close();
}

async function openConversation(page, user, path) {
    await page.signIn(user, path);
    await waitForConversation(page);
    await page.evaluate(theme => window.applyTheme(theme), THEME);
    // The page scrolls to the newest message on load; images above can still shift it.
    await page.locator('.messages').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await waitForVisibleImages(page);
}

// The conversation column (not the sidebar), about DESKTOP_COMPOSER_HEIGHT pixels
// from the bottom. The top edge moves up into the gap above the first message row
// that the target line would cut, so no row is cut in half.
async function composerClip(page) {
    const column = await page.locator('.messages').boundingBox();
    const bottom = page.viewportSize().height;
    const target = bottom - DESKTOP_COMPOSER_HEIGHT;
    const rowTop = await page.locator('.messages-flow > .message-group').evaluateAll(
        (rows, line) => rows.map(r => r.getBoundingClientRect()).find(r => r.bottom > line)?.top ?? line, target);
    const y = Math.floor(Math.min(target, rowTop - 8));
    return { x: column.x, y, width: column.width, height: bottom - y };
}

// Fails the capture when the page is not in the state the image claims to show.
async function expectWidth(page, selector, ok, what) {
    await page.waitForTimeout(300); // the 0.15 s collapse transitions
    const width = await page.locator(selector).evaluate(el => el.getBoundingClientRect().width);
    if (!ok(width)) throw new Error(`Expected ${what}, but ${selector} is ${width}px wide.`);
}
