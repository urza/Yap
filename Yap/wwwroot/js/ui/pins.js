// The pinned messages panel (SH-47 to SH-49): opened from the header's pin button, a popup under the button on
// desktop and a bottom sheet on a phone. One entry per pin, the newest first, with the author, the time, the whole
// text, the media under it, and who pinned it. A tap on an entry jumps to the message. The trash control unpins, for
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
    const media = node.querySelector('.pin-media'), label = node.querySelector('.pin-attachment-label');
    const shown = attachment(p.media);
    if (shown) { media.hidden = false; media.append(shown); label.remove(); }
    else if (p.hasMedia || p.media) { media.remove(); label.hidden = false; label.textContent = labelFor(p.media); }
    else { media.remove(); label.remove(); }
    replace(node.querySelector('.pin-by'), inline(displayName(p.pinnedBy)));
    node.addEventListener('click', e => { if (!e.target.closest('a')) jump(node, p); });   // a link in the text opens as a link
    node.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jump(node, p); } });
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
    on('pins', draw), on('users', draw),
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

// What an entry shows under its text (SH-48, Pin.media): the picture, the GIF itself, a poster with a play mark, or a
// link card's picture and title, each at the panel's width. Null when there is nothing to show; labelFor names it then.
function attachment(media) {
  if (!media) return null;
  const frame = el('span', { class: 'pin-frame' });
  switch (media.kind) {
    case 'image':
      frame.append(el('img', { class: 'pin-picture', src: media.src, alt: 'Attachment', loading: 'lazy' }));
      return frame;
    case 'gif': {
      if (!media.src) return null;   // the file is gone
      const video = el('video', { class: 'pin-gif', src: media.src, autoplay: true, loop: true, muted: true, playsinline: true, preload: 'auto' });
      if (media.width && media.height) {   // its own size, no wider than the panel, no taller than the 240 px cap
        video.style.width = `${media.width}px`;   // not 100%: the frame fits its content, and a video is 300 px wide until it loads
        video.style.aspectRatio = `${media.width} / ${media.height}`;
        video.style.maxWidth = `min(100%, ${media.width}px, ${Math.round(240 * media.width / media.height)}px)`;
      }
      video.muted = true; video.play?.().catch(() => {});   // the attribute alone does not satisfy every autoplay rule; a still first frame is fine
      frame.append(video);
      return frame;
    }
    case 'video':
    case 'link': {
      if (!media.poster) return null;
      frame.append(el('img', { class: 'pin-picture', src: media.poster, alt: media.title ?? 'Attachment', loading: 'lazy' }));
      if (media.kind === 'video') frame.append(el('span', { class: 'pin-play', 'aria-hidden': 'true' }));
      return media.title ? el('span', { class: 'pin-card' }, frame, el('span', { class: 'pin-card-title' }, media.title)) : frame;
    }
    default:
      return null;
  }
}

const labelFor = media => media?.kind === 'voice' ? 'Voice message' : media?.kind === 'gif' ? 'GIF attachment' : 'Attachment';
