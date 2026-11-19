// The picture of README.md: screenshot.png in the repository root. It shows one room with a talk that uses the
// features a visitor looks for (a gallery, reactions, a reply, a pin, a message link, a voice message, unread
// counts, presence). The spec pictures of screenshots.mjs show one rule each and are not made for this purpose.
//
//   node readme.mjs                                   writes screenshot.png
//   node readme.mjs --theme=nord,teahouse:8pm,ocean   writes shots/readme-<theme>.png for each, to compare them
//
// A Tea House scene follows the clock of the device, so a run at a different hour gives a different picture.
// "teahouse:<scene>" sets the scene (6am 8am 10am noon 2pm 4pm 6pm 8pm 10pm midnight 2am 4am).
//
// The server needs ffmpeg for the voice message.

import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { BASE, REPO, SHOTS, arg, sleep, waitTrue, row, step, finish, startServer, signIn, watchContext, watchPage, fixture, sendFiles, api } from './lib.mjs';

const LOOKS = (arg('theme') ?? 'teahouse:6pm').split(',').map(look => look.split(':'));   // [theme, scene]
// GitHub shows the picture about 900 px wide. 1.5 times the pixels keep the text sharp on a dense display; twice
// the pixels gave a file of 2.6 MB, because the scene art compresses badly as PNG.
const VIEW = { viewport: { width: 1200, height: 900 }, deviceScaleFactor: 1.5 };
const HEART = '❤️', THUMBS = '\u{1F44D}';   // the titles of the quick reactions; the heart needs its variation selector

const need = (cond, text) => { if (!cond) throw new Error(text); };

// ---- the photos ----

// Draws one landscape in the page and returns it as a JPEG data URL: a sky gradient, a sun or a moon, and layered
// ridges. The repository holds no photos, and a download needs a licence and the network. Runs in the browser.
function landscape({ sky, sun, ridges, stars = 0, seed }) {
  const w = 1200, h = 900;
  const canvas = Object.assign(document.createElement('canvas'), { width: w, height: h });
  const g = canvas.getContext('2d');
  let s = seed;
  const random = () => (s = (s * 16807) % 2147483647) / 2147483647;   // seeded: each run draws the same photos

  const gradient = g.createLinearGradient(0, 0, 0, h);
  sky.forEach((colour, i) => gradient.addColorStop(i / (sky.length - 1), colour));
  g.fillStyle = gradient;
  g.fillRect(0, 0, w, h);

  for (let i = 0; i < stars; i++) {
    g.fillStyle = `rgba(255,255,255,${0.3 + random() * 0.7})`;
    g.beginPath(); g.arc(random() * w, random() * h * 0.6, 0.6 + random() * 1.8, 0, 7); g.fill();
  }

  const x = sun.x * w, y = sun.y * h, r = sun.r * h;
  const glow = g.createRadialGradient(x, y, r * 0.5, x, y, r * 6);
  glow.addColorStop(0, sun.glow); glow.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = glow; g.fillRect(0, 0, w, h);
  g.fillStyle = sun.colour;
  g.beginPath(); g.arc(x, y, r, 0, 7); g.fill();

  // A ridge line is a sum of sines with random phases. Each nearer ridge sits lower and is flatter.
  ridges.forEach((colour, i) => {
    const base = h * (0.5 + 0.42 * i / ridges.length), rough = h * (0.11 - 0.02 * i);
    const waves = [1, 2.3, 5.1, 11].map(f => [f, random() * 7]);
    g.fillStyle = colour;
    g.beginPath(); g.moveTo(0, h);
    for (let px = 0; px <= w; px += 4) g.lineTo(px, base - rough * 0.6 * waves.reduce((sum, [f, phase]) => sum + Math.sin(f * px / w * 6.283 + phase) / f, 0));
    g.lineTo(w, h); g.fill();
  });
  return canvas.toDataURL('image/jpeg', 0.9);
}

