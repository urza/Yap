// Composer behaviours restored from the pre-rewrite page (audit B-01, B-02, B-03, B-06, B-12).
// Desktop: Send clears the draft in the click's own task and is never disabled for text or
// during a send; a whitespace draft sends nothing; a file drop outside the composer is
// cancelled; the upload warning modal moves no focus. Phone: with the emoji picker open, the
// + button and the toggles sit under the backdrop while the draft and Send stay reachable.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { joinPhone, origin } = require('./support/phone.cjs');

async function joinDesktop(browser) {
    const context = await browser.newContext({ serviceWorkers: 'block' });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.goto(origin + '/login');
    await page
        .locator('.username-input')
        .fill('cr' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5));
    await page.locator('.join-button').click();
    await page.waitForURL('**/lobby');
    await page.locator('#draft:not([disabled])').waitFor();
    await page.waitForFunction(() =>
        document.querySelector('#connection')?.textContent.startsWith('Synced'),
    );
    return { context, page, errors };
}

const rowCount = (page) =>
    page.locator('#timeline .message-group, #pending .message-group').count();

(async () => {
    const browser = await chromium.launch();
    try {
        const desktop = await joinDesktop(browser);
        try {
            const { page } = desktop;
            const text = 'Cleared at once ' + Date.now().toString(36);
            // The clear and the button state are read in the same task as the click.
            const sent = await page.evaluate((text) => {
                const draft = document.querySelector('#draft'),
                    send = document.querySelector('#send');
                draft.value = text;
                draft.dispatchEvent(new Event('input', { bubbles: true }));
                const before = send.disabled;
                send.click();
                return { before, after: draft.value, disabledAfter: send.disabled };
            }, text);
            assert.deepEqual(
                sent,
                { before: false, after: '', disabledAfter: false },
                'Send clears the draft at once and stays enabled',
            );
            await page
                .locator('#timeline .message-group')
                .filter({ has: page.locator('.message-content', { hasText: text }) })
                .waitFor();
            // Whitespace: the button is not disabled, and the click sends nothing.
            const rows = await rowCount(page);
            const blank = await page.evaluate(() => {
                const draft = document.querySelector('#draft'),
                    send = document.querySelector('#send');
                draft.value = '   ';
                draft.dispatchEvent(new Event('input', { bubbles: true }));
                const disabled = send.disabled;
                send.click();
                return { disabled, value: draft.value };
            });
            await page.waitForTimeout(800);
            assert.deepEqual(
                blank,
                { disabled: false, value: '   ' },
                'whitespace keeps Send live',
            );
            assert.equal(await rowCount(page), rows, 'a whitespace draft sends nothing');
            await page.locator('#draft').fill('');
            // A drop outside the composer is cancelled (the browser would open the file).
            const prevented = await page.evaluate(() => {
                const transfer = new DataTransfer();
                transfer.items.add(new File(['x'], 'a.png', { type: 'image/png' }));
                const target = document.querySelector('.chat-header');
                const over = !target.dispatchEvent(
                    new DragEvent('dragover', {
                        dataTransfer: transfer,
                        bubbles: true,
                        cancelable: true,
                    }),
                );
                const drop = !target.dispatchEvent(
                    new DragEvent('drop', {
                        dataTransfer: transfer,
                        bubbles: true,
                        cancelable: true,
                    }),
                );
                return { over, drop };
            });
            assert.deepEqual(prevented, { over: true, drop: true }, 'drop on the header cancelled');
            // The warning modal moves no focus.
            await page.focus('#draft');
            const focus = await page.evaluate(async () => {
                const { warn } = await import('/chat-client/warnings.js');
                warn('Test warning', 'body');
                const during = document.activeElement?.id;
                document.querySelector('.warning-modal-button').click();
                return {
                    during,
                    after: document.activeElement?.id,
                    gone: !document.querySelector('.warning-modal-backdrop'),
                };
            });
            assert.deepEqual(
                focus,
                { during: 'draft', after: 'draft', gone: true },
                'the warning modal leaves focus where it was',
            );
            assert.deepEqual(desktop.errors, []);
            console.log(
                'PASS desktop: synchronous clear, Send never disabled for text, whitespace sends nothing, outside drop cancelled, warning keeps focus',
            );
        } finally {
            await desktop.context.close();
        }
        const phone = await joinPhone(browser, { prefix: 'cr' });
        try {
            const { page } = phone;
            await page.locator('#draft').fill('under the sheet');
            await page.evaluate(() => {
                window.__chooser = 0;
                document
                    .querySelector('#upload-files')
                    .addEventListener('click', () => window.__chooser++);
            });
            await page.locator('#emoji-button').tap();
            await page.waitForFunction(
                () => !!document.querySelector('.message-input-container').dataset.picker,
            );
            await page.waitForTimeout(300);
            const hits = await page.evaluate(() => {
                const at = (selector) => {
                    const r = document.querySelector(selector).getBoundingClientRect();
                    const n = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                    // The hit can be a button's SVG icon; name the control it belongs to.
                    const hit = n?.closest('#send, #draft, #upload-button, .emoji-picker-backdrop');
                    return hit?.id || hit?.className || n?.tagName;
                };
                return { upload: at('#upload-button'), draft: at('#draft'), send: at('#send') };
            });
            const where = JSON.stringify(hits);
            assert.equal(hits.upload, 'emoji-picker-backdrop', `+ under the backdrop: ${where}`);
            assert.equal(hits.draft, 'draft', `the draft stays reachable: ${where}`);
            assert.equal(hits.send, 'send', `Send stays reachable: ${where}`);
            // A tap on + only closes the picker.
            await page.locator('#upload-button').tap({ force: true });
            await page.waitForTimeout(300);
            const after = await page.evaluate(() => ({
                picker: document.querySelector('.message-input-container').dataset.picker,
                chooser: window.__chooser,
            }));
            assert.deepEqual(
                after,
                { picker: undefined, chooser: 0 },
                'the tap on + only closed the picker',
            );
            assert.deepEqual(phone.errors, []);
            console.log(
                'PASS 390x844 touch: + and the toggles sit under the picker backdrop; a tap on + only closes the picker',
            );
        } finally {
            await phone.context.close();
        }
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
