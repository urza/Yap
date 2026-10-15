// User-flow reproductions. All account creation and changes use the visible UI.
// Run from the repository after building Yap and installing tests/browser dependencies:
// node reviews/2026-10-15-user-testing-evidence/reproduce.mjs
// Optional: --port=5302 --dll=/path/Yap.dll; TMPDIR selects temporary storage.
// Set YAP_REVIEW_CHROMIUM to use an existing Chromium executable.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { BASE, PORT, DLL, REPO, signIn, row, sleep, waitTrue } from '../../tests/browser/lib.mjs';
const { chromium } = createRequire(new URL('../../tests/browser/package.json', import.meta.url))('playwright');
const OUT = fileURLToPath(new URL('./', import.meta.url));
const results = [], errors = [];
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'yap-user-review-'));
fs.cpSync(path.join(REPO, 'Yap', 'wwwroot'), path.join(root, 'wwwroot'), { recursive: true });
fs.mkdirSync(path.join(root, 'Data'));
for (const e of fs.readdirSync(path.join(REPO, 'Yap', 'Data'), { withFileTypes: true })) {
  if (e.isFile() && e.name.endsWith('.json')) fs.copyFileSync(path.join(REPO, 'Yap', 'Data', e.name), path.join(root, 'Data', e.name));
}
fs.copyFileSync(path.join(REPO, 'Yap', 'appsettings.json'), path.join(root, 'appsettings.json'));
try { const r = await fetch(BASE + '/login'); if (r.ok) throw new Error(`${BASE} is already running`); }
catch (e) { if (!e.message.includes('fetch failed')) throw e; }
const log = fs.openSync(path.join(root, 'server.log'), 'w');
const server = spawn('dotnet', [DLL, '--contentRoot', root, '--urls', BASE,
  '--ChatSettings:Persistence:Enabled=false', '--ChatSettings:WelcomePageEnabled=false'],
  { cwd: root, env: { ...process.env, DOTNET_EnableDiagnostics: '0', ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', log, log] });
let exited = false;
server.on('exit', () => { exited = true; });
let browser;
async function check(name, fn) {
  try { const evidence = await fn(); results.push({ name, ...evidence }); console.log(JSON.stringify(results.at(-1))); }
  catch (e) { results.push({ name, error: e.message }); console.log(JSON.stringify(results.at(-1))); }
}
async function menu(p, action) { await p.locator('.status-button').click(); await p.locator(`[data-action="${action}"]`).click(); }
async function room(p, name) {
  await p.locator('.rooms-list .room-item').filter({ hasText: name }).click();
  await p.locator('.message-input').waitFor();
  await waitTrue(async () => (await p.locator('.chat-header').innerText()).includes(name));
}
try {
  if (!await waitTrue(async () => !exited && (await fetch(BASE + '/login')).ok, 30000)) throw new Error('Server did not start');
  browser = await chromium.launch({ ...(process.env.YAP_REVIEW_CHROMIUM ? { executablePath: process.env.YAP_REVIEW_CHROMIUM } : {}), args: ['--disable-dev-shm-usage'] });
  const ac = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const bc = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const A = await ac.newPage(), B = await bc.newPage();
  for (const [p, name] of [[A, 'Alice'], [B, 'Bob']]) {
    p.setDefaultTimeout(8000);
    p.on('pageerror', e => errors.push({ page: name, error: e.message }));
    p.on('dialog', d => d.accept());
  }
  await signIn(A, 'reviewalice'); await signIn(B, 'reviewbob');
  await check('U01: favorite star keyboard activation sends a GIF', async () => {
    await menu(A, 'settings');
    await A.locator('.gif-lib-file-input.files').setInputFiles({ name: 'private-review.gif', mimeType: 'image/gif',
      buffer: Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64') });
    await A.locator('.gif-library img').first().waitFor({ timeout: 20000 });
    await room(A, 'lobby');
    const events = [];
    for (const key of ['Enter', 'Space']) {
      await A.getByRole('button', { name: 'GIF picker', exact: true }).click();
      await A.locator('.gif-sidebar [data-tab="favorites"]').click();
      const card = A.locator('.gif-card').first();
      await card.waitFor(); await sleep(700); // let the tab refresh finish replacing its cards
      await card.focus(); await A.keyboard.press('Tab');
      const focused = await A.evaluate(() => ({ class: document.activeElement.className, title: document.activeElement.title }));
      const before = await B.locator('.gif-message').count();
      await A.screenshot({ path: path.join(OUT, `gif-star-before-${key.toLowerCase()}.png`) });
      await A.keyboard.press(key);
      const received = await waitTrue(async () => await B.locator('.gif-message').count() > before);
      events.push({ key, focused, before, after: await B.locator('.gif-message').count(), received });
      console.log(JSON.stringify({ gifKey: events.at(-1) }));
      await A.keyboard.press('Escape');
    }
    await A.getByRole('button', { name: 'GIF picker', exact: true }).click();
    await A.locator('.gif-sidebar [data-tab="favorites"]').click();
    const stillFavorited = await A.locator('.gif-fav-btn').first().getAttribute('title');
    const beforeMouse = await B.locator('.gif-message').count();
    await A.locator('.gif-fav-btn').first().click(); await sleep(400);
    const afterMouse = await B.locator('.gif-message').count();
    await B.screenshot({ path: path.join(OUT, 'gif-star-recipient.png') });
    await A.keyboard.press('Escape');
    return { reproduced: events.every(e => e.received && e.focused.title === 'Remove from favorites'), events, stillFavorited, beforeMouse, afterMouse };
  });
  await check('U02: emoji bypasses the composer length limit', async () => {
    await A.keyboard.press('Escape');
    await A.locator('.message-input').fill('x'.repeat(4000));
    await A.getByRole('button', { name: 'Emoji picker', exact: true }).click();
    await A.locator('.emoji-picker .emoji-btn').first().click();
    const length = (await A.locator('.message-input').inputValue()).length;
    await A.getByRole('button', { name: 'Send message', exact: true }).click();
    await A.locator('.message-group.failed').waitFor();
    const reason = await A.locator('.pending-reason').innerText();
    const draft = await A.locator('.message-input').inputValue();
    await A.locator('.message-group.failed').hover();
    const editButtons = await A.locator('.message-group.failed .action-edit').count();
    await A.screenshot({ path: path.join(OUT, 'emoji-limit-failed.png') });
    await A.locator('.pending-retry').click();
    await A.locator('.message-group.failed').waitFor();
    const retryReason = await A.locator('.pending-reason').innerText();
    await A.locator('.pending-remove').click();
    return { reproduced: length > 4000 && reason.includes('4000') && draft === '' && editButtons === 0, length, reason, draft, editButtons, retryReason };
  });
  await A.keyboard.press('Escape');
  await A.locator('.room-item.add-room').click();
  await A.locator('#channel-name').fill('review-room');
  await A.getByRole('button', { name: 'Create Channel', exact: true }).click();
  await A.locator('.message-input').waitFor(); await room(B, 'review-room');
  await check('U03: keyboard cannot reach sidebar conversations or upload', async () => {
    await B.locator('.message-input').focus();
    const stops = [];
    for (let i = 0; i < 10; i++) {
      await B.keyboard.press('Tab');
      stops.push(await B.evaluate(() => ({ tag: document.activeElement.tagName, class: document.activeElement.className, title: document.activeElement.title })));
    }
    const controls = await B.locator('.room-item,.user-item,.image-upload-button').evaluateAll(es => es.map(e => ({ text: e.innerText, tag: e.tagName, tabIndex: e.tabIndex, role: e.getAttribute('role') })));
    return { reproduced: controls.every(c => c.tabIndex === -1) && !stops.some(s => /room-item|user-item|image-upload/.test(s.class)), stops, controls };
  });
  await check('U04: renamed room keeps the old header and title', async () => {
    await B.locator('.message-input').fill('Preserved draft');
    await A.getByTitle('Channel settings').last().click();
    await A.locator('#channel-name').fill('renamed-room');
    await A.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await B.locator('.room-name').filter({ hasText: 'renamed-room' }).waitFor();
    await sleep(1200);
    const beforeReload = { header: await B.locator('.chat-header').innerText(), title: await B.title(), sidebar: await B.locator('.rooms-list').innerText() };
    await B.screenshot({ path: path.join(OUT, 'room-rename-stale.png') });
    await B.reload(); await B.locator('.message-input').waitFor();
    const afterReload = { header: await B.locator('.chat-header').innerText(), title: await B.title(), draft: await B.locator('.message-input').inputValue() };
    return { reproduced: beforeReload.title.includes('#review-room') && afterReload.title.includes('#renamed-room'), beforeReload, afterReload };
  });
  await check('U05: large text clips phone notification options', async () => {
    const pc = await browser.newContext({ viewport: { width: 320, height: 568 }, isMobile: true, hasTouch: true });
    const P = await pc.newPage(); P.setDefaultTimeout(8000);
    await signIn(P, 'reviewphone'); await menu(P, 'settings');
    await P.getByText('20px', { exact: true }).click();
    await P.getByText('Solarized Light', { exact: true }).click();
    await P.getByText('Direct messages', { exact: true }).scrollIntoViewIfNeeded();
    await P.mouse.wheel(0, 160); await sleep(300);
    const bounds = await P.locator('.seg-btn').evaluateAll(es => es.filter(e => e.textContent.trim() === 'Mute all').map(e => ({ text: e.textContent, right: e.getBoundingClientRect().right, parentWidth: e.parentElement.clientWidth, contentWidth: e.parentElement.scrollWidth })));
    await P.screenshot({ path: path.join(OUT, 'phone-mute-clipped.png') });
    return { reproduced: bounds.length === 2 && bounds.every(b => b.contentWidth > b.parentWidth), viewport: P.viewportSize(), textSize: 20, bounds };
  });
  await check('Control: Back navigation works after the conversation renders', async () => {
    await B.locator('.users-list .user-item').filter({ hasText: 'reviewalice' }).click();
    await B.waitForFunction(() => document.querySelector('.chat-container')?.dataset.context === 'dm');
    await room(B, 'lobby');
    await B.waitForFunction(() => document.querySelector('.chat-container')?.dataset.context === 'room');
    await B.goBack();
    await B.waitForFunction(() => document.querySelector('.chat-container')?.dataset.context === 'dm');
    return { passed: new URL(B.url()).pathname === '/dm/reviewalice', path: new URL(B.url()).pathname };
  });
  fs.writeFileSync(path.join(OUT, 'results.json'), JSON.stringify({ browser: browser.version(), port: PORT, results, pageErrors: errors }, null, 2) + '\n');
  process.exitCode = results.some(r => r.error || r.reproduced === false || r.passed === false) ? 1 : 0;
} finally {
  await browser?.close(); server.kill();
  await waitTrue(() => exited, 10000);
  fs.closeSync(log);
  fs.rmSync(root, { recursive: true, force: true });
}
