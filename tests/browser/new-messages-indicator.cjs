// Emulated phone: the view keeps its place while the user reads older messages, a "New
// messages" pill appears when someone else posts, and a tap on it goes to the bottom. A tap
// with a small finger movement must not release follow: it did, and new messages then landed
// below the fold with no sign (audit C-01, decision C-03).
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { joinPhone, origin } = require('./support/phone.cjs');

async function joinDesktop(browser) {
    const context = await browser.newContext({ serviceWorkers: 'block' });
    const page = await context.newPage();
    await page.goto(origin + '/login');
    await page
        .locator('.username-input')
        .fill('oth' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5));
    await page.locator('.join-button').click();
    await page.waitForURL('**/lobby');
    await page.locator('#draft:not([disabled])').waitFor();
    await page.waitForFunction(() =>
        document.querySelector('#connection')?.textContent.startsWith('Synced'),
    );
    return { context, page };
}

const row = (page, text) =>
    page
        .locator('#timeline .message-group')
        .filter({ has: page.locator('.message-content', { hasText: text }) })
        .last();

async function post(page, text) {
    await page.locator('#draft').fill(text);
    await page.locator('#send').click();
    await row(page, text).waitFor();
}

const scrollState = (page) =>
    page.evaluate(() => {
        const s = document.querySelector('.messages');
        return {
            top: s.scrollTop,
            gap: s.scrollHeight - s.clientHeight - s.scrollTop,
            pill: !document.querySelector('#new-messages').hidden,
        };
    });

(async () => {
    const browser = await chromium.launch();
    try {
        const { context, page, errors } = await joinPhone(browser, { prefix: 'ind' });
        const other = await joinDesktop(browser);
        try {
            const stamp = Date.now().toString(36);
            // Enough rows to scroll at 844 px.
            for (let i = 1; i <= 16; i++)
                await post(other.page, `Filler ${i} ${stamp}\nsecond line`);
            await row(page, `Filler 16 ${stamp}`).waitFor();
            await page.waitForTimeout(500);
            let state = await scrollState(page);
            assert(state.gap < 2, `follows the bottom after the fill: ${JSON.stringify(state)}`);
            assert(state.top > 100, `the lobby scrolls on the phone: ${JSON.stringify(state)}`);
            // A tap with a small finger movement on a row (a finger is never perfectly still).
            // Synthetic events: Chromium keeps a 6 px CDP move inside its touch slop and never
            // delivers it to the page, while a phone browser does deliver such a touchmove.
            await row(page, `Filler 14 ${stamp}`)
                .locator('.message-content')
                .evaluate((node) => {
                    const touch = (y) =>
                        new Touch({ identifier: 1, target: node, clientX: 100, clientY: y });
                    const fire = (type, y) =>
                        node.dispatchEvent(
                            new TouchEvent(type, {
                                touches: type === 'touchend' ? [] : [touch(y)],
                                changedTouches: [touch(y)],
                                bubbles: true,
                                cancelable: true,
                            }),
                        );
                    fire('touchstart', 400);
                    fire('touchmove', 406);
                    fire('touchend', 406);
                });
            await post(other.page, `After the nudge ${stamp}`);
            await row(page, `After the nudge ${stamp}`).waitFor();
            await page.waitForTimeout(500);
            state = await scrollState(page);
            assert(
                state.gap < 2 && !state.pill,
                `still follows after a tap with a small movement: ${JSON.stringify(state)}`,
            );
            // Reading older messages: the view keeps its place and the pill appears.
            await page.evaluate(() => (document.querySelector('.messages').scrollTop -= 300));
            await page.waitForTimeout(300);
            const before = await scrollState(page);
            assert(before.gap > 200, `scrolled up: ${JSON.stringify(before)}`);
            await post(other.page, `While reading ${stamp}`);
            await row(page, `While reading ${stamp}`).waitFor();
            await page.waitForTimeout(500);
            state = await scrollState(page);
            assert(
                Math.abs(state.top - before.top) < 4 && state.pill,
                `keeps the place and shows the pill: before ${JSON.stringify(before)}, after ${JSON.stringify(state)}`,
            );
            await page.locator('#new-messages').tap();
            await page.waitForTimeout(500);
            state = await scrollState(page);
            assert(
                state.gap < 2 && !state.pill,
                `the pill goes to the bottom and hides: ${JSON.stringify(state)}`,
            );
            assert.deepEqual(errors, []);
            console.log(
                'PASS 390x844 touch: a tap with a small movement keeps following; reading older messages keeps the place and shows the New messages pill; the pill goes to the bottom',
            );
        } finally {
            await other.context.close();
            await context.close();
        }
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
