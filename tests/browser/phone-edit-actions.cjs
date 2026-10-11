// Emulated phone: the message action bar stays hidden while a message is being edited.
// Regression: a tap on a row adds .touch-actions, whose display: flex outranked the .editing
// display: none, so after a tap on Edit the bar floated over the edit box, and every tap inside
// the editor re-added the class. The pre-rewrite page did not render the bar during an edit.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { joinPhone } = require('./support/phone.cjs');

// The bar of the row under edit, as a failure needs it: computed display and opacity, whether
// its box overlaps the editor's box, and whether the row still carries the touch state.
const barState = (page) =>
    page.evaluate(() => {
        const row = document.querySelector('#timeline .message-group.editing'),
            bar = row?.querySelector('.message-actions'),
            input = row?.querySelector('.edit-input');
        if (!row || !bar || !input) return { missing: true };
        const a = bar.getBoundingClientRect(),
            b = input.getBoundingClientRect();
        return {
            display: getComputedStyle(bar).display,
            opacity: getComputedStyle(bar).opacity,
            overlapsEditor:
                a.width > 0 &&
                a.left < b.right &&
                a.right > b.left &&
                a.top < b.bottom &&
                a.bottom > b.top,
            touchActions: row.classList.contains('touch-actions'),
        };
    });

async function hiddenBar(page, label) {
    const state = await barState(page);
    assert.equal(
        state.display,
        'none',
        `${label}: the action bar must be hidden while editing; ${JSON.stringify(state)}`,
    );
}

(async () => {
    const browser = await chromium.launch();
    try {
        const { context, page, errors } = await joinPhone(browser, { prefix: 'edit' });
        try {
            const text = 'Edit me on a phone ' + Date.now().toString(36);
            await page.locator('#draft').fill(text);
            await page.locator('#send').tap();
            // Find the confirmed row by its text, then pin it by id: the editor replaces the
            // content node, so a text filter loses the row the moment Edit opens.
            const byText = page
                .locator('#timeline .message-group[id^="msg-"]')
                .filter({ has: page.locator('.message-content', { hasText: text }) })
                .last();
            await byText.waitFor();
            const id = await byText.getAttribute('id');
            const row = page.locator('#' + id);
            await row.locator('.message-content').tap();
            assert(
                await row.evaluate((n) => n.classList.contains('touch-actions')),
                'a tap on the row shows its action bar',
            );
            await row.getByTitle('Edit', { exact: true }).tap();
            await row.locator('.edit-input').waitFor();
            await hiddenBar(page, 'after the tap on Edit');
            // A tap inside the editor (caret placement) lands in the row and re-arms the touch bar.
            await row.locator('.edit-input').tap();
            await hiddenBar(page, 'after a tap inside the editor');
            await row.locator('.edit-input').fill(text + ' edited');
            await row.locator('.edit-save').tap();
            await page.waitForFunction(
                (edited) =>
                    [...document.querySelectorAll('#timeline .message-content')].some((n) =>
                        n.textContent.includes(edited),
                    ),
                text + ' edited',
            );
            // Keyboard up (focus kept on the draft through a button tap, as on iOS): Edit moves
            // the focus into the editor, as the old page did. Keyboard down: it stays down.
            await page.focus('#draft');
            await row.evaluate((node) => node.querySelector('.action-edit').click());
            await row.locator('.edit-input').waitFor();
            assert.equal(
                await page.evaluate(() => document.activeElement?.className),
                'edit-input',
                'Edit takes the focus when a field already holds the keyboard',
            );
            // Empty text: Save closes the editor and keeps the message.
            await row.locator('.edit-input').fill('');
            await row.locator('.edit-save').tap();
            await row.locator('.edit-input').waitFor({ state: 'detached' });
            assert(
                (await row.locator('.message-content').innerText()).includes('edited'),
                'an empty save keeps the message',
            );
            // IME composition owns Enter and Escape (keyCode 229).
            await page.evaluate(() => document.querySelector('#draft').blur());
            await row.locator('.message-content').tap();
            await row.getByTitle('Edit', { exact: true }).tap();
            await row.locator('.edit-input').waitFor();
            await row.locator('.edit-input').evaluate((node) => {
                for (const key of ['Enter', 'Escape'])
                    node.dispatchEvent(
                        new KeyboardEvent('keydown', { key, keyCode: 229, bubbles: true }),
                    );
            });
            await page.waitForTimeout(200);
            assert.equal(await row.locator('.edit-input').count(), 1, 'IME keys keep the editor');
            // Save replaces the editor with the edited text in one step: no recorded state of
            // the row shows the old text without the editor on the way.
            const old = await row.locator('.edit-input').inputValue();
            await row.locator('.edit-input').fill(old + ' again');
            await page.evaluate((id) => {
                window.__states = [];
                const read = () =>
                    document.getElementById(id)?.querySelector('.message-content')?.textContent ??
                    null;
                new MutationObserver(() => window.__states.push(read())).observe(
                    document.querySelector('#timeline'),
                    { subtree: true, childList: true, characterData: true },
                );
            }, id);
            await row.locator('.edit-save').tap();
            await page.waitForFunction(
                (id) =>
                    document
                        .getElementById(id)
                        ?.querySelector('.message-content')
                        ?.textContent.includes('again'),
                id,
            );
            const states = await page.evaluate(() => window.__states);
            assert(
                !states.some(
                    (s) => s && s.startsWith(old) && !s.includes('again') && !s.includes('Save'),
                ),
                `no state showed the old text during the save: ${JSON.stringify(states)}`,
            );
            assert.deepEqual(errors, []);
            console.log(
                'PASS 390x844 touch: the action bar stays hidden after the Edit tap and after a tap inside the editor; Save applies the edit; focus, empty save, IME keys and one-step save match the old page',
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