const PHOTOS = [
  { name: 'dawn', seed: 11, sky: ['#2b1b4a', '#b4476b', '#f6a55c', '#ffd9a0'], sun: { x: .68, y: .5, r: .06, colour: '#fff3d6', glow: 'rgba(255,214,150,.55)' }, ridges: ['#7d4a7a', '#5b3566', '#3d2550', '#24173a'] },
  { name: 'noon', seed: 23, sky: ['#2f7fd1', '#7fbdf0', '#d9eefc'], sun: { x: .8, y: .2, r: .04, colour: '#ffffff', glow: 'rgba(255,255,255,.5)' }, ridges: ['#9db8cf', '#6f9a8a', '#3f7a55', '#22532f'] },
  { name: 'mist', seed: 37, sky: ['#aebfc4', '#d3dcd8', '#eef0e6'], sun: { x: .55, y: .3, r: .035, colour: '#fffdf2', glow: 'rgba(255,255,240,.6)' }, ridges: ['#a9bcb6', '#7e9a90', '#52766a', '#2c4c43'] },
  { name: 'sunset', seed: 41, sky: ['#1d1a3d', '#7a2f5c', '#e2583e', '#fbb040'], sun: { x: .4, y: .58, r: .08, colour: '#ffe08a', glow: 'rgba(255,170,80,.6)' }, ridges: ['#8a3b4a', '#5e2941', '#3a1b33', '#1c0f22'] },
  { name: 'night', seed: 53, sky: ['#050b1f', '#0f2147', '#27457a'], sun: { x: .3, y: .25, r: .045, colour: '#f4f1e4', glow: 'rgba(200,215,255,.25)' }, ridges: ['#1c2f55', '#142344', '#0d1830', '#070e1f'], stars: 140 },
];

// ---- helpers ----

const people = {};   // username -> { ctx, page }
async function join(name) {
  const ctx = await browser.newContext(VIEW);
  watchContext(ctx, name);
  const page = await ctx.newPage(); watchPage(page, name);
  await signIn(page, name);
  people[name] = { ctx, page };
  return page;
}

async function show(page, where) {
  await page.goto(`${BASE}/${where}`);
  await page.waitForSelector('.message-input', { timeout: 10000 });
}

// `where` is "channels/<id>" for a room and "dm/<username>" for a DM.
const say = (who, where, body) => api(people[who].page, 'POST', `/api/${where}/messages`, { clientId: crypto.randomUUID(), replyToId: null, ...body });
const arrived = (page, needle, ms = 10000) => waitTrue(async () => (await row(page, needle).count()) > 0, ms);

// A click on the quick reaction of the row's action bar. Dispatched, not clicked with the pointer: a hub event
// redraws the row under the pointer. The quick reactions exist only after a pointer entered the row.
async function react(page, needle, emoji) {
  const r = row(page, needle).last();
  const total = () => r.locator('.reaction-count').evaluateAll(cs => cs.reduce((n, c) => n + Number(c.textContent), 0));
  const before = await total();
  await r.dispatchEvent('pointerover');
  await r.locator(`.message-actions .quick-reaction[title="${emoji}"]`).dispatchEvent('click');
  need(await waitTrue(async () => (await total()) > before, 5000), `no reaction ${emoji} on "${needle}"`);
}

// Waits for the fonts and for every image in the viewport, so the picture shows no half-loaded message.
async function settle(page, ms = 400) {
  await page.evaluate(() => document.fonts?.ready.then(() => true));
  await waitTrue(() => page.evaluate(() => [...document.images].filter(i => {
    const r = i.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
  }).every(i => i.complete)), 8000, 100);
  await sleep(ms);
}

// ---- the run ----

const server = await startServer();
const browser = await chromium.launch();
const rooms = {};
let lastJoinAt = 0;

