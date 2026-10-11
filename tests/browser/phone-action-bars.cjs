// Emulated phone: one message row at a time shows its action bar.
// Regression (an iPhone photo): a touch browser keeps a tapped row in :hover until the next tap,
// and the tap state (.touch-actions) moves with every touch, a scroll nudge included, so the hover
// reveal and the tap reveal showed two bars at once. The hover reveal now applies only to devices
// that can hover. CDP forces :hover on the first row, the way DevTools does, to model the sticky
// hover; the emulated media features make the phone input profile explicit.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { joinPhone } = require('./support/phone.cjs');

// Ids of the rows whose action bar is visible right now.
const visibleBars = (page) =>
    page.evaluate(() =>
        [...document.querySelectorAll('#timeline .message-group')]
            .filter((row) => {
                const bar = row.querySelector('.message-actions');
                if (!bar) return false;
                const s = getComputedStyle(bar);
                return s.display !== 'none' && s.opacity !== '0';
            })
            .map((row) => row.id),
    );

async function sendAndFind(page, text) {
    await page.locator('#draft').fill(text);
    await page.locator('#send').tap();
    const byText = page
        .locator('#timeline .message-group[id^="msg-"]')
        .filter({ has: page.locator('.message-content', { hasText: text }) })
        .last();
    await byText.waitFor();
    const id = await byText.getAttribute('id');
    return { id, row: page.locator('#' + id) };
}

(async () => {
    const browser = await chromium.launch();
    try {
        const { context, page, errors } = await joinPhone(browser, { prefix: 'bars' });
        try {
            const cdp = await context.newCDPSession(page);
            await cdp.send('Emulation.setEmulatedMedia', {
                features: [
                    { name: 'hover', value: 'none' },
                    { name: 'pointer', value: 'coarse' },
                ],
            });
            assert.equal(await page.evaluate(() => matchMedia('(hover: none)').matches), true);
            const stamp = Date.now().toString(36);
            const first = await sendAndFind(page, 'First row ' + stamp);
            const second = await sendAndFind(page, 'Second row ' + stamp);
            await first.row.locator('.message-content').tap();
            // The bar fades in over 0.1s.
            await page.waitForTimeout(300);
            assert.deepEqual(await visibleBars(page), [first.id], 'after a tap on the first row');
            // iOS keeps the tapped row in :hover until the next tap. Force it on the first row.
            await cdp.send('DOM.enable');
            await cdp.send('CSS.enable');
            const { root } = await cdp.send('DOM.getDocument', { depth: 0 });
            const { nodeId } = await cdp.send('DOM.querySelector', {
                nodeId: root.nodeId,
                selector: '#' + first.id,
            });
            await cdp.send('CSS.forcePseudoState', { nodeId, forcedPseudoClasses: ['hover'] });
            await second.row.locator('.message-content').tap();
            await page.waitForTimeout(300);
            assert.deepEqual(
                await visibleBars(page),
                [second.id],
                'after a touch on the second row while the first row is still hovered',
            );
            // A touch outside the messages hides every bar, the hovered row included. The room
            // title always has a size; the spacer above the rows has none once the lobby fills.
            await page.locator('#title').tap();
            await page.waitForTimeout(500);
            assert.deepEqual(await visibleBars(page), [], 'after a touch outside the messages');
            // The bar appears after the tap, never under the finger. A tap on the spot where
            // the row's bar will show must reveal the bar and press none of its buttons. A bar
            // shown at pointerdown took that tap's click (Edit or the reaction picker opened).
            const spot = await second.row.evaluate((row) => {
                const bar = row.querySelector('.message-actions');
                row.classList.add('touch-actions');
                const b = bar.getBoundingClientRect(),
                    r = row.getBoundingClientRect();
                row.classList.remove('touch-actions');
                // Inside the first bar button and inside the row's own box.
                return { x: b.left + 14, y: Math.max(b.bottom - 6, r.top + 2) };
            });
            assert(
                await page.evaluate(
                    ({ x, y }) => !document.elementFromPoint(x, y)?.closest('.message-actions'),
                    spot,
                ),
                'the hidden bar does not take hits at the spot',
            );
            await page.touchscreen.tap(spot.x, spot.y);
            await page.waitForTimeout(500);
            const fired = await second.row.evaluate((row) => ({
                bar: row.classList.contains('touch-actions'),
                reaction: !!row.querySelector('.reaction-pill'),
                editor: !!row.querySelector('.edit-input'),
                picker: !!document.querySelector('.client-reaction-picker'),
                menu: !!document.querySelector('.more-menu'),
                reply: !document.querySelector('#reply-bar').hidden,
            }));
            assert.deepEqual(
                fired,
                {
                    bar: true,
                    reaction: false,
                    editor: false,
                    picker: false,
                    menu: false,
                    reply: false,
                },
                'a tap where the bar appears shows the bar and presses nothing',
            );
            assert.deepEqual(errors, []);
            console.log(
                'PASS 390x844 touch: one action bar at a time, with a sticky :hover on another row; a touch outside hides it; a tap where the bar appears presses nothing',
            );
        } finally {
            await context.close();
        }
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
