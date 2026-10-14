// Seeds a fresh Yap instance with the data the spec screenshots show:
// accounts alice and bob, a conversation in #lobby (one image, one reply, one
// reaction) and a short direct message thread.
//
// Everything goes through the real page, the way a user does it, so the script
// works on any version of the app without knowing its internals.
//
//   node seed.mjs --origin http://127.0.0.1:7651 --db <app>/Data/yap.db

import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium, DEVICES, parseArgs, readTokens } from './common.mjs';

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

if (!db) throw new Error('Pass --db <app>/Data/yap.db so the seed can see whether it already ran.');
const existing = await readTokens(db, ['alice', 'bob']);
if (Object.keys(existing).length) {
    console.log(`Already seeded (${Object.keys(existing).join(', ')} exist). Delete the app's Data/yap.db for a fresh seed.`);
    process.exit(0);
}

const work = mkdtempSync(join(tmpdir(), 'yap-seed-'));
const browser = await chromium.launch();
try {
    const imagePath = await drawRidgePhoto(browser, join(work, 'ridge.jpg'));

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

    await runConversation(pages, LOBBY, imagePath);

    await Promise.all([pages.alice.goto(`${origin}/dm/bob`), pages.bob.goto(`${origin}/dm/alice`)]);
    await Promise.all(Object.values(pages).map(p => p.locator('textarea.message-input').waitFor()));
    await runConversation(pages, DM, imagePath);

    // The bot sends every new account a welcome message one minute after sign-up.
    // Wait for it with the bot conversation open, so it arrives read and no unread
    // badge shows in the screenshots.
    await Promise.all(Object.values(pages).map(async page => {
        await page.goto(`${origin}/dm/ping`);
        await page.locator('.message-content', { hasText: /welcome/i }).first().waitFor({ timeout: 120000 });
    }));

    console.log(`Seeded ${origin}: alice, bob, ${LOBBY.length} lobby steps, ${DM.length} direct messages.`);
} finally {
    await browser.close();
    rmSync(work, { recursive: true, force: true });
}

async function runConversation(pages, steps, imagePath) {
    // Wait for join notices and the like to land, then count from there on each page.
    await pages.alice.waitForTimeout(1500);
    const base = {};
    for (const [name, page] of Object.entries(pages)) base[name] = await page.locator(ROWS).count();
    let sent = 0;

    for (const [who, step] of steps) {
        const page = pages[who];
        if (typeof step === 'string') {
            await sendText(page, step);
        } else if (step.kind === 'reply') {
            const row = rowWithText(page, step.to);
            await row.hover();
            await row.locator('.action-reply').click();
            await page.locator('.reply-bar').waitFor();
            await sendText(page, step.text);
        } else if (step.kind === 'react') {
            const row = rowWithText(page, step.to);
            await row.hover();
            await row.locator(`.message-actions .action-btn[title="${step.emoji}"]`).click();
            await row.locator('.reaction-pill').waitFor();
            continue; // a reaction adds no row
        } else if (step.kind === 'image') {
            await page.locator('.image-upload-button input[type=file]').setInputFiles(imagePath);
        }
        sent++;
        // Every page must show the row before the next step, so the order is the
        // order of this list and each sender sees the other's last message.
        for (const [name, p] of Object.entries(pages)) {
            await p.waitForFunction(([sel, n]) => document.querySelectorAll(sel).length >= n, [ROWS, base[name] + sent], { timeout: 30000 });
        }
    }
}

async function sendText(page, text) {
    const box = page.locator('textarea.message-input');
    await box.fill(text);
    await box.press('Enter');
}

function rowWithText(page, text) {
    return page.locator(ROWS, { has: page.locator('.message-content', { hasText: text }) }).last();
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
