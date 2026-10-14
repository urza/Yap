// Seeds a fresh Yap instance with the data the spec screenshots show:
// accounts alice and bob, a conversation in #lobby (one image, one reply with
// reactions, one other reaction), a direct message thread with two photos in one
// message, recent emoji for alice and one GIF that alice uploaded (her favourite).
// For the frame, presence and notification images: carol and dave, the rooms
// general and random, bob's profile, alice's mutes and messages she has not read.
//
// Everything goes through the real page, the way a user does it, so the script
// works on any version of the app without knowing its internals.
//
//   node seed.mjs --origin http://127.0.0.1:7651 --db <app>/Data/yap.db
//
// The base seed runs once, when alice and bob do not exist. The additions after
// it run every time and skip each step that is already there, so an instance
// seeded before they existed gets them too. On the current client (not the old
// page) only the base runs: its two images need nothing more.

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium, detectApp, DEVICES, parseArgs, queryDb, readTokens, ROWS, signInWithToken, waitForConversation } from './common.mjs';

const { origin, db } = parseArgs(process.argv.slice(2));
const app = await detectApp(origin);

// The + button's file input: inside the button on the old page, beside it in the current client.
const UPLOAD_INPUT = '.image-upload-button input[type=file], #upload-files';

// Each step is [who, text] for a message, or [who, {kind, ...}] for something else.
// Times are not set: the rows get the time of the seed run.
const LOBBY = [
    ['bob', 'morning! anyone up?'],
    ['alice', 'barely ☕'],
    ['bob', 'same. the train was packed again'],
    ['alice', 'ugh. did you finish the slides for thursday?'],
    ['bob', 'almost, two charts left'],
    ['alice', 'nice 👍'],
    ['bob', 'are we still on for saturday?'],
    ['alice', 'yes! forecast says sunny, 22°'],
    ['bob', 'perfect hiking weather'],
    ['alice', 'I was thinking the ridge trail, the long loop'],
    ['bob', 'how long is it?'],
    ['alice', 'about 14 km, 5 hours with breaks'],
    ['bob', 'ok my legs will hate me but fine 😅'],
    ['alice', 'there is a hut at the top with really good soup'],
    ['bob', 'now you are talking'],
    ['bob', 'what time do we leave?'],
    ['alice', '7:30 train from the main station?'],
    ['bob', '7:30 on a saturday 😩'],
    ['alice', 'the 8:30 gets there too late, the hut closes at 3'],
    ['bob', 'fine fine, 7:30 it is'],
    ['alice', "I'll bring sandwiches"],
    ['bob', "I'll bring coffee and the good chocolate"],
    ['alice', 'deal 🤝'],
    ['alice', { kind: 'image' }],
    ['alice', 'the view from the top, last year'],
    ['bob', 'wow ok, that sells it'],
    ['bob', { kind: 'react', to: 'the view from the top', emoji: '❤️' }],
    ['bob', 'should we ask Marek too?'],
    ['alice', 'already did, he is in if he can find his boots 😂'],
    ['bob', { kind: 'reply', to: "I'll bring sandwiches", text: 'no cucumber in mine please' }],
    ['alice', 'noted, no cucumber'],
    ['bob', 'see you saturday then'],
    ['alice', 'see you! 🥾'],
];

const DM = [
    ['alice', 'hey, do you still have my headlamp?'],
    ['bob', 'yes! it is in my backpack, sorry'],
    ['alice', 'no worries, bring it on saturday'],
    ['bob', 'will do 👍'],
    ['bob', 'also, happy birthday for tomorrow 🎂'],
    ['alice', 'haha thank you 😊'],
];

// The additions. The direct thread gets a group of three rows from alice between
// two rows from bob (the message row group image). Two of her emoji are picks in
// the emoji picker, not typed: only picks and reactions fill an account's recent
// emoji, which the emoji picker image shows. Last, alice uploads a GIF through the
// GIF picker; an upload also adds the GIF to her favourites, so the GIF picker has
// a Favorites tile and a card to show. Then she sends two photos in one message: the
// gallery viewer shows its counter only for a message with more than one image. They
// go here, not in the lobby, so the lobby images keep their last rows.
const DM_MORE = [
    ['bob', 'so is there cake on saturday?'],
    ['alice', 'of course, a whole one'],
    ['alice', { kind: 'pick', text: 'we can eat it at the hut', emoji: '🎂' }],
    ['alice', { kind: 'pick', text: 'if it survives the trail', emoji: '😅' }],
    ['bob', "I'll carry it like a baby"],
    ['alice', { kind: 'gif' }],
    ['alice', { kind: 'photos' }],
];

