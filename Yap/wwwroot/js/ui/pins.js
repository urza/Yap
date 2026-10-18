// The pinned messages panel (SH-47 to SH-49): opened from the header's pin button, a popup under the button on
// desktop and a bottom sheet on a phone. One entry per pin, the newest first, with the author, the time, the whole
// text or the first picture, and who pinned it. A tap on an entry jumps to the message. The ✕ unpins, for the
// people who may pin (MR-77). The panel draws from state.open.pins and keeps no copy; it follows every change
// while it is open, and it closes when the last pin goes.

import { state, on, mayPin, unpinLocally, displayName } from '../store.js';
import { clone, replace } from '../render/dom.js';
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
    const thumb = node.querySelector('.pin-thumb'), label = node.querySelector('.pin-attachment-label');
    if (p.thumb) { thumb.hidden = false; thumb.src = p.thumb; label.remove(); }
    else if (p.hasMedia) { thumb.remove(); label.hidden = false; }   // a GIF: no stored picture (Pin.Thumb)
    else { thumb.remove(); label.remove(); }
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
    on('pins', draw), on('rooms', draw), on('users', draw),
    on('open', () => { if (state.open?.channelId !== channelId) close(); else draw(); }),   // a switch closes it; a reconnect redraws it
  ];
  const close = overlays.open({ element: panel, keep: [anchor], onClose: () => { offs.forEach(off => off()); layer.remove(); onClose?.(); } });
  draw();
  if (layer.isConnected) overlays.place(panel, anchor);   // after the entries exist: the height decides below or above
  return close;
}
