// Captures the named screenshot states of the spec pages into docs/spec/img/.
// Run seed.mjs once first. See README.md in this folder.
//
//   node capture.mjs --origin http://127.0.0.1:7651 --db <app>/Data/yap.db [state ...]
//
// A state is <page>-<device>-<name>, which is also its file name. Most states need
// the old page (commit 0cb67422); a state with app: 'main' needs the current client.
// The script asks --origin which one it serves. With no names, every state of that
// app is captured.

import { fileURLToPath } from 'node:url';
import {
    applyTheme, chromium, detectApp, DEVICES, parseArgs, queryDb, readTokens, ROWS, signInWithToken, waitForConversation, waitForVisibleImages,
} from './common.mjs';

const OUT_DIR = fileURLToPath(new URL('../../img/', import.meta.url));

// The spec shows the light theme. The page's own applyTheme (what Settings calls)
// switches it, so the seeded accounts keep whatever theme they have.
const THEME = 'light';

// How much of the conversation column a desktop input box shot shows, from the
// bottom of the window: the box plus the last two or three messages above it.
const DESKTOP_COMPOSER_HEIGHT = 260;

// The timeline images: the open conversation from the bottom of the window, and the
// start of the history from the top of the column.
const DESKTOP_TIMELINE_HEIGHT = 600;
const DESKTOP_HISTORY_TOP_HEIGHT = 360;

// The space a row or picker shot keeps around its element, so it has a little context.
const MARGIN = 12;

// The Settings image shows the page from the header to the end of the Appearance
// card, about 1370 px. The page scrolls inside its own column, which a full-page shot
// does not reach, so that state uses a window this tall. The width stays the desktop's.
const SETTINGS_TOP_WINDOW = { width: 1280, height: 1400 };

// The other people online while a state marked people: true is captured (old page
// only): bob online, carol away by her own pick. dave stays offline, a grey row.
const PEOPLE_ONLINE = [['bob', 'Online'], ['carol', 'Away']];

// The phone profile sends Chromium's desktop User-Agent, and the old page decides on
// the server, from the User-Agent, whether a device is a phone. A state that shows
// what only phones get (the sidebar's info buttons) sends a phone's.
const PHONE_USER_AGENT = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36';