await step(1, 'People and rooms', async (check) => {
  const M = await join('mia');   // the first account is the admin, and the picture is her view
  for (const name of ['trips', 'music', 'food']) await api(M, 'POST', '/api/rooms', { name, description: null, adminOnly: false, historyLimit: 'oneMonth', sinceJoined: true });
  for (const r of (await api(M, 'GET', '/api/bootstrap')).rooms) rooms[r.name] = r.id;
  check(rooms.trips && rooms.music && rooms.food, `rooms ${Object.keys(rooms).join(', ')}`);
  // A new account has every room muted by mode. Individual mode lets the sidebar show a count on one room.
  await api(M, 'PUT', '/api/account/mutes', { roomMode: 'individual', dmMode: 'individual', newDmsMuted: false });
  for (const id of Object.values(rooms)) await api(M, 'PUT', '/api/account/mutes', { channel: { channelId: id, muted: false } });

  for (const name of ['tom', 'lena', 'jonas', 'eva', 'ben']) await join(name);
  lastJoinAt = Date.now();
  // A room shows its history from the moment a person joined it, so everyone opens the room before the talk.
  for (const name of ['mia', 'tom', 'lena', 'jonas']) await show(people[name].page, `room/${rooms.trips}`);
});

await step(2, 'The talk in #trips', async (check, notes) => {
  const { mia: { page: M }, tom: { page: T }, lena: { page: L }, jonas: { page: J } } = people;
  const trips = `channels/${rooms.trips}`;

  const plan = await say('mia', trips, { text: 'saturday: the ridge trail. we start at 8 at the station 🥾' });
  await say('tom', trips, { text: 'how long is it?' });
  await say('mia', trips, { text: 'about 14 km, 5 hours with breaks' });
  await say('lena', trips, { text: "I'll bring the thermos ☕" });

  // The pin, through the row's More menu as a person does it. The header button shows only with a pin.
  need(await arrived(M, 'the ridge trail'), 'the plan did not arrive');
  const planRow = row(M, 'the ridge trail').last();
  await planRow.hover();
  await planRow.locator('.action-more').click();
  await M.locator('.more-menu [data-action="pin"]').click();
  check(await waitTrue(() => M.locator('.chat-header .pins-button').isVisible(), 5000), 'the header shows the pin button');

  const files = [];
  for (const photo of PHOTOS) {
    const url = await M.evaluate(landscape, photo);
    files.push(fixture(`readme-${photo.name}.jpg`));
    fs.writeFileSync(files.at(-1), Buffer.from(url.split(',')[1], 'base64'));
  }
  const caption = 'we made it! photos from the top 📸';
  await T.fill('.message-input', caption);
  await sendFiles(T, files);
  need(await waitTrue(async () => (await row(M, 'we made it').locator('.image-gallery img').count()) === files.length, 30000, 200), 'the photos did not arrive');
  // The photo message went through the composer, so its id comes from the page: the node with the id "msg-<id>".
  const photoId = await row(M, 'we made it').last().evaluate(e => (e.closest('[id^="msg-"]') ?? e.querySelector('[id^="msg-"]'))?.id.slice(4) ?? null);
  need(photoId, 'no id for the photo message');

  await say('lena', trips, { text: 'that light on the way down 😍' });
  await say('jonas', trips, { text: 'next time I come too', replyToId: photoId });
  // A message link shows as a label when its origin is the page's own (message-links.js). The code is the id's
  // 16 bytes as base64url.
  const link = `${BASE}/m/${Buffer.from(plan.id.replaceAll('-', ''), 'hex').toString('base64url')}`;
  await say('mia', trips, { text: `we do it again in spring. the plan stays the same: ${link}` });

  // The voice message. The sound is a plain tone: a picture shows only the wave, and the wave comes from `peaks`,
  // which the sender's client supplies (one base-36 digit per bar).
  const wav = fixture('readme-voice.wav');
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'sine=frequency=220:duration=14', '-ar', '8000', wav]);
  const voice = await T.evaluate(async b64 => {
    const { uploadOne } = await import('/js/upload.js');
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    return uploadOne(new File([bytes], 'voice.wav', { type: 'audio/wav' }), '/api/tus', { kind: 'voice' });
  }, fs.readFileSync(wav).toString('base64'));
  check(voice?.type === 'voice', `voice upload: ${JSON.stringify(voice)}`);
  const peaks = Array.from({ length: 64 }, (_, i) => Math.max(2, Math.round(35 * Math.abs(Math.sin(i * 0.9)) * (0.35 + 0.65 * Math.abs(Math.sin(i * 0.23 + 1))))).toString(36)).join('');
  await say('tom', trips, { voice: { url: voice.url, durationMs: voice.durationMs, peaks } });

  await react(L, 'we made it', HEART);
  await react(J, 'we made it', HEART);
  await react(M, 'we made it', HEART);
  await react(J, 'we made it', THUMBS);
  await react(T, 'next time I come too', THUMBS);
});

