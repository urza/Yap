// The voice message in a row (MR-76), and the bars that both it and the recorder draw. One audio element plays
// every voice message of the page: a play on one pauses the other, as the voice notes of a phone do. The speed
// is one setting for the device.
//
// The bars are one canvas per row, not 48 elements, redrawn while the message plays. Their colours come from the
// stylesheet (--voice-bar and --voice-bar-played on .voice-bars), so a theme shows at the next draw.

import { el } from '../render/dom.js';

const SPEEDS = [1, 1.5, 2];
const FLAT = new Array(48).fill(0.25);   // a message whose recorder measured nothing
let speed = Number(localStorage.getItem('yap.voiceSpeed'));
if (!SPEEDS.includes(speed)) speed = 1;

const audio = new Audio();
// The message that plays, or is paused part way: its key and `view`, the controls of its row as last drawn. The
// key is the message's, not the file's: a pending row plays its Blob, and the accepted row that takes its place
// carries another URL but the same message, so its controls take over the playback.
let current = null;
let frame = 0;

export const formatTime = ms => { const s = Math.round(ms / 1000); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };

// levels: 0 to 1 per bar, left to right; played: the part of the width in the played colour, 0 to 1.
export function drawBars(canvas, levels, played = 0) {
  const dpr = devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (!w || !h) return;   // not laid out yet: the resize observer of the row draws when it is
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  const n = levels.length;
  if (!n) return;
  const style = getComputedStyle(canvas);
  const idle = style.getPropertyValue('--voice-bar').trim() || '#888', done = style.getPropertyValue('--voice-bar-played').trim() || '#fff';
  const gap = 2, bar = Math.max(1, (w - gap * (n - 1)) / n), edge = played * w;
  for (let i = 0; i < n; i++) {
    const x = i * (bar + gap), bh = Math.max(2, levels[i] * h);
    ctx.fillStyle = x + bar / 2 <= edge ? done : idle;
    if (ctx.roundRect) { ctx.beginPath(); ctx.roundRect(x, (h - bh) / 2, bar, bh, bar / 2); ctx.fill(); }
    else ctx.fillRect(x, (h - bh) / 2, bar, bh);
  }
}

// voice: { src, durationMs, peaks }; key: the message (its clientId, or its id). The pending row passes the Blob's
// URL, the accepted row the file's.
export function voicePlayer(voice, key) {
  const levels = voice.peaks ? [...voice.peaks].map(c => parseInt(c, 36) / 35) : FLAT;
  const play = el('button', { type: 'button', class: 'voice-play', title: 'Play voice message', 'aria-label': 'Play voice message' }, playIcon(), pauseIcon());
  const bars = el('canvas', { class: 'voice-bars', 'aria-hidden': 'true' });
  const time = el('span', { class: 'voice-time' });
  const speedButton = el('button', { type: 'button', class: 'voice-speed', title: 'Playback speed' }, speedLabel());
  const node = el('div', { class: 'voice-message' }, play, bars, time, speedButton);
  const view = { key, voice, levels, node, bars, time };
  if (current?.key === key) current.view = view;   // the row was drawn again while it plays: the controls follow

  play.addEventListener('click', () => {
    if (current?.key !== key) { start(view, 0); return; }
    if (audio.paused) audio.play().catch(() => {}); else audio.pause();
  });
  bars.addEventListener('click', e => {   // a tap on the bars seeks (MR-76)
    const r = bars.getBoundingClientRect();
    const at = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)) * total(view);
    if (current?.key === key) { audio.currentTime = at; draw(view); } else start(view, at);
  });
  speedButton.addEventListener('click', () => {
    speed = SPEEDS[(SPEEDS.indexOf(speed) + 1) % SPEEDS.length];
    localStorage.setItem('yap.voiceSpeed', String(speed));
    audio.defaultPlaybackRate = audio.playbackRate = speed;
    for (const b of document.querySelectorAll('.voice-speed')) b.textContent = speedLabel();   // one speed, every row shows it
  });
  new ResizeObserver(() => draw(view)).observe(bars);   // the first draw, and every width change (a phone turns)
  draw(view);
  return node;
}

// The length in seconds: the file's own once it is known, else the recorder's. A Chrome recording played as a Blob
// reports no length at all (its WebM carries none), so the stored figure is never dropped.
const total = view => Number.isFinite(audio.duration) && audio.duration > 0 && current?.key === view.key ? audio.duration : view.voice.durationMs / 1000;

function draw(view) {
  const mine = current?.key === view.key;
  const at = mine ? audio.currentTime : 0;
  drawBars(view.bars, view.levels, total(view) ? Math.min(1, at / total(view)) : 0);
  view.time.textContent = formatTime(mine && at > 0 ? at * 1000 : view.voice.durationMs);
  view.node.classList.toggle('playing', mine && !audio.paused && !audio.ended);
}

function start(view, at) {
  const previous = current?.view;
  current = { key: view.key, view };
  if (previous) draw(previous);   // its bars go back to the start
  audio.src = view.voice.src;
  audio.defaultPlaybackRate = audio.playbackRate = speed;
  if (at > 0) audio.addEventListener('loadedmetadata', () => { audio.currentTime = at; }, { once: true });
  audio.play().catch(() => {});
}

const tick = () => { if (current) draw(current.view); };
const loop = () => { tick(); if (!audio.paused && !audio.ended) frame = requestAnimationFrame(loop); };   // smoother than timeupdate's four a second
audio.addEventListener('play', () => { cancelAnimationFrame(frame); loop(); });
audio.addEventListener('pause', tick);
audio.addEventListener('ended', () => { const view = current?.view; current = null; audio.removeAttribute('src'); if (view) draw(view); });
audio.addEventListener('error', () => { const view = current?.view; current = null; if (view) draw(view); });

const speedLabel = () => `${speed}×`;
const playIcon = () => { const s = el('span', { class: 'play-icon', 'aria-hidden': 'true' }); s.innerHTML = '<svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>'; return s; };
const pauseIcon = () => { const s = el('span', { class: 'pause-icon', 'aria-hidden': 'true' }); s.innerHTML = '<svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor"><path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/></svg>'; return s; };
