// Shell details restored from the pre-rewrite page: the iOS home-screen head tags (audit D-13),
// the profile info button on every touch screen (D-01), one font in the account menu (D-16),
// and no playsinline flag on uploaded video elements (C-06).
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { origin } = require('./support/phone.cjs');

(async () => {
    const browser = await chromium.launch();
    try {
        // A wide touch screen (phone in landscape, tablet). A real touch context: this Chromium
        // ignores pointer and hover in CDP's emulated media features.
        const context = await browser.newContext({
            serviceWorkers: 'block',
            viewport: { width: 1024, height: 768 },
            isMobile: true,
            hasTouch: true,
        });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', (error) => errors.push(error.message));
        await page.goto(origin + '/login');
        await page
            .locator('.username-input')
            .fill('sh' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5));
        await page.locator('.join-button').click();
        await page.waitForURL('**/lobby');
        await page.locator('#draft:not([disabled])').waitFor();
        await page.waitForFunction(() =>
            document.querySelector('#connection')?.textContent.startsWith('Synced'),
        );
        const head = await page.evaluate(() => ({
            icon: document.querySelector('link[rel="apple-touch-icon"]')?.getAttribute('href'),
            capable: document.querySelector('meta[name="apple-mobile-web-app-capable"]')?.content,
            bar: document.querySelector('meta[name="apple-mobile-web-app-status-bar-style"]')
                ?.content,
        }));
        assert.deepEqual(
            head,
            { icon: '/icon-192.png', capable: 'yes', bar: 'black-translucent' },
            'the iOS home-screen head tags are present',
        );
        // The account menu: buttons and links share the page font.
        const fonts = await page.evaluate(() => {
            const body = getComputedStyle(document.body).fontFamily;
            return [...document.querySelectorAll('.dropdown-item')].map(
                (n) => getComputedStyle(n).fontFamily === body,
            );
        });
        assert(fonts.length > 3 && fonts.every(Boolean), `menu items share one font: ${fonts}`);
        // A wide touch screen: the info button shows (the hover card is off for coarse pointers).
        assert.equal(
            await page.evaluate(() => matchMedia('(pointer: coarse)').matches),
            true,
            'the context reports a coarse pointer',
        );
        await page.locator('.profile-info-btn').first().waitFor({ state: 'attached' });
        const display = await page
            .locator('.profile-info-btn')
            .first()
            .evaluate((n) => getComputedStyle(n).display);
        assert.equal(display, 'flex', 'the info button shows under a coarse pointer at 1024 px');
        // Uploaded video elements, where present, carry no playsinline flag.
        const inline = await page.evaluate(() =>
            [...document.querySelectorAll('video.video-player, video.cached-video-player')].map(
                (v) => v.playsInline,
            ),
        );
        assert(!inline.includes(true), `no playsinline on timeline videos: ${inline}`);
        assert.deepEqual(errors, []);
        console.log(
            'PASS shell: iOS head tags, one menu font, info button on a wide coarse pointer, no playsinline',
        );
        await context.close();
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
