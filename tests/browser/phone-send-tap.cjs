// Emulated phone: one tap on Send sends, with or without the emoji picker open, and sending
// leaves focus where the tap found it, so the phone keyboard stays open or closed as it was.
// Regressions: Send's pointerdown focused the draft. With the draft unfocused, that focus closed
// the picker during the tap; the composer dropped by the picker margin and Chromium's re-hit-tested
// click landed on the timeline, so the first tap only closed the picker. After that fix, send()
// still focused the draft once the message was queued, which opened the keyboard after every
// send made from the picker with the keyboard down. The pre-rewrite page did neither.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { joinPhone, centre } = require('./support/phone.cjs');

// Records where each click lands, so a missed tap names the element that took it.
const recordClicks = () => {
    window.__clicks = [];
    document.addEventListener(
        'click',
        (event) => {
            const n = event.target;
            window.__clicks.push(
                n.nodeType === 1
                    ? n.tagName.toLowerCase() +
                          (n.id ? '#' + n.id : '') +
                          (n.className ? '.' + n.className : '')
                    : n.nodeName,
            );
        },
        true,
    );
};
const join = (browser) => joinPhone(browser, { prefix: 'tap', init: recordClicks });

// Stands in for the phone keyboard: Android and iOS show it while the draft has focus.
const isDraftFocused = (page) => page.evaluate(() => document.activeElement?.id === 'draft');

// Picks three Unicode cells (custom :name: sections fill the first screen) and taps or clicks them.
async function chooseEmojis(page, { tap }) {
    await page.waitForFunction(() => {
        const cell = [
            ...document.querySelectorAll('.emoji-picker-container:not([hidden]) .emoji-btn'),
        ].find((b) => !b.dataset.emoji.startsWith(':') && b.getBoundingClientRect().width);
        if (!cell) return false;
        const scroller = cell.closest('.emoji-content');
        scroller.scrollTop +=
            cell.getBoundingClientRect().top - scroller.getBoundingClientRect().top - 40;
        return true;
    });
    await page.waitForTimeout(200);
    const cells = await page.evaluate(() => {
        const out = [];
        for (const b of document.querySelectorAll(
            '.emoji-picker-container:not([hidden]) .emoji-btn',
        )) {
            const r = b.getBoundingClientRect();
            const x = r.left + r.width / 2,
                y = r.top + r.height / 2;
            if (b.dataset.emoji.startsWith(':') || out.some((c) => c.emoji === b.dataset.emoji))
                continue;
            if (!r.width || document.elementFromPoint(x, y)?.closest('.emoji-btn') !== b) continue;
            out.push({ x, y, emoji: b.dataset.emoji });
            if (out.length === 3) break;
        }
        return out;
    });
    assert.equal(cells.length, 3, 'three visible Unicode emoji cells');
    for (const cell of cells) {
        if (tap) await page.touchscreen.tap(cell.x, cell.y);
        else
            await page.evaluate(
                (emoji) =>
                    document
                        .querySelector(`.emoji-picker-container .emoji-btn[data-emoji="${emoji}"]`)
                        .click(),
                cell.emoji,
            );
        await page.waitForTimeout(150);
    }
    const text = cells.map((c) => c.emoji).join('');
    assert.equal(await page.locator('#draft').inputValue(), text);
    return text;
}

async function sentCount(page, text) {
    return page.evaluate((text) => {
        const plain = (node) => {
            const copy = node.cloneNode(true);
            for (const img of copy.querySelectorAll('img[alt]')) img.replaceWith(img.alt);
            return copy.textContent;
        };
        return [...document.querySelectorAll('#timeline .message-group')].filter((g) =>
            plain(g).includes(text),
        ).length;
    }, text);
}

// Exactly one touch tap on the centre of Send; the message must be sent by it.
async function tapSendOnce(page, text, label) {
    // Send grows in over 0.15s once the draft has text; measure it after the transition.
    await page.waitForTimeout(400);
    const at = await centre(page, '#send');
    assert(
        await page.evaluate(({ x, y }) => !!document.elementFromPoint(x, y)?.closest('#send'), at),
        `${label}: Send is the top element at its centre before the tap`,
    );
    const before = await sentCount(page, text);
    await page.evaluate(() => (window.__clicks = []));
    await page.touchscreen.tap(at.x, at.y);
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
        const draft = await page.locator('#draft').inputValue();
        if (draft === '' && (await sentCount(page, text)) > before) return;
        await page.waitForTimeout(50);
    }
    const state = await page.evaluate(() => ({
        clicks: window.__clicks,
        draft: document.querySelector('#draft').value,
        sendTop: Math.round(document.querySelector('#send').getBoundingClientRect().top),
    }));
    assert.fail(
        `${label}: one tap at (${Math.round(at.x)},${Math.round(at.y)}) did not send; ` +
            `click landed on ${state.clicks.join(', ') || 'nothing'}, ` +
            `draft ${JSON.stringify(state.draft)}, Send now at y=${state.sendTop}`,
    );
}

const cases = [
    {
        label: 'picker open, draft unfocused (button taps take focus, as on Android)',
        async prepare(page) {
            assert.notEqual(await page.evaluate(() => document.activeElement?.id), 'draft');
            const at = await centre(page, '#emoji-button');
            await page.touchscreen.tap(at.x, at.y);
            return chooseEmojis(page, { tap: true });
        },
        picker: true,
        draftFocused: false,
    },
    {
        label: 'picker open, focus kept on the draft (button taps leave focus, as on iOS)',
        async prepare(page) {
            await page.focus('#draft');
            await page.evaluate(() => document.querySelector('#emoji-button').click());
            return chooseEmojis(page, { tap: false });
        },
        picker: true,
        draftFocused: true,
    },
    {
        label: 'picker closed, draft blurred',
        async prepare(page) {
            const text = '😀😁😂';
            await page.locator('#draft').fill(text);
            await page.evaluate(() => document.querySelector('#draft').blur());
            return text;
        },
        picker: false,
        draftFocused: false,
    },
];

(async () => {
    const browser = await chromium.launch();
    try {
        for (const { label, prepare, picker, draftFocused } of cases) {
            const { context, page, errors } = await join(browser);
            try {
                const text = await prepare(page);
                assert.equal(
                    await page.evaluate(
                        () => !!document.querySelector('.message-input-container').dataset.picker,
                    ),
                    picker,
                    `${label}: picker state before the tap`,
                );
                assert.equal(
                    await isDraftFocused(page),
                    draftFocused,
                    `${label}: draft focus before the tap`,
                );
                await tapSendOnce(page, text, label);
                // send() finishes after the queued row renders; let its tail run before reading focus.
                await page.waitForTimeout(300);
                assert.equal(
                    await isDraftFocused(page),
                    draftFocused,
                    `${label}: sending must leave focus where the tap found it (keyboard state)`,
                );
                assert.equal(
                    await page.evaluate(
                        () => document.querySelector('.message-input-container').dataset.picker,
                    ),
                    undefined,
                    `${label}: picker closed after send`,
                );
                assert.deepEqual(errors, []);
                console.log(`PASS 390x844 touch: ${label}; one tap on Send sends, focus unchanged`);
            } finally {
                await context.close();
            }
        }
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