// Reactions on bob's reply (the reply row image): two pills, and for alice only the
// second one is her own, so one pill is highlighted and one is not. alice's reaction
// is also her newest recent emoji.
const LOBBY_MORE = [
    ['bob', { kind: 'react', to: 'no cucumber in mine please', emoji: '👍' }],
    ['alice', { kind: 'react', to: 'no cucumber in mine please', emoji: '😂' }],
    ['bob', { kind: 'react', to: 'no cucumber in mine please', emoji: '😂' }],
];

// The frame, presence and notification images. Two more people: carol writes alice
// two direct messages she has not read; dave writes one that alice mutes, and he
// stays offline, so the people list has a grey row. Two more rooms, which alice (the
// admin) makes with Add Room: general, with a two-line description, where two posts
// from bob are unread for her, and random, which she mutes, so bob's post there is a
// dot. bob gets a display name, a bio and a location for the profile card.
const PEOPLE = ['carol', 'dave'];

const ROOMS = [
    { name: 'general', description: 'Plans, links and anything that is not for the lobby.\nBe kind: everyone can read this room.' },
    { name: 'random', description: '' },
];

// Settings field id -> value.
const BOB_PROFILE = {
    'display-name': 'Bob K.',
    bio: 'Trail runner, coffee snob, keeper of the good chocolate.',
    country: 'Brno',
};

// [who, where, text]: '#room' or '@user' for the direct message with that user. alice
// stays online in the lobby while they arrive: a post in a room she muted counts only
// because she is online (NT-03).
const UNREAD = [
    ['bob', '#general', 'did you see the new trail map at the station?'],
    ['bob', '#general', 'the ridge loop is marked as difficult now'],
    ['bob', '#random', 'found a great bakery next to the station'],
    ['carol', '@alice', 'hi alice! is there room for one more on saturday?'],
    ['carol', '@alice', 'I can bring a thermos of tea'],
    ['dave', '@alice', 'hey, can I borrow your trekking poles sometime?'],
];

// alice's notification settings, both in Individual mode so each conversation has its
// own bell. A room starts muted (NT-18), so the lobby and general are allowed here;
// with the room rows' bells, a muted lobby would show one on the open room. Name -> muted.
const MUTES = {
    Rooms: { '#lobby': false, '#general': false, '#random': true },
    'Direct messages': { dave: true },
};

if (!db) throw new Error('Pass --db <app>/Data/yap.db so the seed can see whether it already ran.');
const existing = await readTokens(db, ['alice', 'bob']);

const work = mkdtempSync(join(tmpdir(), 'yap-seed-'));
const browser = await chromium.launch();
try {
    const files = {
        image: await drawRidgePhoto(browser, join(work, 'ridge.jpg')),
        gif: writeSunriseGif(join(work, 'sunrise-over-the-ridge.gif')),
        photos: await drawHutPhotos(browser, work),
    };
    if (Object.keys(existing).length) {
        console.log(`Base seed skipped (${Object.keys(existing).join(', ')} exist). Delete the app's Data/yap.db for a fresh seed.`);
    } else {
        await seedBase(browser, files);
    }
    // The current client's two states need only the lobby; everything after the base
    // drives the old page's pickers, Settings and Add Room.
    if (app === 'main') {
        console.log('Current client: base seed only.');
    } else {
        await seedAdditions(browser, files);
        await seedFrame(browser);
    }
} finally {
    await browser.close();
    rmSync(work, { recursive: true, force: true });
}