const APP_NAMES = { old: 'the old page (commit 0cb67422)', main: 'the current client' };

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
    {
        page: 'timeline', device: 'desktop', name: 'open',
        // The lobby just opened: the newest messages at the bottom, the input box under
        // them. The conversation column, about DESKTOP_TIMELINE_HEIGHT pixels high.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const gap = await page.locator('.messages').evaluate(el => el.scrollHeight - el.scrollTop - el.clientHeight);
            if (gap > 2) throw new Error(`Expected the view at the bottom, but it is ${gap}px above it.`);
            return columnBottomClip(page, DESKTOP_TIMELINE_HEIGHT, { atHeader: true });
        },
    },
    {
        page: 'timeline', device: 'desktop', name: 'history-start',
        // The lobby scrolled to its top with the wheel: the note "This is the beginning
        // of #lobby", the empty space under it, the first messages. The top of the column.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const list = page.locator('.messages');
            const box = await list.boundingBox();
            await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
            const note = page.locator('.history-start');
            // Older pages load on the way up; the note shows once all history is there.
            for (let i = 0; i < 60 && !(await note.count() && await list.evaluate(el => el.scrollTop === 0)); i++) {
                await page.mouse.wheel(0, -1000);
                await page.waitForTimeout(200);
            }
            const text = (await note.innerText()).trim();
            if (text !== 'This is the beginning of #lobby') throw new Error(`Expected the beginning note, got "${text}".`);
            await waitForVisibleImages(page);
            return columnTopClip(page, DESKTOP_HISTORY_TOP_HEIGHT);
        },
    },
    {
        page: 'timeline', device: 'phone', name: 'gallery',
        // A tap on the first of the two photos alice sent bob: the viewer on black, the
        // counter "1 / 2", close and fill. Whole screen.
        async drive(page) {
            await openConversation(page, 'alice', '/dm/bob');
            const rows = page.locator(ROWS);
            const i = await rows.evaluateAll(els => els.findLastIndex(
                r => r.dataset.author === 'alice' && r.querySelectorAll('.gallery-item').length > 1));
            if (i < 0) throw new Error('No message from alice with two images: run seed.mjs.');
            const item = rows.nth(i).locator('.gallery-item').first();
            await item.scrollIntoViewIfNeeded();
            await item.tap();
            const viewer = page.locator('.image-modal');
            await viewer.waitFor({ state: 'visible' });
            await page.waitForFunction(() => {
                const img = document.querySelector('.image-modal .modal-stage img');
                return img && img.complete && img.naturalWidth > 0;
            });
            const counter = (await viewer.locator('.modal-counter').innerText()).replace(/\s+/g, ' ').trim();
            if (counter !== '1 / 2') throw new Error(`Expected the counter "1 / 2", got "${counter}".`);
            for (const control of ['.modal-close', '.modal-fill']) await viewer.locator(control).waitFor({ state: 'visible' });
            await page.waitForTimeout(400); // the fade-in
            return null;
        },
    },
    {
        page: 'frame', device: 'desktop', name: 'room', people: true,
        // The lobby, whole window: the title, the status chip, the mailbox (unread in
        // general and from carol), the people count, the header line, the sidebar.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await expectMailbox(page);
            return null;
        },
    },
    {
        page: 'frame', device: 'desktop', name: 'status-menu',
        // A click on alice's chip (she is the admin): the menu with its eight entries.
        // The right side of the header with the open menu.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('.status-button').click();
            const menu = page.locator('.status-dropdown');
            await menu.waitFor({ state: 'visible' });
            const items = (await menu.locator('.dropdown-item').allInnerTexts()).map(t => t.replace(/\s+/g, ' ').trim());
            const expected = ['Online', 'Away', 'Invisible', 'Settings', 'Notification settings', 'Admin', 'Sign Out'];
            if (items.join() !== expected.join() || await menu.locator('.dropdown-divider').count() !== 1) {
                throw new Error(`Expected ${expected.join(', ')} with one divider, got ${items.join(', ')}.`);
            }
            await expectClass(menu.locator('.dropdown-item').first(), 'active', 'Online marked');
            await page.waitForTimeout(300);
            return unionClip(page, [menu, page.locator('.header-controls')]);
        },
    },
    {
        page: 'frame', device: 'desktop', name: 'profile-card', people: true,
        // The pointer on bob's name in the sidebar: his card with the banner, avatar,
        // display name, @bob, Online, About me, location and Member since. The card and
        // the rows around his.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const row = sidebarPerson(page, 'Bob K.');
            await row.locator('.user-name').hover();
            const card = page.locator('.profile-popup .profile-card');
            await card.waitFor({ state: 'visible' });
            // innerText follows the card's capitals in CSS, so the check ignores case.
            const text = (await card.innerText()).toLowerCase();
            const expected = ['Bob K.', '@bob', 'Online', 'About me', 'Trail runner', 'Brno', 'Member since'];
            const absent = expected.filter(t => !text.includes(t.toLowerCase()));
            if (absent.length) throw new Error(`The card lacks ${absent.join(', ')}: run seed.mjs.`);
            await waitForVisibleImages(page);
            await page.waitForTimeout(300);
            return unionClip(page, [card, row, page.locator('.users-sidebar .sidebar-section', { has: page.locator('.users-list') })]);
        },
    },
    {
        page: 'frame', device: 'phone', name: 'sidebar', people: true, userAgent: PHONE_USER_AGENT,
        // A tap on the hamburger: the sidebar over the conversation, the header above
        // it, an info button after each other person's name. Whole screen.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.locator('.users-toggle').tap();
            await page.locator('.users-sidebar.sidebar-open').waitFor();
            await sidebarPerson(page, 'Bob K.').locator('.profile-info-btn').waitFor();
            await page.waitForTimeout(400); // the slide-in
            return null;
        },
    },
    {
        page: 'presence', device: 'desktop', name: 'list', people: true,
        // The people section: alice (you) with the crown, the bot, carol away, bob
        // online, dave offline. The section with a margin.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const section = page.locator('.users-sidebar .sidebar-section', { has: page.locator('.users-list') });
            const dots = Object.fromEntries(await section.locator('.user-item').evaluateAll(items => items.map(i => [
                i.querySelector('.user-name').textContent.trim(),
                [...i.querySelector('.user-status-dot').classList].filter(c => c !== 'user-status-dot').join(' '),
            ])));
            const expected = { 'alice (you)': 'online', Ping: 'online', carol: 'away', 'Bob K.': 'online', dave: 'invisible' };
            for (const [name, dot] of Object.entries(expected)) {
                if (dots[name] !== dot) throw new Error(`Expected ${name} ${dot}, got ${JSON.stringify(dots)}.`);
            }
            await sidebarPerson(page, 'alice').locator('.admin-badge').waitFor();
            await sidebarPerson(page, 'Ping').locator('.bot-badge').waitFor();
            return unionClip(page, [section]);
        },
    },
    {
        page: 'notifications', device: 'desktop', name: 'sidebar', people: true,
        // The unread marks: general with a count, random muted with the bell and a dot,
        // carol with a count, dave muted with the bell and a dot, the mailbox with the
        // total. The right side of the header and the sidebar down to the last person.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const marks = [
                [sidebarRoom(page, 'general'), '.unread-badge'],
                [sidebarRoom(page, 'random'), '.mute-bell'],
                [sidebarRoom(page, 'random'), '.unread-dot'],
                [sidebarPerson(page, 'carol'), '.unread-badge'],
                [sidebarPerson(page, 'dave'), '.mute-bell'],
                [sidebarPerson(page, 'dave'), '.unread-dot'],
            ];
            for (const [row, mark] of marks) {
                if (!await row.locator(mark).count()) throw new Error(`A row lacks ${mark}: run seed.mjs (or a capture read it).`);
            }
            const counts = await page.locator('.users-sidebar .unread-badge').allInnerTexts();
            const total = await expectMailbox(page);
            const sum = counts.reduce((a, c) => a + Number(c), 0);
            if (total !== sum) throw new Error(`The mailbox reads ${total}, the rows add up to ${sum}.`);
            return unionClip(page, [page.locator('.header-controls'), page.locator('.users-sidebar .rooms-list'), page.locator('.users-sidebar .users-list')]);
        },
    },
    {
        page: 'sessions', device: 'desktop', name: 'login', signedOut: true,
        // A browser that never signed in, on the login page, after typing a name nobody
        // has: the heading, the box and the join button in one row, the new-account
        // hint, the build time. The card with the heading.
        async drive(page) {
            await page.goto(`${origin}/login`);
            const input = page.locator('.username-input');
            await input.waitFor();
            await applyTheme(page, THEME);
            // The page is interactive only once its connection is up; earlier keys are lost.
            await page.waitForTimeout(1500);
            await input.pressSequentially('marek', { delay: 50 });
            const hint = page.locator('.new-account-hint');
            await hint.waitFor();
            if (!(await hint.innerText()).includes('marek')) throw new Error('The hint does not name the typed username.');
            const card = page.locator('.username-container');
            return unionClip(page, [card.locator('h2'), card.locator('.username-form'), card.locator('.build-info')]);
        },
    },
    {
        page: 'settings', device: 'desktop', name: 'top', people: true,
        // bob (display name, bio, location) opens Settings from the status menu: the frame
        // with the back arrow and "Settings", the page heading with the "Saved" chip, the
        // Profile card, the Appearance card with the theme grid. From the top of the window
        // to the end of the Appearance card, in a taller window (SETTINGS_TOP_WINDOW).
        async drive(page) {
            await page.setViewportSize(SETTINGS_TOP_WINDOW);
            await openConversation(page, 'bob', '/lobby');
            await expectClass(page.locator('.status-button'), 'status-online', 'bob Online in the header');
            await openFromStatusMenu(page, 'Settings', '/settings');
            const title = (await page.locator('.chat-header .dm-header-title').innerText()).trim();
            if (title !== 'Settings') throw new Error(`Expected "Settings" in the header, got "${title}".`);
            await page.locator('.chat-header .back-button').waitFor({ state: 'visible' });
            await page.waitForFunction(() => document.querySelector('#display-name')?.value);
            // The one style this script adds to a page. The old build means to hide the native
            // file input behind "Upload Picture" (.file-input { display: none } in
            // Settings.razor.css), but a scoped rule cannot reach the <input> that the
            // InputFile component draws, so "Choose File / No file chosen" shows. The spec
            // shows only the button.
            await page.addStyleTag({ content: '#profile-upload { display: none !important; }' });
            await expectWidth(page, '#profile-upload', w => w === 0, 'the native file input hidden');
            for (const [id, value] of Object.entries({ 'display-name': 'Bob K.', bio: 'Trail runner', country: 'Brno' })) {
                if (!(await page.locator(`#${id}`).inputValue()).startsWith(value)) throw new Error(`#${id} lacks "${value}": run seed.mjs.`);
            }

            // THEME paints the page but does not save it, so the check badge would sit on
            // bob's own theme while the page shows Daylight. He picks Daylight here and his
            // own theme again after the shot. (An unset theme comes back as the default
            // theme set explicitly; the Themes stats on the admin page count both the same.)
            const cards = page.locator('.theme-grid .theme-card');
            if (await cards.count() !== 11) throw new Error(`Expected 11 theme cards, got ${await cards.count()}.`);
            const own = await page.locator('.theme-grid .theme-card.selected').getAttribute('title');
            const daylight = page.locator('.theme-grid .theme-card[title="Daylight"]');
            if (own !== 'Daylight') {
                await daylight.click();
                page.cleanup = async () => {
                    await page.locator(`.theme-grid .theme-card[title="${own}"]`).click();
                    // The badge moves before the server saves; the page must stay open until it has.
                    for (let i = 0; i < 50 && (await queryDb(db, 'SELECT Theme FROM Users WHERE Username = ?', ['bob']))[0].Theme === THEME; i++) {
                        await page.waitForTimeout(100);
                    }
                };
            }
            await expectClass(daylight, 'selected', 'Daylight checked');
            const shown = await page.evaluate(() => document.documentElement.dataset.theme);
            if (shown !== THEME) throw new Error(`Daylight should paint the page as "${THEME}", got "${shown}".`);

            await page.locator('.settings-container').evaluate(el => { el.scrollTop = 0; });
            const appearance = page.locator('.settings-section', { has: page.locator('h3', { hasText: /^Appearance$/ }) });
            const box = await appearance.boundingBox();
            const bottom = Math.ceil(box.y + box.height + MARGIN);
            if (bottom > SETTINGS_TOP_WINDOW.height) throw new Error(`The Appearance card ends at ${bottom}px, below the window.`);
            await page.mouse.move(900, 300); // empty space right of the cards: no hover tint anywhere
            await waitForVisibleImages(page);

            // The chip shows for 2 s after a profile save. A space typed and taken back
            // saves the same values, so the profile does not change. It goes last, so the
            // shot comes while the chip still shows.
            const bio = page.locator('#bio');
            const text = await bio.inputValue();
            await bio.focus();
            await bio.evaluate(el => el.setSelectionRange(el.value.length, el.value.length));
            await page.keyboard.type(' ');
            await page.keyboard.press('Backspace');
            await bio.evaluate(el => el.blur());
            await page.locator('.settings-header .save-status.saved', { hasText: 'Saved' }).waitFor();
            if (await bio.inputValue() !== text) throw new Error('The bio changed.');
            return { x: 0, y: 0, width: SETTINGS_TOP_WINDOW.width, height: bottom };
        },
    },
    {
        page: 'admin', device: 'desktop', name: 'users', people: true, noHeader: true,
        // alice (the admin) opens Admin from the status menu: the navigation column with
        // the back arrow, "Admin" and the ten tabs, Users open; the users table with
        // avatars, status pills (bob online, carol away, dave offline) and the PWA
        // column. Whole screen. The page sits outside the chat frame: no header.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await openFromStatusMenu(page, 'Admin', '/admin');
            const nav = page.locator('.admin-sidebar');
            await nav.locator('.back-button[title="Go back"]').waitFor({ state: 'visible' });
            const heading = (await nav.locator('h2').innerText()).trim();
            const tabs = (await nav.locator('.nav-item').allInnerTexts()).map(t => t.trim());
            const expectedTabs = ['Users', 'Debug', 'Bot', 'Settings', 'Invites', 'Logs', 'Media', 'GIFs', 'Diagnostics', 'Visitors'];
            if (heading !== 'Admin' || tabs.join() !== expectedTabs.join()) {
                throw new Error(`Expected "Admin" and ${expectedTabs.join(', ')}, got "${heading}" and ${tabs.join(', ')}.`);
            }
            await expectClass(nav.locator('.nav-item').first(), 'active', 'Users open');

            const table = page.locator('.users-table');
            await table.locator('tbody tr').first().waitFor();
            // innerText follows the headers' capitals in CSS, so the check ignores case.
            const columns = (await table.locator('thead th').allInnerTexts()).map(t => t.trim().toLowerCase());
            const expectedColumns = ['user', 'status', 'pwa', 'created', 'last action', 'locale', 'recent ips'];
            if (columns.join() !== expectedColumns.join()) throw new Error(`Expected the columns ${expectedColumns.join(', ')}, got ${columns.join(', ')}.`);
            const rows = await table.locator('tbody tr').evaluateAll(trs => trs.map(tr => ({
                name: tr.querySelector('.user-name').firstChild.textContent.trim(),
                pill: tr.querySelector('.status-pill').textContent.trim(),
                avatar: !!tr.querySelector('.user-cell .avatar'),
                crown: !!tr.querySelector('.admin-badge'),
            })));
            const pills = Object.fromEntries(rows.map(r => [r.name, r.pill]));
            const expected = { alice: 'Online', 'Bob K.': 'Online', carol: 'Away', dave: 'Offline', Ping: 'Online' };
            for (const [name, pill] of Object.entries(expected)) {
                if (pills[name] !== pill) throw new Error(`Expected ${name} ${pill}, got ${JSON.stringify(pills)}.`);
            }
            if (!rows.every(r => r.avatar)) throw new Error('A row lacks its avatar.');
            // One admin, one crown. The old build keeps the bot's admin flag in the database
            // (see README.md, "Seed"), and then the bot's row has the crown too.
            const crowns = rows.filter(r => r.crown).map(r => r.name);
            if (crowns.join() !== 'alice') throw new Error(`Expected the crown on alice only, got ${crowns.join(', ')}: clear the bot's admin flag (README.md).`);
            // The pointer stays where the menu was, over the table; a row under it is tinted.
            await page.mouse.move(1100, 760);
            await waitForVisibleImages(page);
            return null;
        },
    },
    {
        page: 'timeline', device: 'desktop', name: 'pill', app: 'main',
        // The current client. alice scrolls up about two screens with the wheel; bob
        // posts from his own session; the view holds and the New messages pill shows
        // above the typing strip. The bottom of the column with the pill and the input
        // box. bob deletes his message after the capture, so the lobby stays as seeded.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            const list = page.locator('.messages');
            const box = await list.boundingBox();
            await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
            for (let i = 0; i < 6; i++) {
                await page.mouse.wheel(0, -box.height / 3);
                await page.waitForTimeout(100);
            }
            await page.waitForTimeout(500);
            const top = await list.evaluate(el => el.scrollTop);
            const bob = await page.openAs('bob', '/lobby');
            const text = 'does anyone know if the hut takes cards?';
            const before = await bob.locator(ROWS).count();
            await sendText(bob, text);
            await bob.waitForFunction(([sel, n]) => document.querySelectorAll(sel).length > n, [ROWS, before]);
            const id = await bob.locator(ROWS).last().getAttribute('id');
            page.cleanup = async () => {
                const row = bob.locator(`#${id}`);
                await row.hover();
                await row.locator('.action-more').click();
                await bob.locator('.more-menu-item', { hasText: 'Delete Message' }).click();
                await bob.locator('.delete-confirm-dialog .confirm-delete').click();
                await row.waitFor({ state: 'detached' });
            };
            const pill = page.locator('#new-messages');
            await pill.waitFor({ state: 'visible' });
            await page.locator('#typing').waitFor({ state: 'hidden' });
            const moved = await list.evaluate(el => el.scrollTop) - top;
            if (Math.abs(moved) > 1) throw new Error(`The view moved by ${moved}px when bob's message arrived.`);
            await applyTheme(page, THEME); // the arrival brought the account's theme back
            await page.waitForTimeout(300);
            const pillTop = (await pill.boundingBox()).y;
            return columnBottomClip(page, page.viewportSize().height - pillTop + 100);
        },
    },
    {
        page: 'offline', device: 'phone', name: 'pending', app: 'main',
        // The current client on a phone without a connection: the grey ring in the
        // header, the last messages, and "see you there" faded at the bottom, pending.
        // Whole screen. The pending message dies with the browser context.
        async drive(page) {
            await openConversation(page, 'alice', '/lobby');
            await page.context().setOffline(true);
            await page.locator('html[data-connection="offline"]').waitFor({ state: 'attached' });
            const box = page.locator('textarea.message-input');
            await box.tap();
            await box.fill('see you there');
            await page.locator('.send-button').tap();
            // Pending rows have their own list (#pending) under the accepted ones.
            await page.locator('.message-group.pending-message', { hasText: 'see you there' }).waitFor();
            await applyTheme(page, THEME);
            await page.waitForTimeout(400);
            return null;
        },
    },
];

