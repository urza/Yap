// Shared phone fixture for the phone-* suites: a 390x844 touch context on the disposable
// origin, joined as a fresh synthetic account, with the draft enabled and the first sync done.
const origin = process.env.YAP_TEST_ORIGIN || 'http://127.0.0.1:7643';
if (
    !['localhost', '127.0.0.1'].includes(new URL(origin).hostname) ||
    new URL(origin).port === '7543'
)
    throw Error('Disposable local fixture required');

// `init` runs in every page before its scripts (context.addInitScript). `prefix` starts the
// username; usernames are length-capped, so keep it short and the unique suffix survives.
async function joinPhone(browser, { init, prefix = 'ph' } = {}) {
    const context = await browser.newContext({
        serviceWorkers: 'block',
        viewport: { width: 390, height: 844 },
        isMobile: true,
        hasTouch: true,
    });
    if (init) await context.addInitScript(init);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.goto(origin + '/login');
    await page
        .locator('.username-input')
        .fill(prefix + Date.now().toString(36) + Math.random().toString(36).slice(2, 5));
    await page.locator('.join-button').click();
    await page.waitForURL('**/lobby');
    await page.locator('#draft:not([disabled])').waitFor();
    await page.waitForFunction(() =>
        document.querySelector('#connection')?.textContent.startsWith('Synced'),
    );
    return { context, page, errors };
}

// Centre of an element, for touchscreen.tap.
async function centre(page, selector) {
    const box = await page.locator(selector).boundingBox();
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

module.exports = { origin, joinPhone, centre };
