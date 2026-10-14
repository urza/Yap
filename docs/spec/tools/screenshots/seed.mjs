// Seeds a fresh Yap instance with the data the spec screenshots show:
// accounts alice and bob, a conversation in #lobby (one image, one reply with
// reactions, one other reaction), a direct message thread, recent emoji for alice
// and one GIF that alice uploaded (her favourite).
//
// Everything goes through the real page, the way a user does it, so the script
// works on any version of the app without knowing its internals.
//
//   node seed.mjs --origin http://127.0.0.1:7651 --db <app>/Data/yap.db
//
// The base seed runs once, when alice and bob do not exist. The additions after
// it run every time and skip each step that is already there, so an instance
// seeded before they existed gets them too.

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium, DEVICES, parseArgs, readTokens, signInWithToken, waitForConversation } from './common.mjs';

const { origin, db } = parseArgs(process.argv.slice(2));

// Rows the server accepted. Pending copies live in .pending-echoes, not here.
const ROWS = '.messages-flow > .message-group';

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
// a Favorites tile and a card to show.
const DM_MORE = [
    ['bob', 'so is there cake on saturday?'],
    ['alice', 'of course, a whole one'],
    ['alice', { kind: 'pick', text: 'we can eat it at the hut', emoji: '🎂' }],
    ['alice', { kind: 'pick', text: 'if it survives the trail', emoji: '😅' }],
    ['bob', "I'll carry it like a baby"],
    ['alice', { kind: 'gif' }],
];

// Reactions on bob's reply (the reply row image): two pills, and for alice only the
// second one is her own, so one pill is highlighted and one is not. alice's reaction
// is also her newest recent emoji.
const LOBBY_MORE = [
    ['bob', { kind: 'react', to: 'no cucumber in mine please', emoji: '👍' }],
    ['alice', { kind: 'react', to: 'no cucumber in mine please', emoji: '😂' }],
    ['bob', { kind: 'react', to: 'no cucumber in mine please', emoji: '😂' }],
];

if (!db) throw new Error('Pass --db <app>/Data/yap.db so the seed can see whether it already ran.');
const existing = await readTokens(db, ['alice', 'bob']);

const work = mkdtempSync(join(tmpdir(), 'yap-seed-'));
const browser = await chromium.launch();
try {
    const files = {
        image: await drawRidgePhoto(browser, join(work, 'ridge.jpg')),
        gif: writeSunriseGif(join(work, 'sunrise-over-the-ridge.gif')),
    };
    if (Object.keys(existing).length) {
        console.log(`Base seed skipped (${Object.keys(existing).join(', ')} exist). Delete the app's Data/yap.db for a fresh seed.`);
    } else {
        await seedBase(browser, files);
    }
    await seedAdditions(browser, files);
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

    await Promise.all([pages.alice.goto(`${origin}/dm/bob`), pages.bob.goto(`${origin}/dm/alice`)]);
    await Promise.all(Object.values(pages).map(p => p.locator('textarea.message-input').waitFor()));
    await runConversation(pages, DM, files);

    // The bot sends every new account a welcome message one minute after sign-up.
    // Wait for it with the bot conversation open, so it arrives read and no unread
    // badge shows in the screenshots.
    await Promise.all(Object.values(pages).map(async page => {
        await page.goto(`${origin}/dm/ping`);
        await page.locator('.message-content', { hasText: /welcome/i }).first().waitFor({ timeout: 120000 });
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
            await row.hover();
            await row.locator(`.message-actions .action-btn[title="${step.emoji}"]`).click();
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
            await page.locator('.image-upload-button input[type=file]').setInputFiles(files.image);
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