const { origin, db, names } = parseArgs(process.argv.slice(2));
const app = await detectApp(origin);
const fileName = s => `${s.page}-${s.device}-${s.name}`;
const appOf = s => s.app ?? 'old';
const unknown = names.filter(n => !STATES.some(s => fileName(s) === n));
if (unknown.length) {
    throw new Error(`Unknown state(s): ${unknown.join(', ')}. Known: ${STATES.map(fileName).join(', ')}`);
}
const selected = names.length ? STATES.filter(s => names.includes(fileName(s))) : STATES.filter(s => appOf(s) === app);
const elsewhere = selected.filter(s => appOf(s) !== app);
if (elsewhere.length) {
    throw new Error(`${origin} serves ${APP_NAMES[app]}, but ${elsewhere.map(fileName).join(', ')} need ${APP_NAMES[appOf(elsewhere[0])]}.`);
}
const people = selected.some(s => s.people);

const tokens = await readTokens(db, ['alice', 'bob', ...(people ? ['carol', 'dave'] : [])]);
const missing = ['alice', 'bob', ...(people ? ['carol', 'dave'] : [])].filter(n => !tokens[n]);
if (missing.length) throw new Error(`${missing.join(', ')} missing: run seed.mjs first.`);

const browser = await chromium.launch();
try {
    const open = async (user, path, device = 'desktop') => {
        const page = await (await browser.newContext(DEVICES[device])).newPage();
        await signInWithToken(page, origin, tokens[user], path);
        await waitForConversation(page);
        return page;
    };

    // One session of alice stays open for the whole run. When the last session of a user
    // closes, the server sets them Away, and the next state's header would show it. A
    // session that closed a moment ago (the seed's) still holds that Away, and a new
    // session does not reset it on the old page: the pick does.
    const anchor = await open('alice', '/dm/ping');
    if (app === 'old') await pickStatus(anchor, 'Online');

    if (people) {
        await expectOffline(anchor, 'dave');
        for (const [user, status] of PEOPLE_ONLINE) await pickStatus(await open(user, '/lobby'), status);
        // A person's first session makes the bot tell alice (the admin) "just joined".
        // Opening the bot conversation again reads it, so no count shows on Ping.
        await anchor.goto(`${origin}/dm/ping`);
        await waitForConversation(anchor);
    }

    for (const state of selected) {
        // A fresh context per state: no draft, picker or hover left over from the last one.
        const context = await browser.newContext({ ...DEVICES[state.device], ...(state.userAgent && { userAgent: state.userAgent }) });
        const page = await context.newPage();
        const others = [];
        page.signIn = (user, path) => signInWithToken(page, origin, tokens[user], path);
        // Another person's session for this state only.
        page.openAs = async (user, path) => {
            const other = await open(user, path);
            others.push(other.context());
            return other;
        };
        const clip = await state.drive(page);
        // A whole screen shows the header's status pill: alice reads Online.
        if (!clip && !state.signedOut && !state.noHeader) await expectClass(page.locator('.status-button').first(), 'status-online', 'alice Online in the header');
        const path = `${OUT_DIR}${fileName(state)}.png`;
        // animations: 'disabled' runs transitions to their end, so Send is fully
        // shown or fully collapsed rather than caught halfway.
        await page.screenshot({ path, clip: clip ?? undefined, animations: 'disabled' });
        console.log(`${fileName(state)}.png`);
        // A state that changed data undoes it here.
        await page.cleanup?.();
        await Promise.all([context, ...others].map(c => c.close()));
    }
} finally {
    await browser.close();
}