await step(3, 'The sidebar: unread counts, away and offline', async (check, notes) => {
  const M = people.mia.page, E = people.eva.page;
  for (const text of ['new mix for the drive home 🎧', 'side B is better, trust me']) await say('tom', `channels/${rooms.music}`, { text });

  await say('lena', 'dm/mia', { text: 'can you send me the full size photos?' });
  // An offline person stays in the list only with a DM, so Ben writes one before he leaves.
  await say('ben', 'dm/mia', { text: "can't make it this time, have fun" });

  await E.click('.status-button');
  await E.locator('.status-dropdown [data-status="away"]').click();
  await people.ben.ctx.close();   // the last tab closed: offline about 30 s later

  const benDot = M.locator('.users-list .user-item', { hasText: 'ben' }).locator('.user-status-dot');
  check(await waitTrue(async () => !(await benDot.getAttribute('class')).includes('online'), 90000, 500), 'ben is offline in the list');

  // The bot greets each new account a minute after its first connection. Mia reads that DM and Ben's, so the
  // counts that stay are the room and Lena.
  const wait = lastJoinAt + 65000 - Date.now();
  notes.push(`waiting ${Math.max(0, Math.round(wait / 1000))} s for the bot's welcome DM`);
  if (wait > 0) await sleep(wait);
  for (const partner of ['ping', 'ben']) { await show(M, `dm/${partner}`); await sleep(1000); }
});

await step(4, `The picture: ${LOOKS.map(look => look.join(' ')).join(', ')}`, async (check, notes) => {
  const M = people.mia.page, L = people.lena.page;
  for (const [theme, scene] of LOOKS) {
    await api(M, 'PATCH', '/api/account', { theme });
    await show(M, `room/${rooms.trips}`);   // a fresh page: the new theme, and no join or leave lines
    need(await arrived(M, 'the plan stays the same'), 'the talk did not load');
    if (scene) await M.evaluate(scene => { document.documentElement.dataset.scene = scene; }, scene);
    await settle(M, 800);
    // The reader drops a typist 3.5 s after the last keystroke, so the typing comes last.
    await L.fill('.message-input', '');
    await L.locator('.message-input').pressSequentially('haha ', { delay: 40 });
    need(await waitTrue(() => M.locator('.typing-indicator:not([hidden])').isVisible(), 4000), 'no typing indicator');
    await M.mouse.move(VIEW.viewport.width - 100, VIEW.viewport.height - 20);   // the empty foot of the sidebar: no hover state
    await sleep(200);
    const file = LOOKS.length === 1 ? path.join(REPO, 'screenshot.png') : path.join(SHOTS, `readme-${[theme, scene].filter(Boolean).join('-')}.png`);
    await M.screenshot({ path: file });
    notes.push(`wrote ${file}`);
  }
  check(true, `${LOOKS.length} picture(s)`);
});

await finish(browser, server);
