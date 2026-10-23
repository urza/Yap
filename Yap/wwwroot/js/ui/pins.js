// The pinned messages panel (SH-47 to SH-49): opened from the header's pin button, a popup under the button on
// desktop and a bottom sheet on a phone. One entry per pin, the newest first, with the author, the time, the whole
// text, the media under it, and who pinned it. A tap on an entry jumps to the message. The crossed-pin control unpins, for
// the people who may pin (MR-77). The panel draws from state.open.pins and keeps no copy; it follows every change
// while it is open, and it closes when the last pin goes.

import { state, on, mayPin, unpinLocally, displayName } from '../store.js';
import { clone, el, replace } from '../render/dom.js';
import { inline, messageText } from '../render/text.js';
import { messageTime } from '../render/time.js';
import { avatar } from '../render/avatar.js';
import * as hub from '../hub.js';
import * as overlays from './overlays.js';
import { jumpTo } from './timeline.js';
import { voicePlayer } from './voice.js';

// Opens the panel at the anchor and returns the function that closes it. onClose runs on every close.
export function openPins(anchor, onClose) {
  const layer = clone('t-pins-panel');
  const panel = layer.querySelector('.pins-panel');
  const list = layer.querySelector('.pins-list');
  const channelId = state.open?.channelId;

  function draw() {
    const pins = state.open?.pins ?? [];
    if (!pins.length) { close(); return; }   // the last pin went, and the header button with it (SH-47)
    const allowed = mayPin();
    replace(list, pins.map(p => entry(p, allowed)));
  }

  function entry(p, allowed) {
    const node = clone('t-pin-entry');
    node.querySelector('.pin-avatar').append(avatar(p.username, state.users[p.username]?.avatarUrl, 'small', displayName(p.username)));
    replace(node.querySelector('.pin-author'), inline(displayName(p.username)));
    node.querySelector('.pin-time').textContent = messageTime(p.timestamp);
    const text = node.querySelector('.pin-text');
    if (p.text) text.append(...messageText(p.text).nodes); else text.remove();
    const media = node.querySelector('.pin-media');
    const shown = attachments(p.media, p.messageId);
    if (shown.length) { media.hidden = false; media.append(...shown); } else media.remove();
    replace(node.querySelector('.pin-by'), inline(displayName(p.pinnedBy)));
    node.addEventListener('click', e => { if (!e.target.closest('a, .voice-message')) jump(node, p); });   // a link in the text opens as a link; the voice player plays in place
    node.addEventListener('keydown', e => { if (e.target === node && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); jump(node, p); } });   // the trash button and a link handle their own keys
    const unpin = node.querySelector('.pin-unpin');
    if (allowed) unpin.addEventListener('click', e => { e.stopPropagation(); unpinNow(node, p); }); else unpin.remove();
    return node;
  }

  // SH-49: the panel stays while older pages load; it closes once the row is on screen.
  async function jump(node, p) {
    if (node.classList.contains('jumping')) return;
    node.classList.add('jumping');
    const found = await jumpTo(p.messageId);
    node.classList.remove('jumping');
    if (found) close(); else overlays.reason(node, 'Not in the loaded history');
  }

  async function unpinNow(node, p) {
    if (state.connection !== 'connected') { overlays.reason(node, 'Needs a connection'); return; }   // MR-75
    const undo = unpinLocally(p.messageId);
    const result = await hub.invoke('unpin', channelId, p.messageId);
    if (result?.error) { undo?.(); overlays.reason(panel, result.error); }
  }

  layer.querySelector('.pins-close').addEventListener('click', () => close());
  layer.querySelector('.pins-backdrop').addEventListener('click', () => close());
  document.body.append(layer);   // the body, not the frame: a fixed popup must not sit in another stacking context
  const offs = [
    on('pins', draw), on('rooms', draw), on('users', draw),
    on('open', () => { if (state.open?.channelId !== channelId) close(); else draw(); }),   // a switch closes it; a reconnect redraws it
  ];
  const close = overlays.open({ element: panel, keep: [anchor], onClose: () => { offs.forEach(off => off()); layer.remove(); onClose?.(); } });
  draw();
  // After the entries exist: the height decides below or above. The button ends 8 px above the header's edge and the
  // 3 px header line follows, so a gap of 18 px leaves 7 px of air under the line (12 px touched it). The button sits
  // after the title on a desktop and on the right in a narrow window (SH-47): the panel grows from the button's side.
  if (layer.isConnected) {
    const align = anchor.getBoundingClientRect().left < innerWidth / 2 ? 'left' : 'right';
    overlays.place(panel, anchor, { align, gap: 18 });
  }
  return close;
}