async function openConversation(page, user, path) {
    await page.signIn(user, path);
    await waitForConversation(page);
    await applyTheme(page, THEME);
    // The page scrolls to the newest message on load; images above can still shift it.
    await page.locator('.messages').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await waitForVisibleImages(page);
}

// The conversation column (not the sidebar), about DESKTOP_COMPOSER_HEIGHT pixels
// from the bottom.
function composerClip(page) {
    return columnBottomClip(page, DESKTOP_COMPOSER_HEIGHT);
}

// The conversation column, about `height` pixels from the bottom of the window. The
// top edge moves up into the gap above the first message row that the target line
// would cut, so no row is cut in half. With atHeader it moves down instead, to the gap
// above the first row with a name: rows of one person touch, so the gap above a row
// in the middle of a group is too thin to cut in.
async function columnBottomClip(page, height, { atHeader = false } = {}) {
    const column = await page.locator('.messages').boundingBox();
    const bottom = page.viewportSize().height;
    const target = bottom - height;
    const rowTop = await page.locator(ROWS).evaluateAll((rows, [line, header]) => rows
        .filter(r => !header || r.classList.contains('has-header'))
        .map(r => r.getBoundingClientRect())
        .find(r => (header ? r.top >= line : r.bottom > line))?.top ?? line, [target, atHeader]);
    const y = Math.floor(atHeader ? rowTop - 12 : Math.min(target, rowTop - 8));
    return { x: column.x, y, width: column.width, height: bottom - y };
}

