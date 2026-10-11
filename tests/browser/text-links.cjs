// Message text links follow the pre-rewrite rule (LinkPreviewService.UrlRegex, still the
// server's rule for link previews): www. and bare domains link with https://, trailing
// punctuation and an unbalanced closing bracket stay outside the link, balanced brackets stay
// inside. The rewrite linked only http(s) text up to the next space (audit A-08).
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { origin } = require('./support/phone.cjs');

(async () => {
    const browser = await chromium.launch();
    try {
        const context = await browser.newContext({ serviceWorkers: 'block' });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', (error) => errors.push(error.message));
        await page.goto(origin + '/login');
        await page
            .locator('.username-input')
            .fill('lnk' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5));
        await page.locator('.join-button').click();
        await page.waitForURL('**/lobby');
        await page.locator('#draft:not([disabled])').waitFor();
        const links = (text) =>
            page.evaluate(async (text) => {
                const { richText } = await import('/chat-client/content.js');
                return [...richText(text).querySelectorAll('a')].map((a) => [
                    a.textContent,
                    a.getAttribute('href'),
                ]);
            }, text);
        assert.deepEqual(
            await links('see www.example.com and example.com/path for details'),
            [
                ['www.example.com', 'https://www.example.com'],
                ['example.com/path', 'https://example.com/path'],
            ],
            'www. and bare domains link with https://',
        );
        assert.deepEqual(
            await links('(https://x.com). then http://a.io/b, done'),
            [
                ['https://x.com', 'https://x.com'],
                ['http://a.io/b', 'http://a.io/b'],
            ],
            'trailing ) . and , stay outside the link',
        );
        // The old rule's character class never admitted a closing bracket in an http(s) link,
        // so the bracket stayed outside; the balanced-bracket rule applies to bare-domain paths.
        assert.deepEqual(
            await links('read https://en.wikipedia.org/wiki/Foo_(bar) now'),
            [
                [
                    'https://en.wikipedia.org/wiki/Foo_(bar',
                    'https://en.wikipedia.org/wiki/Foo_(bar',
                ],
            ],
            'http(s) links end before a closing bracket, as on the old page',
        );
        assert.deepEqual(
            await links('see example.com/a(b) and example.com/c) too'),
            [
                ['example.com/a(b)', 'https://example.com/a(b)'],
                ['example.com/c', 'https://example.com/c'],
            ],
            'bare-domain paths keep balanced brackets and drop an unbalanced one',
        );
        assert.deepEqual(
            await links('mail me@example.com'),
            [['example.com', 'https://example.com']],
            'an email address links its domain, as on the old page',
        );
        assert.deepEqual(await links('no links here, just text.'), [], 'plain text stays plain');
        assert.deepEqual(errors, []);
        console.log('PASS message links follow the old URL rule');
        await context.close();
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