async function seedBase(browser, files) {
    // Desktop for both: the message actions (Reply, reactions) show on hover.
    const pages = {};
    for (const name of ['alice', 'bob']) {
        const page = await (await browser.newContext(DEVICES.desktop)).newPage();
        // A new username at /auth/signin creates the account while registration is open.
        await page.goto(`${origin}/auth/signin?username=${name}`);
        if (new URL(page.url()).pathname !== '/lobby') throw new Error(`Could not create ${name}: ended at ${page.url()}`);
        await page.locator('textarea.message-input').waitFor();
        pages[name] = page;
    }

    await runConversation(pages, LOBBY, files);

    // One after the other: two first opens at the same moment can make two threads for
    // the same pair on the old page, and its Settings page then fails on the duplicate.
    for (const [page, path] of [[pages.alice, '/dm/bob'], [pages.bob, '/dm/alice']]) {
        await page.goto(`${origin}${path}`);
        await page.locator('textarea.message-input').waitFor();
    }
    await runConversation(pages, DM, files);

    // The bot sends every new account a welcome message one minute after sign-up.
    // Wait for it with the bot conversation open, so it arrives read and no unread
    // badge shows in the screenshots. The current client sends no welcome once the
    // bot conversation exists, and this visit makes it exist: there the visit only
    // reads the bot's note to alice that bob joined.
    await Promise.all(Object.values(pages).map(async page => {
        await page.goto(`${origin}/dm/ping`);
        if (app === 'main') await waitForConversation(page);
        else await page.locator('.message-content', { hasText: /welcome/i }).first().waitFor({ timeout: 120000 });
    }));

    await Promise.all(Object.values(pages).map(p => p.context().close()));
    console.log(`Seeded ${origin}: alice, bob, ${LOBBY.length} lobby steps, ${DM.length} direct messages.`);
}

async function seedAdditions(browser, files) {
    const tokens = await readTokens(db, ['alice', 'bob']);
    if (!tokens.alice || !tokens.bob) throw new Error('alice or bob is missing: delete Data/yap.db and seed again.');

    const dmWith = { alice: '/dm/bob', bob: '/dm/alice' };
    const pages = {};
    for (const name of ['alice', 'bob']) {
        const page = await (await browser.newContext(DEVICES.desktop)).newPage();
        await signInWithToken(page, origin, tokens[name], dmWith[name]);
        pages[name] = page;
    }
    await Promise.all(Object.values(pages).map(p => waitForConversation(p)));
    let added = await runConversation(pages, DM_MORE, files, isDone);

    // Opening a conversation marks it read. A message that arrives while it is open
    // is marked read only when the server sees the reader as present, and a fresh
    // headless page can still count as Away. Without the second open, the thread
    // stays unread and the header shows a badge in the phone screenshots.
    await openEverywhere(pages, name => dmWith[name]);
    await openEverywhere(pages, () => '/lobby');
    added += await runConversation(pages, LOBBY_MORE, files, isDone);

    console.log(`Additions: ${added} of ${DM_MORE.length + LOBBY_MORE.length} steps added, the rest were there.`);
}