// The conversation column from its top, about `height` pixels down. The bottom edge
// moves up into the gap above the row that the target line would cut.
async function columnTopClip(page, height) {
    const column = await page.locator('.messages').boundingBox();
    const target = column.y + height;
    const rowTop = await page.locator(ROWS).evaluateAll(
        (rows, line) => rows.map(r => r.getBoundingClientRect()).find(r => r.bottom > line)?.top ?? line, target);
    const bottom = Math.ceil(Math.min(target, rowTop - 8));
    return { x: column.x, y: Math.floor(column.y), width: column.width, height: bottom - Math.floor(column.y) };
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

// A pick in the status menu (old page): Online, Away or Invisible.
async function pickStatus(page, label) {
    await page.locator('.status-button').click();
    await page.locator('.status-dropdown .dropdown-item', { hasText: new RegExp(`^\\s*${label}\\s*$`) }).click();
    await page.locator(`.status-button.status-${label.toLowerCase()}`).waitFor();
}

// An entry of the status menu that opens a page (old page): Settings or Admin.
async function openFromStatusMenu(page, label, path) {
    await page.locator('.status-button').click();
    await page.locator('.status-dropdown .dropdown-item', { hasText: new RegExp(`^\\s*${label}\\s*$`) }).click();
    await page.waitForURL(url => url.pathname === path);
}

// dave is grey only while he has no session. The seed signs him out; a closed tab
// would leave his session on the old page for four hours, as Away.
async function expectOffline(page, user) {
    const dot = sidebarPerson(page, user).locator('.user-status-dot');
    if (!await dot.evaluate(el => el.classList.contains('invisible'))) {
        throw new Error(`${user} has a session, so his dot is not grey. Restart the app: it forgets every session.`);
    }
}

function sidebarPerson(page, name) {
    return page.locator('.users-sidebar .user-item', { has: page.locator('.user-name', { hasText: new RegExp(`^${name}( \\(you\\))?$`) }) });
}

function sidebarRoom(page, name) {
    return page.locator('.users-sidebar .room-item', { has: page.locator('.room-name', { hasText: new RegExp(`^${name}$`) }) });
}

async function sendText(page, text) {
    const box = page.locator('textarea.message-input');
    await box.fill(text);
    await box.press('Enter');
}

// The mailbox shows a count above zero; returns it.
async function expectMailbox(page) {
    const badge = page.locator('.mailbox-badge');
    await badge.waitFor();
    const total = Number(await badge.innerText());
    if (!(total > 0)) throw new Error('No mailbox count: run seed.mjs (or a capture read the unread conversations).');
    return total;
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
