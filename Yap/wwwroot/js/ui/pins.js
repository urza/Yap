// The pinned messages bar (TL-36 to TL-38): up to three lines between the header and the list, one per pin, the
// newest pin first. A line shows the author and the text; a tap jumps to the message. The × unpins, for the people
// who may pin (MR-76). The bar draws from state.open.pins and keeps no copy.

import { state, on, mayPin, unpinLocally, displayName } from '../store.js';
import { clone, replace } from '../render/dom.js';
import { inline } from '../render/text.js';
import * as hub from '../hub.js';
import * as overlays from './overlays.js';
import { jumpTo } from './timeline.js';

export function PinsBar(root) {
  const bar = clone('t-pins-bar');
  root.append(bar);

  function draw() {
    const pins = state.open?.pins ?? [];
    bar.hidden = pins.length === 0;
    const allowed = mayPin();
    replace(bar, pins.map(p => {
      const line = clone('t-pin-line');
      replace(line.querySelector('.pin-author'), inline(displayName(p.username)));
      replace(line.querySelector('.pin-text'), p.text ? inline(p.text) : [p.hasMedia ? 'Click to see attachment' : '']);   // the quote's wording (MR-32)
      const body = line.querySelector('.pin-body');
      body.title = `Pinned by ${displayName(p.pinnedBy)}`;
      body.addEventListener('click', async () => { if (!(await jumpTo(p.messageId))) overlays.reason(line, 'Not in the loaded history'); });   // TL-38
      const unpin = line.querySelector('.pin-unpin');
      if (allowed) unpin.addEventListener('click', () => unpinNow(line, p)); else unpin.remove();
      return line;
    }));
  }

  async function unpinNow(line, p) {
    if (state.connection !== 'connected') { overlays.reason(line, 'Needs a connection'); return; }   // MR-75
    const channelId = state.open.channelId;
    const undo = unpinLocally(p.messageId);
    const result = await hub.invoke('unpin', channelId, p.messageId);
    if (result?.error) { undo?.(); overlays.reason(bar, result.error); }
  }

  const offs = [on('pins', draw), on('open', draw), on('rooms', draw), on('users', draw), on('user', draw)];
  draw();
  return () => { offs.forEach(off => off()); bar.remove(); };
}