// What an entry shows under its text (SH-48, Pin.media, in the row's order). The pictures, the uploaded videos and the
// GIFs make one block: alone, at its own size and no taller than 240 px; two or more, a grid of square cells in at most
// two rows, so three entries still fit in one view. A voice message is the row's own player, a link card its picture,
// title and site, a GIF whose file is gone a label. An empty list when there is nothing to show.
const PICTURES = new Set(['image', 'video', 'gif', 'link']);

function attachments(media, key) {
  const items = Array.isArray(media) ? media : [];   // an offline snapshot from before 2026-10-23 holds one object; the next connection replaces it
  const pictures = items.filter(m => PICTURES.has(m.kind) && (m.src || m.poster));
  const nodes = [];
  if (pictures.length === 1) nodes.push(single(pictures[0]));
  else if (pictures.length > 1) nodes.push(grid(pictures));
  for (const m of items) {
    if (pictures.includes(m)) continue;
    if (m.kind === 'voice' && m.src) nodes.push(voicePlayer(m, key));
    else if (m.title) nodes.push(card(null, m));   // a link card without a picture, or a cached video whose poster is gone
    else nodes.push(el('span', { class: 'pin-attachment-label' }, m.kind === 'voice' ? 'Voice message' : m.kind === 'gif' ? 'GIF attachment' : 'Attachment'));
  }
  return nodes;
}

function single(m) {
  const node = picture(m);
  if (m.width && m.height) {   // its own size (a GIF's pixels, a picture's or a poster's), no wider than the panel, no taller than the 240 px cap
    node.style.width = `${m.width}px`;   // not 100%: the frame fits its content, and a video is 300 px wide until it loads
    node.style.aspectRatio = `${m.width} / ${m.height}`;
    node.style.maxWidth = `min(100%, ${m.width}px, ${Math.round(240 * m.width / m.height)}px)`;
  }
  const frame = el('span', { class: 'pin-frame' }, node, m.kind === 'video' ? playMark() : null);
  return m.title ? card(frame, m) : frame;
}

// The count sets the columns (--cols, frame.css): one row up to four pictures, two rows above that.
function grid(items) {
  const cols = items.length <= 4 ? items.length : Math.ceil(items.length / 2);
  return el('span', { class: 'pin-grid', style: `--cols: ${cols}` },
    items.map(m => el('span', { class: 'pin-cell' }, picture(m), m.kind === 'video' ? playMark() : null)));
}

// The picture itself: an image, a link card's picture, a GIF that loops, or a video with its poster. The video shows
// its first frame when the poster file is missing (a server without ffmpeg); it plays in the row, not here.
function picture(m) {
  switch (m.kind) {
    case 'image': return el('img', { class: 'pin-picture', src: m.src, alt: '', loading: 'lazy' });
    case 'link': return el('img', { class: 'pin-picture', src: m.poster, alt: '', loading: 'lazy' });
    case 'gif': {
      const video = el('video', { class: 'pin-gif', src: m.src, autoplay: true, loop: true, muted: true, playsinline: true, preload: 'auto' });
      video.muted = true; video.play?.().catch(() => {});   // the attribute alone does not satisfy every autoplay rule; a still first frame is fine
      return video;
    }
    default: return el('video', { class: 'pin-video', src: m.src, poster: m.poster, muted: true, playsinline: true, preload: 'metadata' });
  }
}

const playMark = () => el('span', { class: 'pin-play', 'aria-hidden': 'true' });

// A link card, or a cached link video: the picture when there is one, then the title and the site (MR-20).
const card = (frame, m) => el('span', { class: 'pin-card' }, frame, el('span', { class: 'pin-card-title' }, m.title), m.site ? el('span', { class: 'pin-card-site' }, m.site) : null);
