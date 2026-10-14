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

// The space a row or picker shot keeps around its element, so it has a little context.
const MARGIN = 12;

// Rows the server accepted (pending copies live in .pending-echoes).
const ROWS = '.messages-flow > .message-group';

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
    {
        page: 'message-row', device: 'desktop', name: 'group',
        // A header row, two rows without header from the same author, then a header
        // row from another author. The seed puts such a group in the direct thread.
        async drive(page) {
            await openConversation(page, 'alice', '/dm/bob');
            const rows = page.locator(ROWS);
            const start = await rows.evaluateAll(els => {
                const header = el => el.classList.contains('has-header');
                const by = el => el.dataset.author;
                for (let i = els.length - 4; i >= 0; i--) {
                    const [a, b, c, d] = els.slice(i, i + 4);
                    if (header(a) && !header(b) && !header(c) && header(d)
                        && by(b) === by(a) && by(c) === by(a) && by(d) !== by(a)) return i;
                }
                return -1;
            });
            if (start < 0) throw new Error('No header row with two rows under it and another author after: run seed.mjs.');
            return rowsClip(page, rows.nth(start), rows.nth(start + 3));
        },
    },
    {
        page: 'message-row', device: 'desktop', name: 'hover-bar',
        // The pointer over one of alice's own text rows: the action bar at the top
        // right. The row above is in the shot, because the bar overlaps it.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const rows = page.locator(ROWS);
            const i = await rowIndex(page, 'alice', 'about 14 km, 5 hours with breaks');
            const clip = await rowsClip(page, rows.nth(i - 1), rows.nth(i));
            await rows.nth(i).hover();
            await page.waitForTimeout(300); // the 0.1 s fade-in
            const bar = await rows.nth(i).locator('.message-actions').evaluate(el => ({
                opacity: getComputedStyle(el).opacity,
                buttons: [...el.children].map(b => (b.classList.contains('action-divider') ? '|' : b.title)),
            }));
            const expected = ['|', 'Add Reaction', 'Reply', 'Edit', 'More'];
            if (bar.opacity !== '1' || bar.buttons.length !== 8 || bar.buttons.slice(3).join() !== expected.join()) {
                throw new Error(`Expected the bar with three quick reactions, ${expected.join(' ')}; got opacity ${bar.opacity}, ${bar.buttons.join(' ')}.`);
            }
            return clip;
        },
    },
    {
        page: 'message-row', device: 'desktop', name: 'reply-reactions',
        // A reply with at least two reaction pills: one is alice's own (highlighted),
        // one is not. The seed adds them to bob's reply in the lobby.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const rows = page.locator(ROWS);
            const i = await rows.evaluateAll(els => els.findLastIndex(r => r.classList.contains('has-reply')
                && r.querySelector('.reaction-pill.reacted') && r.querySelector('.reaction-pill:not(.reacted)')));
            if (i < 0) throw new Error('No reply with an own and another reaction pill: run seed.mjs.');
            return rowsClip(page, rows.nth(i), rows.nth(i));
        },
    },
    {
        page: 'message-row', device: 'phone', name: 'more-sheet',
        // A tap on one of alice's own rows shows its bar; More opens the bottom sheet
        // over the dimmed page. Whole screen.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const row = page.locator(ROWS).nth(await rowIndex(page, 'alice', 'noted, no cucumber'));
            const pills = await row.locator('.reaction-pill').count();
            // The tap goes to the left end of the text. In this emulation the bar can show
            // before the tap's click lands, and a tap under the bar would add a reaction.
            await row.locator('.message-content').tap({ position: { x: 4, y: 8 } });
            // A phone keeps :hover on the tapped row, and that shows the bar. Playwright's
            // tap leaves no hover, so the pointer moves there too.
            await row.hover();
            await row.locator('.action-more').tap();
            const menu = row.locator('.more-menu');
            await menu.waitFor({ state: 'visible' });
            const items = (await menu.locator('.more-menu-item').allInnerTexts()).map(t => t.trim());
            if (items.join() !== 'Copy Text,Delete Message') throw new Error(`Expected Copy Text and Delete Message, got ${items.join(', ')}.`);
            if (await row.locator('.reaction-pill').count() !== pills) throw new Error('The taps changed the row\'s reactions.');
            await page.waitForTimeout(300);
            return null;
        },
    },
    {
        page: 'pickers', device: 'desktop', name: 'emoji',
        // The emoji picker from the input box toggle: Recent highlighted in the
        // category bar, the search field, the Recent row (the seed fills it), the
        // first pack section. The input box under it is in the shot.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('.emoji-toggle-button').click();
            const picker = page.locator('.emoji-picker-container[data-picker-pane="emoji"] .emoji-picker');
            await picker.waitFor({ state: 'visible' });
            if (await picker.locator('.emoji-section[data-section="recent"] .emoji-btn').count() === 0) {
                throw new Error('Recent is empty: run seed.mjs.');
            }
            await expectClass(picker.locator('.category-btn[data-category="recent"]'), 'active', 'Recent highlighted');
            await waitForVisibleImages(page);
            return unionClip(page, [picker, page.locator('.message-input-container')]);
        },
    },
    {
        page: 'pickers', device: 'desktop', name: 'gif-browse',
        // The GIF picker's first open: Browse, because alice has a favourite (the
        // seed's upload). The Favorites tile; without a provider key there is no
        // Trending and no attribution. The input box under it is in the shot.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('.gif-toggle-button').click();
            const picker = page.locator('.emoji-picker-container[data-picker-pane="gif"] .gif-picker');
            await picker.waitFor({ state: 'visible' });
            await expectClass(picker.locator('.category-btn[title="Browse"]'), 'active', 'Browse open');
            await picker.locator('.gif-folder-tile', { hasText: 'Favorites' }).waitFor();
            await waitForVisibleImages(page);
            return unionClip(page, [picker, page.locator('.message-input-container')]);
        },
    },
    {
        page: 'pickers', device: 'phone', name: 'sheet-gifs',
        // The phone sheet on the GIFs tab with Favorites open: the tab row, the rail,
        // the search row and the seeded GIF's card. Whole screen.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('.emoji-toggle-button').tap();
            await page.locator('.message-input-container[data-picker="emoji"]').waitFor();
            await page.locator('.combined-tab[data-combined-tab="gifs"]').tap();
            const gifs = page.locator('.combined-pane[data-combined-pane="gifs"]');
            await gifs.waitFor({ state: 'visible' });
            await gifs.locator('.category-btn[title="Favorites"]').tap();
            await expectClass(gifs.locator('.category-btn[title="Favorites"]'), 'active', 'Favorites open');
            await gifs.locator('.gif-card img').first().waitFor({ state: 'visible' });
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
    // One session of alice stays open for the whole run. When the last session of a user
    // closes, the server sets them Away, and the next state's header would show it.
    const anchor = await (await browser.newContext(DEVICES.desktop)).newPage();
    await signInWithToken(anchor, origin, tokens.alice, '/dm/ping');
    await waitForConversation(anchor);

    for (const state of selected) {
        // A fresh context per state: no draft, picker or hover left over from the last one.
        const context = await browser.newContext(DEVICES[state.device]);
        const page = await context.newPage();
        page.signIn = (user, path) => signInWithToken(page, origin, tokens[user], path);
        const clip = await state.drive(page);
        // A whole screen shows the header's status pill: alice reads Online.
        if (!clip) await expectClass(page.locator('.status-button').first(), 'status-online', 'alice Online in the header');
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
    const rowTop = await page.locator(ROWS).evaluateAll(
        (rows, line) => rows.map(r => r.getBoundingClientRect()).find(r => r.bottom > line)?.top ?? line, target);
    const y = Math.floor(Math.min(target, rowTop - 8));
    return { x: column.x, y, width: column.width, height: bottom - y };
}

// The index in ROWS of the last row by `author` whose text contains `text`.
async function rowIndex(page, author, text) {
    const i = await page.locator(ROWS).evaluateAll((rows, [a, t]) => rows.findLastIndex(
        r => r.dataset.author === a && r.querySelector('.message-content')?.textContent.includes(t)), [author, text]);
    if (i < 0) throw new Error(`No row by ${author} with "${text}": run seed.mjs.`);
    return i;
}

// The conversation column from the top of the first row to the bottom of the last,
// plus MARGIN. The rows are scrolled to the middle of the list first.
async function rowsClip(page, first, last) {
    await first.evaluate(el => el.scrollIntoView({ block: 'center' }));
    await waitForVisibleImages(page);
    const column = await page.locator('.messages').boundingBox();
    const a = await first.boundingBox();
    const b = await last.boundingBox();
    const top = Math.floor(a.y - MARGIN);
    const bottom = Math.ceil(b.y + b.height + MARGIN);
    if (top < column.y || bottom > column.y + column.height) throw new Error('The rows do not fit in the conversation column.');
    return { x: column.x, y: top, width: column.width, height: bottom - top };
}

// The smallest rectangle around all the elements, plus MARGIN, inside the window.
async function unionClip(page, locators) {
    const boxes = await Promise.all(locators.map(l => l.boundingBox()));
    const { width, height } = page.viewportSize();
    const left = Math.max(0, Math.floor(Math.min(...boxes.map(b => b.x)) - MARGIN));
    const top = Math.max(0, Math.floor(Math.min(...boxes.map(b => b.y)) - MARGIN));
    const right = Math.min(width, Math.ceil(Math.max(...boxes.map(b => b.x + b.width)) + MARGIN));
    const bottom = Math.min(height, Math.ceil(Math.max(...boxes.map(b => b.y + b.height)) + MARGIN));
    return { x: left, y: top, width: right - left, height: bottom - top };
}

// Fails the capture when the page is not in the state the image claims to show.
async function expectWidth(page, selector, ok, what) {
    await page.waitForTimeout(300); // the 0.15 s collapse transitions
    const width = await page.locator(selector).evaluate(el => el.getBoundingClientRect().width);
    if (!ok(width)) throw new Error(`Expected ${what}, but ${selector} is ${width}px wide.`);
}

// Waits up to two seconds: a tab change sets its class after a round trip to the server.
async function expectClass(locator, cls, what) {
    let classes = [];
    for (let i = 0; i < 20 && !classes.includes(cls); i++) {
        if (i) await locator.page().waitForTimeout(100);
        classes = await locator.evaluate(el => [...el.classList]);
    }
    if (!classes.includes(cls)) throw new Error(`Expected ${what}, but the element has classes "${classes.join(' ')}".`);
}