async function seedFrame(browser) {
    // A new name at /auth/signin creates the account. Every first session of a person
    // also makes the bot tell alice (the admin) "just joined"; she reads those last.
    const known = await readTokens(db, PEOPLE);
    for (const name of PEOPLE.filter(n => !known[n])) {
        const page = await (await browser.newContext(DEVICES.desktop)).newPage();
        await page.goto(`${origin}/auth/signin?username=${name}`);
        if (new URL(page.url()).pathname !== '/lobby') throw new Error(`Could not create ${name}: ended at ${page.url()}`);
        await page.context().close();
    }
    const tokens = await readTokens(db, ['alice', 'bob', ...PEOPLE]);
    const pages = {};
    const open = async (name, path) => {
        const page = pages[name] ??= await (await browser.newContext(DEVICES.desktop)).newPage();
        await signInWithToken(page, origin, tokens[name], path);
        return page;
    };
    let added = 0;

    const alice = await open('alice', '/lobby');
    await waitForConversation(alice);
    for (const room of ROOMS) {
        if (await alice.locator('.room-item .room-name', { hasText: new RegExp(`^${room.name}$`) }).count()) continue;
        await alice.locator('.room-item.add-room').click();
        await alice.locator('#channel-name').fill(room.name);
        await alice.locator('#channel-description').fill(room.description);
        await alice.locator('.save-button').click();
        await alice.waitForURL(/\/room\//);
        added++;
    }

    const bob = await open('bob', '/settings');
    let changed = false;
    for (const [id, value] of Object.entries(BOB_PROFILE)) {
        const field = bob.locator(`#${id}`);
        if (await field.inputValue() === value) continue;
        await field.fill(value);
        changed = true;
    }
    if (changed) {
        // The page saves the profile half a second after the last keystroke.
        await bob.locator('.save-status.saved', { hasText: 'Saved' }).first().waitFor();
        added++;
    }

    // The old page saves a count only on the reader's existing read row; it never makes
    // one. Without a row the count is gone after a restart. alice got rows for the rooms
    // when she made them; she opens each new direct message once for the same reason.
    // Only a new one: opening it later would read the messages it holds.
    for (const name of new Set(UNREAD.filter(([, where]) => where === '@alice').map(([who]) => who))) {
        const [dm] = await queryDb(db, `SELECT Id FROM Channels WHERE Type = 1
            AND ((Participant1 = ? AND Participant2 = 'alice') OR (Participant1 = 'alice' AND Participant2 = ?))`, [name, name]);
        if (dm) continue;
        await alice.goto(`${origin}/dm/${name}`);
        await waitForConversation(alice);
        added++;
    }

    // alice waits in the lobby, online, while the messages arrive.
    await alice.goto(`${origin}/lobby`);
    await waitForConversation(alice);
    const rooms = Object.fromEntries((await queryDb(db, 'SELECT Name, Id FROM Channels WHERE Type = 0')).map(r => [r.Name, r.Id]));
    for (const [who, where, text] of UNREAD) {
        const path = where.startsWith('#') ? `/room/${rooms[where.slice(1)]}` : `/dm/${where.slice(1)}`;
        const page = pages[who] && new URL(pages[who].url()).pathname === path ? pages[who] : await open(who, path);
        await waitForConversation(page);
        await page.waitForTimeout(1000); // the rows of a room opened after load
        const own = page.locator(`${ROWS}[data-author="${who}"]`, { has: page.locator('.message-content', { hasText: text }) });
        if (await own.count()) continue;
        const before = await page.locator(ROWS).count();
        await sendText(page, text);
        await page.waitForFunction(([sel, n]) => document.querySelectorAll(sel).length > n, [ROWS, before]);
        added++;
    }

    // Both lists exist only in Individual mode; dave's row exists once he wrote. The
    // classes are the state: Blazor writes a true aria-checked or aria-pressed as an
    // empty attribute.
    await alice.goto(`${origin}/settings`);
    for (const [mode, items] of Object.entries(MUTES)) {
        const individual = alice.locator(`.segmented[aria-label="${mode}"] .seg-btn`, { hasText: 'Individual' });
        if (!await individual.evaluate(el => el.classList.contains('seg-selected'))) {
            await individual.click();
            await alice.locator(`.segmented[aria-label="${mode}"] .seg-btn.seg-selected`, { hasText: 'Individual' }).waitFor();
            added++;
        }
        for (const [name, muted] of Object.entries(items)) {
            const item = alice.locator('.notif-item', { has: alice.locator('.notif-item-name', { hasText: new RegExp(`^${name}$`) }) });
            if (await item.locator('.notif-item-toggle').evaluate(el => el.classList.contains('muted')) === muted) continue;
            await item.locator('.notif-item-toggle').click();
            await item.locator(`.notif-item-toggle${muted ? '.muted' : ':not(.muted)'}`).waitFor();
            added++;
        }
    }

    // Last, with everyone's sessions open: alice reads the bot's "just joined" notes.
    await alice.goto(`${origin}/dm/ping`);
    await waitForConversation(alice);
    await alice.waitForTimeout(1000);

    // dave signs out, which ends his session at once, so he shows offline (grey). A
    // closed tab would not do: the old page keeps a lost session for four hours, as Away.
    const dave = pages.dave;
    dave.on('dialog', dialog => dialog.accept());
    await dave.locator('.status-button').click();
    await dave.locator('.status-dropdown .dropdown-item.signout').click();
    await dave.waitForURL(url => !url.pathname.startsWith('/dm/'));

    await Promise.all(Object.values(pages).map(p => p.context().close()));
    console.log(`Frame data: ${added} steps added, the rest were there.`);
}

async function openEverywhere(pages, pathFor) {
    await Promise.all(Object.entries(pages).map(async ([name, page]) => {
        await page.goto(`${origin}${pathFor(name)}`);
        await waitForConversation(page);
    }));
}

// Runs the steps in order. Every page must show a step's result before the next
// step starts, so the order is the order of this list and each sender sees the
// other's last message. A step for which isDone answers true is skipped.
// Returns the number of steps run.
async function runConversation(pages, steps, files, isDone = async () => false) {
    // Wait for join notices and the like to land before rows are counted.
    await pages.alice.waitForTimeout(1500);
    let run = 0;

    for (const [who, step] of steps) {
        const page = pages[who];
        if (await isDone(page, who, step)) continue;
        run++;

        if (step.kind === 'react') {
            const row = rowWithText(page, step.to);
            // The bar shows on hover and fades in; a click during the fade can find the row
            // on top of the button. Hover again and retry.
            for (let attempt = 1; ; attempt++) {
                await row.hover();
                await page.waitForTimeout(300);
                try {
                    await row.locator(`.message-actions .action-btn[title="${step.emoji}"]`).click({ timeout: 5000 });
                    break;
                } catch (e) {
                    if (attempt === 3) throw e;
                }
            }
            // A reaction adds no row: wait until every page shows this person's pill.
            for (const p of Object.values(pages)) await reactionPill(p, who, step).waitFor();
            continue;
        }

        const before = await Promise.all(Object.values(pages).map(p => p.locator(ROWS).count()));
        if (typeof step === 'string') {
            await sendText(page, step);
        } else if (step.kind === 'reply') {
            const row = rowWithText(page, step.to);
            await row.hover();
            await row.locator('.action-reply').click();
            await page.locator('.reply-bar').waitFor();
            await sendText(page, step.text);
        } else if (step.kind === 'pick') {
            const box = page.locator('textarea.message-input');
            await box.fill(`${step.text} `);
            await page.locator('.emoji-toggle-button').click();
            const picker = page.locator('.emoji-picker-container[data-picker-pane="emoji"]');
            // The emoji is also in Recent once it was used: either cell is the same pick.
            await picker.locator(`.emoji-btn[data-emoji="${step.emoji}"]`).first().click();
            await page.keyboard.press('Escape');
            await box.press('Enter');
        } else if (step.kind === 'image') {
            await page.locator(UPLOAD_INPUT).setInputFiles(files.image);
        } else if (step.kind === 'photos') {
            // Several files in one pick make one message.
            await page.locator(UPLOAD_INPUT).setInputFiles(files.photos);
        } else if (step.kind === 'gif') {
            await page.locator('.gif-toggle-button').click();
            const picker = page.locator('.emoji-picker-container[data-picker-pane="gif"]');
            await picker.waitFor({ state: 'visible' });
            await picker.locator('.gif-upload-btn input[type=file]').setInputFiles(files.gif);
        }
        await Promise.all(Object.values(pages).map((p, i) => p.waitForFunction(
            ([sel, n]) => document.querySelectorAll(sel).length > n, [ROWS, before[i]], { timeout: 30000 })));
    }
    return run;
}

// Is the step already in the open conversation? Text matches without its emoji,
// because the page shows emoji as images.
async function isDone(page, who, step) {
    if (step.kind === 'react') return await reactionPill(page, who, step).count() > 0;
    const own = page.locator(`${ROWS}[data-author="${who}"]`);
    // Any GIF from the uploader counts: the upload made it a favourite.
    if (step.kind === 'gif') return await own.locator('.gif-message').count() > 0;
    if (step.kind === 'photos') return await own.evaluateAll(rows => rows.some(r => r.querySelectorAll('.gallery-item').length > 1));
    const text = typeof step === 'string' ? step : step.text;
    return await own.filter({ has: page.locator('.message-content', { hasText: text }) }).count() > 0;
}

async function sendText(page, text) {
    const box = page.locator('textarea.message-input');
    await box.fill(text);
    await box.press('Enter');
}

function rowWithText(page, text) {
    return page.locator(ROWS, { has: page.locator('.message-content', { hasText: text }) }).last();
}

// The pill of one person's reaction: its tooltip lists the people who reacted.
function reactionPill(page, who, step) {
    return rowWithText(page, step.to)
        .locator(`.reaction-pill[title*="${who}"]`, { has: page.locator(`img[alt="${step.emoji}"]`) });
}

// A generated "photo" of a mountain ridge, so the seed needs no binary file.
async function drawRidgePhoto(browser, path) {
    const page = await browser.newPage({ viewport: { width: 960, height: 640 } });
    await page.setContent(`<!doctype html><style>
        body { margin: 0; width: 960px; height: 640px; overflow: hidden; position: relative;
               background: linear-gradient(#5b8fd6 0%, #a9cbef 45%, #f4d9b0 70%, #e9b483 100%); }
        div { position: absolute; left: 0; right: 0; bottom: 0; }
        .sun { left: 640px; top: 300px; width: 90px; height: 90px; border-radius: 50%; background: #fff3d1;
               box-shadow: 0 0 60px 30px rgba(255, 236, 190, .6); bottom: auto; right: auto; }
        .far  { height: 330px; background: #7f93b5; clip-path: polygon(0 55%, 12% 35%, 22% 48%, 35% 20%, 47% 42%, 58% 28%, 70% 50%, 82% 30%, 100% 45%, 100% 100%, 0 100%); }
        .mid  { height: 260px; background: #4f6a86; clip-path: polygon(0 40%, 15% 60%, 28% 30%, 40% 55%, 55% 25%, 68% 52%, 80% 38%, 92% 58%, 100% 48%, 100% 100%, 0 100%); }
        .near { height: 170px; background: #2e4a3c; clip-path: polygon(0 30%, 20% 50%, 38% 25%, 52% 45%, 66% 35%, 84% 60%, 100% 40%, 100% 100%, 0 100%); }
        .path { height: 170px; background: #a98d6a; clip-path: polygon(46% 100%, 50% 62%, 52% 45%, 53% 45%, 55% 64%, 60% 100%); opacity: .8; }
    </style><div class="sun"></div><div class="far"></div><div class="mid"></div><div class="near"></div><div class="path"></div>`);
    await page.screenshot({ path, type: 'jpeg', quality: 85 });
    await page.close();
    return path;
}

// Two generated PNG "photos" from the hike, for the message with two images.
async function drawHutPhotos(browser, dir) {
    const scenes = {
        'hut.png': `
            body { background: linear-gradient(#6fa3dc, #cfe3f4 70%); }
            .peak { left: 0; right: 0; bottom: 120px; height: 300px; background: #8a9bb4;
                    clip-path: polygon(0 70%, 30% 15%, 45% 40%, 62% 5%, 100% 60%, 100% 100%, 0 100%); }
            .meadow { left: 0; right: 0; bottom: 0; height: 170px; background: linear-gradient(#5f8f4e, #3f6b35); }
            .hut { left: 380px; bottom: 110px; width: 150px; height: 90px; background: #7a4a2b; }
            .roof { left: 360px; bottom: 195px; width: 190px; height: 70px; background: #3b2a22;
                    clip-path: polygon(50% 0, 100% 100%, 0 100%); }
            .window { left: 470px; bottom: 140px; width: 30px; height: 26px; background: #ffd77a; }
            .door { left: 405px; bottom: 110px; width: 34px; height: 56px; background: #4a2c1a; }`,
        'lake.png': `
            body { background: linear-gradient(#f1a65f, #fbe0bd 55%, #4e7898 55%, #2d4f6b); }
            .range { left: 0; right: 0; top: 150px; height: 114px; background: #3d4e63;
                     clip-path: polygon(0 60%, 18% 20%, 30% 45%, 48% 0, 63% 40%, 78% 15%, 100% 55%, 100% 100%, 0 100%); }
            .mirror { left: 0; right: 0; top: 264px; height: 114px; background: #3d4e63; opacity: .45; transform: scaleY(-1);
                      clip-path: polygon(0 60%, 18% 20%, 30% 45%, 48% 0, 63% 40%, 78% 15%, 100% 55%, 100% 100%, 0 100%); }
            .sun { left: 450px; top: 90px; width: 60px; height: 60px; border-radius: 50%; background: #fff1cf; }`,
    };
    const paths = [];
    for (const [name, css] of Object.entries(scenes)) {
        const page = await browser.newPage({ viewport: { width: 640, height: 480 } });
        const divs = [...css.matchAll(/^\s*\.([a-z]+) \{/gm)].map(m => `<div class="${m[1]}"></div>`).join('');
        await page.setContent(`<!doctype html><style>body { margin: 0; height: 480px; overflow: hidden; position: relative; }
            div { position: absolute; } ${css}</style>${divs}`);
        const path = join(dir, name);
        await page.screenshot({ path });
        await page.close();
        paths.push(path);
    }
    return paths;
}

// A generated animated GIF (the sun rises and sets behind the same ridge), encoded
// here so the seed needs no binary file and no image tool. 160 x 120, 16 colours.
function writeSunriseGif(path) {
    const W = 160, H = 120, FRAMES = 16;
    const PALETTE = [
        0x5b8fd6, 0x74a2de, 0x8fb6e6, 0xabc9ec, 0xcfd9e4, 0xf4d9b0, // sky, top to horizon
        0xffeec4, 0xfffaea,                                         // sun glow, sun
        0x7f93b5, 0x4f6a86, 0x2e4a3c,                               // far, middle, near ridge
        0, 0, 0, 0, 0,                                              // unused: the table holds 16
    ];
    // A ridge line: the y of the ridge at x, linear between [x, y] points.
    const ridge = points => x => {
        const i = points.findIndex(([px]) => px >= x);
        if (i <= 0) return points[0][1];
        const [[x0, y0], [x1, y1]] = [points[i - 1], points[i]];
        return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
    };
    const far = ridge([[0, 66], [19, 58], [35, 63], [56, 50], [75, 60], [93, 54], [112, 63], [131, 55], [160, 61]]);
    const mid = ridge([[0, 78], [24, 86], [45, 72], [64, 83], [88, 70], [109, 82], [128, 76], [147, 86], [160, 81]]);
    const near = ridge([[0, 96], [32, 103], [61, 93], [83, 100], [106, 96], [134, 106], [160, 98]]);
    const colour = (x, y, sunY) => {
        if (y >= near(x)) return 10;
        if (y >= mid(x)) return 9;
        if (y >= far(x)) return 8;
        const d = Math.hypot(x - 112, y - sunY);
        if (d < 10) return 7;
        if (d < 15) return 6;
        return Math.min(5, Math.floor(y / 11));
    };

    // LZW with a clear code after every 12 pixels: the code table never grows past
    // 31 entries, so every code stays 5 bits wide and any decoder reads the stream.
    // Larger than real LZW, but the file stays near 200 KB.
    const encode = pixels => {
        const bytes = [];
        let acc = 0, bits = 0;
        const put = code => {
            acc |= code << bits;
            bits += 5;
            while (bits >= 8) { bytes.push(acc & 0xff); acc >>= 8; bits -= 8; }
        };
        pixels.forEach((p, i) => { if (i % 12 === 0) put(16); put(p); });
        put(17);
        if (bits > 0) bytes.push(acc & 0xff);
        return bytes;
    };

    const u16 = n => [n & 0xff, n >> 8];
    const out = [...Buffer.from('GIF89a'), ...u16(W), ...u16(H), 0xb3, 0, 0];
    for (const c of PALETTE) out.push(c >> 16, (c >> 8) & 0xff, c & 0xff);
    out.push(0x21, 0xff, 0x0b, ...Buffer.from('NETSCAPE2.0'), 3, 1, 0, 0, 0); // loop forever
    for (let f = 0; f < FRAMES; f++) {
        const sunY = 62 - 22 * Math.cos(2 * Math.PI * f / FRAMES); // up in the first frame
        const pixels = [];
        for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) pixels.push(colour(x, y, sunY));
        out.push(0x21, 0xf9, 4, 0x04, ...u16(12), 0, 0); // 0.12 s per frame
        out.push(0x2c, 0, 0, 0, 0, ...u16(W), ...u16(H), 0, 4);
        const data = encode(pixels);
        for (let i = 0; i < data.length; i += 255) {
            const block = data.slice(i, i + 255);
            out.push(block.length, ...block);
        }
        out.push(0);
    }
    out.push(0x3b);
    writeFileSync(path, Buffer.from(out));
    return path;
}
