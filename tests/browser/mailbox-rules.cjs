// The mailbox count is the sum of the counts the sidebar rows show. The open conversation,
// room or DM, never counts: an Away session keeps the open conversation's arrivals unread on
// the server (NOTIF-03), its row shows no count, and a mailbox that counted it showed one
// unread that no row carried (user report, 2026-10-12). The old header excluded only the DM.
// A new account mutes every room (NotifRoomMode defaults to MuteAll) and a muted conversation
// never counts, so the reader first allows room notifications, as the reporting account had.
const { readSnapshot } = require('./support/authority.cjs');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { origin } = require('./support/phone.cjs');

const pause = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(check, what) {
    const end = Date.now() + 30000;
    while (Date.now() < end) {
        if (await check()) return;
        await pause(100);
    }
    throw new Error('Timed out waiting for ' + what);
}

(async () => {
    const browser = await chromium.launch();
    try {
        const names = ['ma', 'mb'].map(
            (n) => n + Date.now().toString(36) + Math.random().toString(36).slice(2, 4),
        );
        const contexts = [];
        const pages = [];
        const errors = [];
        const synced = async (page) => {
            await page.locator('#draft:not([disabled])').waitFor();
            await page.waitForFunction(() =>
                document.querySelector('#connection')?.textContent.startsWith('Synced'),
            );
        };
        for (const name of names) {
            const context = await browser.newContext({
                serviceWorkers: 'block',
                viewport: { width: 1440, height: 1000 },
            });
            const page = await context.newPage();
            page.on('pageerror', (error) => errors.push(error.message));
            await page.goto(origin + '/login');
            await page.locator('.username-input').fill(name);
            await page.locator('.join-button').click();
            await page.waitForURL('**/lobby');
            await synced(page);
            contexts.push(context);
            pages.push(page);
        }
        const [alice, bob] = pages;
        // Rooms notify, as on the reporting account.
        await alice.goto(origin + '/settings#notification-settings');
        const allow = alice
            .getByRole('radiogroup', { name: 'Rooms', exact: true })
            .getByRole('radio', { name: 'Allow all', exact: true });
        await allow.click();
        await until(
            () => allow.evaluate((button) => button.classList.contains('seg-selected')),
            'the room mode change',
        );
        await alice.goto(origin + '/lobby');
        await synced(alice);
        await until(
            () =>
                alice.evaluate(async () => {
                    const { readState } = await import('/chat-client/storage.js');
                    const state = await readState();
                    const lobby = state?.snapshot?.conversations.find((c) => c.path === '/lobby');
                    return lobby?.muted === false;
                }),
            'the unmuted lobby on the client',
        );
        const status = async (page, value) => {
            await page.locator('#menu-button').click();
            await page.locator(`[data-status="${value}"]`).click();
            await page.locator('#menu-button.status-' + value).waitFor();
        };
        const send = async (text) => {
            await bob.locator('#draft').fill(text);
            await bob.locator('#send').click();
            await until(
                () =>
                    alice
                        .locator('#timeline .message-text')
                        .filter({ hasText: text })
                        .count()
                        .then((n) => n === 1),
                'the arrival on the reader',
            );
        };
        const serverUnread = async (path) => {
            const s = await readSnapshot(contexts[0].request, origin);
            return s.conversations.find((c) => c.path === path).unread;
        };
        // What the rows show, and what the mailbox shows, read together.
        const shown = () =>
            alice.evaluate(() => ({
                rows: [...document.querySelectorAll('#rooms .unread-badge, #dms .unread-badge')]
                    .map((n) => Number(n.textContent))
                    .reduce((a, b) => a + b, 0),
                dots: document.querySelectorAll('#rooms .unread-dot, #dms .unread-dot').length,
                mailbox: document.querySelector('#mailbox').hidden
                    ? 0
                    : Number(document.querySelector('#mailbox-count').textContent),
            }));

        // Away in the open room: the arrival stays unread on the server, the row shows no count
        // and the mailbox shows none either.
        await status(alice, 'away');
        await send('away arrival in the open room ' + names[0]);
        await until(async () => (await serverUnread('/lobby')) === 1, 'the Away room unread');
        await pause(500);
        assert.deepEqual(
            await shown(),
            { rows: 0, dots: 0, mailbox: 0 },
            'the open room counts nowhere while Away',
        );
        console.log('PASS the mailbox never counts the open room');
        // Back Online, the open room is read.
        await status(alice, 'online');
        await until(async () => (await serverUnread('/lobby')) === 0, 'the resume read');
        assert.deepEqual(await shown(), { rows: 0, dots: 0, mailbox: 0 });

        // A DM arrival while the lobby is open: the row and the mailbox agree.
        await bob.goto(origin + '/dm/' + names[0]);
        await bob.locator('#draft:not([disabled])').waitFor();
        const dmRow = alice.locator(`#dms [data-username="${names[1]}"]`);
        await bob.locator('#draft').fill('dm arrival ' + names[0]);
        await bob.locator('#send').click();
        await dmRow.locator('.unread-badge').waitFor();
        await until(
            async () => (await shown()).mailbox === 1,
            'the mailbox to show the DM arrival',
        );
        assert.deepEqual(await shown(), { rows: 1, dots: 0, mailbox: 1 });
        console.log('PASS the mailbox equals the sum of the row counts');

        // Away in the open DM: the arrival counts nowhere, as before the change.
        await dmRow.click();
        await alice.waitForURL('**/dm/' + names[1]);
        await until(async () => (await serverUnread('/dm/' + names[1])) === 0, 'the DM open read');
        await status(alice, 'away');
        await bob.locator('#draft').fill('away arrival in the open dm ' + names[0]);
        await bob.locator('#send').click();
        await until(
            async () => (await serverUnread('/dm/' + names[1])) === 1,
            'the Away DM unread',
        );
        await pause(500);
        assert.deepEqual(
            await shown(),
            { rows: 0, dots: 0, mailbox: 0 },
            'the open DM counts nowhere while Away',
        );
        await status(alice, 'online');
        await until(
            async () => (await serverUnread('/dm/' + names[1])) === 0,
            'the DM resume read',
        );
        console.log('PASS the mailbox never counts the open DM');
        assert.deepEqual(errors, []);
        console.log(
            'PASS mailbox rules: the count is the sum of the row counts, open conversation excluded',
        );
        for (const context of contexts) await context.close();
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
