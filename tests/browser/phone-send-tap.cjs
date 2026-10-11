// Emulated phone: one tap on Send sends, with or without the emoji picker open.
// Regression: Send's pointerdown focused the draft. With the draft unfocused, that focus closed
// the picker during the tap; the composer dropped by the picker margin and Chromium's re-hit-tested
// click landed on the timeline, so the first tap only closed the picker.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const origin = process.env.YAP_TEST_ORIGIN || 'http://127.0.0.1:7643';
if (
    !['localhost', '127.0.0.1'].includes(new URL(origin).hostname) ||
    new URL(origin).port === '7543'
)
    throw Error('Disposable local fixture required');

async function join(browser) {
    const context = await browser.newContext({
        serviceWorkers: 'block',
        viewport: { width: 390, height: 844 },
        isMobile: true,
        hasTouch: true,
    });
    // Records where each click lands, so a missed tap names the element that took it.
    await context.addInitScript(() => {
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
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.goto(origin + '/login');
    // Usernames are length-capped; keep the prefix short so the unique suffix survives.
    await page
        .locator('.username-input')
        .fill('tap' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5));
    await page.locator('.join-button').click();
    await page.waitForURL('**/lobby');
    await page.locator('#draft:not([disabled])').waitFor();
    await page.waitForFunction(() =>
        document.querySelector('#connection')?.textContent.startsWith('Synced'),
    );
    return { context, page, errors };
}

async function centre(page, selector) {
    const box = await page.locator(selector).boundingBox();
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

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
    },
    {
        label: 'picker open, focus kept on the draft (button taps leave focus, as on iOS)',
        async prepare(page) {
            await page.focus('#draft');
            await page.evaluate(() => document.querySelector('#emoji-button').click());
            const text = await chooseEmojis(page, { tap: false });
            assert.equal(await page.evaluate(() => document.activeElement?.id), 'draft');
            return text;
        },
        picker: true,
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
    },
];

(async () => {
    const browser = await chromium.launch();
    try {
        for (const { label, prepare, picker } of cases) {
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
                await tapSendOnce(page, text, label);
                assert.equal(
                    await page.evaluate(
                        () => document.querySelector('.message-input-container').dataset.picker,
                    ),
                    undefined,
                    `${label}: picker closed after send`,
                );
                assert.deepEqual(errors, []);
                console.log(`PASS 390x844 touch: ${label}; one tap on Send sends`);
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
