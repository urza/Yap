// The list of messages (timeline.md): the rows, the follow rule, the New messages pill, the down arrow, older
// pages, the top note, the lobby lines. The rows themselves are drawn by ui/message.js. The timeline is the one
// component that updates rows in place, because it can hold hundreds of them.

import { state, on, prependOlder, roomById, dmByChannel, lobby } from '../store.js';
import { clone, el, replace } from '../render/dom.js';
import { inline, pick } from '../render/text.js';
import { messageTime } from '../render/time.js';
import * as hub from '../hub.js';
import * as overlays from './overlays.js';
import { renderRow, needsHeader, rowReplaced, touchScrolled } from './message.js';

let current = null;   // the mounted instance, for jumpTo

export function Timeline(root) {
  const list = clone('t-messages');
  const flow = list.querySelector('.messages-flow');
  const top = flow.querySelector('.messages-top');
  const spacer = flow.querySelector('.messages-spacer');
  root.append(list);
  const aborter = new AbortController();
  const { signal } = aborter;

  const rows = new Map();          // message id -> { node, message }
  let pendingNodes = [];
  let following = true;
  let loading = false;
  let pill = null;
  let pillPick = '';
  let down = null;                 // the down arrow (TL-40)
  let catching = null;             // the catching-up pill (TL-39)
  let stale = false;               // the rows on screen are the device's copy, or older than the connection (TL-39)
  let unread = false;              // the server counted a message in the open conversation and markRead has not answered yet (NT-07)
  let shownChannel = null;         // the channel the rows belong to

  // ---- drawing ----

  function ordered() { return [...(state.open?.messages ?? []), ...state.pending]; }

  // A reset for the same conversation (a reconnect, TL-35) keeps the view where it is; a new one opens at the bottom (TL-01).
  function reset() {
    const same = state.open && state.open.channelId === shownChannel;
    const keptTop = list.scrollTop, keptFollowing = following;
    for (const r of rows.values()) r.node.remove();
    rows.clear();
    pendingNodes.forEach(n => n.remove()); pendingNodes = [];
    flow.querySelectorAll('.lobby-line').forEach(n => n.remove());
    hidePill(); hideDown();
    shownChannel = state.open?.channelId ?? null;
    unread = state.unread[shownChannel] > 0;   // a count from before this open; chat.js marks it read on open, this is the fallback
    if (!state.open) { renderTop(); return; }
    let prev = null;
    for (const m of ordered()) { flow.append(makeRow(m, prev)); prev = m; }
    // An older page still in flight belongs to the rows just removed: its answer is dropped (loadOlder), and
    // the spinner must not outlive the rows it was loading for. Without this, a reconnect that re-opened the
    // conversation while a page was loading showed "loading older" for ever (the token was never released).
    loadToken++; loading = false;
    renderTop();
    if (same && !keptFollowing) {
      following = false; list.scrollTop = keptTop;
      syncDown();   // the same value as before gives no scroll event, so the arrow is checked here (TL-40)
      if (list.scrollTop <= 100) loadOlder();   // still at the top after a reconnect: the page the person waited for (TL-06)
      return;
    }
    following = true;
    scrollToBottom();
  }

  function makeRow(m, prev) {
    const node = renderRow(m, { header: needsHeader(prev, m) });
    if (m.pending) pendingNodes.push(node); else rows.set(m.id, { node, message: m });
    return node;
  }

  // Draws one row again in place, with the header rule applied (MR-01, MR-09).
  function redraw(m, prev) {
    const r = rows.get(m.id);
    if (!r || r.node.classList.contains('editing')) return;   // the editor and its text stay (MR-62); the row refreshes after
    const node = renderRow(m, { header: needsHeader(prev, m) });
    r.node.replaceWith(node);
    rowReplaced(r.node, node);   // the touch bar and an open picker or menu follow the row (MR-51)
    r.node = node; r.message = m;
  }

  function redrawAll() {
    const all = state.open?.messages ?? [];
    all.forEach((m, i) => redraw(m, all[i - 1] ?? null));
    keepPlace();
  }

  function neighbours(index) {
    const all = state.open.messages;
    return { prev: all[index - 1] ?? null, next: all[index + 1] ?? null };
  }

  function onMessages(detail) {
    if (!state.open) { reset(); return; }
    switch (detail?.type) {
      case 'reset': reset(); break;
      case 'add': {
        const m = detail.message;
        const index = state.open.messages.indexOf(m);
        const { prev, next } = neighbours(index);
        const node = renderRow(m, { header: needsHeader(prev, m) });
        rows.set(m.id, { node, message: m });
        const nextNode = next ? rows.get(next.id)?.node : pendingNodes[0];
        if (nextNode) flow.insertBefore(node, nextNode); else flow.append(node);
        if (next) redraw(next, m);
        arrived(m);
        break;
      }
      case 'replace': {
        const m = detail.message;
        const { prev } = neighbours(state.open.messages.indexOf(m));
        redraw(m, prev);
        keepPlace();
        break;
      }
      case 'remove': {
        const r = rows.get(detail.id);
        if (!r) break;
        r.node.remove(); rows.delete(detail.id);
        const all = state.open.messages;
        const i = all.findIndex(x => x.timestamp >= r.message.timestamp && x.id !== detail.id);   // the row that followed may now start a group
        if (i >= 0) redraw(all[i], all[i - 1] ?? null);
        break;
      }
      case 'older': {
        const before = list.scrollHeight, beforeTop = list.scrollTop;
        const all = state.open.messages;
        const nodes = [];
        for (let i = 0; i < detail.count; i++) nodes.push(makeRow(all[i], all[i - 1] ?? null));
        if (all[detail.count]) redraw(all[detail.count], all[detail.count - 1]);   // the old first row may lose its header
        spacer.after(...nodes);
        loading = false;
        renderTop();   // the spinner leaves BEFORE the measurement below. Measured with it still in place, the rows
                       // ended 49 px above where the scroll put them, and Chrome scored the whole prepend as a layout
                       // shift: it discounts a compensated move only when the scroll matches it within 3 px.
        list.scrollTop = beforeTop + (list.scrollHeight - before);   // the view does not move (TL-06)
        break;
      }
    }
  }

  // The pending rows: a send goes to the bottom (TL-14); progress and other tabs do not move the view (TL-12).
  function onPending(detail) {
    pendingNodes.forEach(n => n.remove()); pendingNodes = [];
    let prev = state.open?.messages.at(-1) ?? null;
    for (const m of state.pending) { flow.append(makeRow(m, prev)); prev = m; }
    if (detail?.type === 'queued') { following = true; scrollToBottom(); hidePill(); }
    else keepPlace();
  }

  // A new row arrived: follow, or show the pill (TL-10, TL-16). The read mark is not sent from here: the server
  // gives a reader no count (NT-07), so for a person who is looking there is nothing to answer. The `unread`
  // event drives markRead instead (onUnread), one call per counted message and none per message in a busy room.
  function arrived(m) {
    const own = m.username === state.user?.username;
    if (following) scrollToBottom();
    else if (!own) showPill();
  }

  // The server counted a message in the open conversation: the person was hidden, away or idle when it came.
  // Answer with markRead as soon as the person sees it (NT-07).
  function onUnread() {
    if (!state.open || !(state.unread[state.open.channelId] > 0)) return;
    unread = true;
    markReadIfSeen();
  }

  // ---- the top note (TL-07, TL-09, TL-34) ----

  function renderTop() {
    const open = state.open;
    if (!open) { replace(top, []); return; }
    if (loading) { replace(top, [clone('t-loading-more')]); return; }
    if (open.loading && state.connection !== 'offline') { replace(top, [clone('t-loading-tail')]); return; }   // the first answer is on its way (TL-02)
    if (open.hasMore) {
      if (state.connection !== 'connected') { replace(top, [el('div', { class: 'history-start' }, 'Needs a connection')]); return; }
      if (list.scrollHeight <= list.clientHeight + 1) {
        const button = clone('t-load-earlier');
        button.addEventListener('click', loadOlder);
        replace(top, [button]);
      } else replace(top, []);
      return;
    }
    const dm = dmByChannel(open.channelId) ?? (open.partner ? { partner: open.partner } : null);
    if (dm) {
      const note = clone('t-dm-start');
      note.querySelector('strong').textContent = dm.partner;
      replace(top, [note]);
      return;
    }
    const room = roomById(open.channelId);
    const note = clone('t-history-start');
    if (open.restricted) note.append(el('span', null, 'Older messages are not available in this channel'));
    else if (room?.description) note.append(...room.description.split('\n').map(line => el('div', null, line)));
    else note.append(el('span', null, `This is the beginning of #${room?.name ?? ''}`));
    replace(top, [note]);
  }

  let loadToken = 0;   // the older request in flight; a reset invalidates it
  async function loadOlder() {
    const open = state.open;
    if (!open || loading || !open.hasMore || !open.messages.length || state.connection !== 'connected') return;
    const token = ++loadToken;
    loading = true; renderTop();
    const oldest = open.messages[0];
    const startedAt = performance.now();
    const page = await hub.invoke('older', open.channelId, oldest.timestamp, oldest.id);
    if (token !== loadToken || state.open !== open) return;   // the rows changed meanwhile; reset() released the spinner
    if (page?.error) { loading = false; renderTop(); overlays.reason(list.parentElement, page.error); return; }   // GEN-02
    prependOlder(page);
    hub.report('older', performance.now() - startedAt);   // the rows are in place: the 'older' change ran inside prependOlder (AD-26)
  }

  // ---- following and the pill (TL-10 to TL-18) ----

  const distanceFromBottom = () => list.scrollHeight - list.scrollTop - list.clientHeight;

  // One scroll per frame, however many rows, reactions and cards asked for one: each scrollHeight read forces a
  // layout of the whole list, and on the 2026-10-18 load test those layouts were a share of a tab that stopped
  // answering. The frame callback runs before the paint, so the bottom is in view as early as before.
  let scrollFrame = 0;
  const watched = new WeakSet();   // images with a load listener already: a second call must not add another
  function scrollToBottom() {
    if (scrollFrame) return;
    scrollFrame = requestAnimationFrame(() => {
      scrollFrame = 0;
      list.scrollTop = list.scrollHeight;
      // again after the images and posters of the newest rows load (TL-04)
      for (const img of flow.querySelectorAll('img')) {
        if (img.complete || watched.has(img)) continue;
        watched.add(img);
        img.addEventListener('load', () => { if (following) scrollToBottom(); }, { once: true });
      }
    });
  }

  function keepPlace() { if (following) scrollToBottom(); }   // a reaction or a card never moves the newest row out of view (TL-10)

  let lastTop = 0;
  list.addEventListener('scroll', () => {
    touchScrolled(list.scrollTop);                                            // MR-51: 50 px hides the touch bar
    const up = list.scrollTop < lastTop;
    lastTop = list.scrollTop;
    const distance = distanceFromBottom();
    if (up && distance > 80) following = false;                               // only an upward scroll stops the follow (TL-11)
    else if (distance <= 80 && !following) { following = true; hidePill(); markReadIfSeen(); }   // TL-13
    syncDown(distance);                                                       // TL-40
    if (list.scrollTop <= 100) loadOlder();                                   // TL-06
  }, { passive: true, signal });

  // The list and its container: a taller input box or an open sheet shrinks the list, and the bottom must stay in view (TL-10).
  // The pill and the arrow are placed again: the strip they sit above moved with the list's bottom edge.
  const sizes = new ResizeObserver(() => {
    if (following) list.scrollTop = list.scrollHeight;
    if (state.open?.hasMore && !loading) renderTop();
    if (pill) place(pill);
    if (down) place(down);
  });
  sizes.observe(flow); sizes.observe(list);

  // Above the typing strip and the input box, never over the draft (TL-16, TL-40). Measured against the positioned
  // column the node sits in: the timeline root has `display: contents` and so no box of its own.
  function place(node) {
    const column = node.offsetParent ?? list.parentElement;
    const strip = column.querySelector('.typing-indicator-container');
    const top = strip ? strip.getBoundingClientRect().top : list.getBoundingClientRect().bottom;
    node.style.bottom = `${Math.max(0, column.getBoundingClientRect().bottom - top) + 8}px`;
  }

  function showPill() {
    if (pill) return;
    hideDown();   // the pill says more and does the same (TL-40)
    pill = clone('t-pill');
    pillPick ||= pick(state.texts?.pillTexts, 'New messages');   // the phrase does not change while shown (TL-17)
    pill.querySelector('.pill-text').textContent = pillPick;
    pill.addEventListener('click', () => { following = true; scrollToBottom(); hidePill(); markReadIfSeen(); });
    list.parentElement.append(pill);
    place(pill);
  }
  function hidePill() { pill?.remove(); pill = null; pillPick = ''; }

  // ---- the down arrow (TL-40) ----
  // The way back after a jump to a pinned message or a long read up: the view is more than a screen above the
  // bottom and there is no pill. The pill wins, because it also says that something new arrived. A few rows up
  // show nothing: the person can scroll that far by hand.
  function syncDown(distance = distanceFromBottom()) {
    if (following || pill || distance <= list.clientHeight) { hideDown(); return; }
    if (down) return;
    down = clone('t-down-arrow');
    down.addEventListener('click', () => { following = true; scrollToBottom(); hideDown(); markReadIfSeen(); });
    list.parentElement.append(down);
    place(down);
  }
  function hideDown() { down?.remove(); down = null; }

  // ---- the catching-up pill (TL-39) ----
  // The rows came from the device, or the connection went away since they came: the server has not confirmed them.
  // The pill floats over the top of the list and takes no space, so no row moves. It ends with the server's answer
  // (setOpen from openRoom), not with the socket: the dot reports the socket, the pill reports the content.
  function renderCatchingUp() {
    const show = stale && !!state.open && !state.open.loading;
    if (!show) { catching?.remove(); catching = null; return; }
    if (!catching) { catching = clone('t-catching-up'); list.parentElement.append(catching); }
    const failed = state.connection === 'offline' || state.connectionFailed;
    catching.classList.toggle('failed', failed);
    catching.querySelector('.catching-up-text').textContent = failed ? 'No connection. Showing older messages.' : 'Catching up…';
  }

  // NT-07: unread rows, the view at the bottom, and the person looking (hub.looking: visible, not away, not idle).
  function markReadIfSeen() {
    if (!unread || !following || !hub.looking()) return;
    hub.send('markRead', state.open.channelId);
    unread = false;
  }

  document.addEventListener('visibilitychange', () => {   // TL-15, NT-07
    if (document.visibilityState === 'visible' && following) { scrollToBottom(); markReadIfSeen(); }
  }, { signal });
  const offInput = (() => { const h = () => markReadIfSeen(); document.addEventListener('pointerdown', h, { capture: true, passive: true, signal }); document.addEventListener('keydown', h, { capture: true, passive: true, signal }); return () => {}; })();   // input after an idle away (PR-13)

  // ---- lobby lines (MR-73) ----
  function onPresence(detail) {
    if (!detail?.changes) return;                          // a bootstrap replaces the map without a detail
    if (!state.open || state.open.channelId !== lobby()?.id) return;
    let added = false;
    for (const { username, kind } of detail.changes) {    // one tick of changes, a line per join or leave (MR-73)
      if (kind !== 'join' && kind !== 'leave') continue;
      const line = clone('t-system-line');
      line.classList.add('lobby-line');
      const text = kind === 'join' ? pick(state.texts?.joinLines, '{0} joined the chat', username) : pick(state.texts?.leaveLines, '{0} left the chat', username);
      replace(line.querySelector('.message-content'), inline(text));
      flow.append(line);
      added = true;
    }
    if (added && following) scrollToBottom();
  }

  // The labels follow the clock: at midnight "today" becomes "Yesterday" (MR-06).
  const clock = setInterval(() => {
    for (const { node, message } of rows.values()) {
      const time = node.querySelector('.message-time');
      if (time) time.textContent = messageTime(message.timestamp);
    }
  }, 60_000);

  // ---- jump to a message (MR-34) ----
  async function jumpTo(id) {
    for (let tries = 0; tries < 20 && !rows.has(id) && state.open?.hasMore; tries++) {
      await loadOlder();
      await new Promise(r => setTimeout(r, 50));
    }
    const r = rows.get(id);
    if (!r) return false;   // older than the loaded history, or gone: the caller says so (SH-49)
    following = false;
    const target = r.node.offsetTop - list.offsetTop - list.clientHeight / 2 + r.node.offsetHeight / 2;
    const start = list.scrollTop, distance = target - start, t0 = performance.now();
    const ease = t => 1 - Math.pow(1 - t, 3);
    const step = now => {
      const k = Math.min((now - t0) / 600, 1);
      list.scrollTop = start + distance * ease(k);
      if (k < 1) requestAnimationFrame(step);
      else { r.node.classList.add('highlight-message'); setTimeout(() => r.node.classList.remove('highlight-message'), 2000); }
    };
    requestAnimationFrame(step);
    return true;
  }

  const offs = [
    on('messages', onMessages), on('pending', onPending), on('presence', onPresence),
    on('connection', () => { if (state.connection !== 'connected') stale = true; renderTop(); renderCatchingUp(); }),   // a lost socket: the rows may be old (TL-39)
    on('open', () => { stale = !!state.open?.fromDevice; renderCatchingUp(); }),                                           // the server's answer ends the pill
    on('rooms', renderTop),
    on('unread', onUnread),
    on('user', () => markReadIfSeen()), on('presence', () => markReadIfSeen()),   // Away ended, here or on the server: the three conditions may hold again (NT-07)
    on('users', redrawAll),            // a new display name or avatar shows on every row (ST-07)
    on('favoriteGifIds', redrawAll),   // the stars follow the account (PK-36)
    on('emoji', redrawAll),            // the quick reactions of the bar (MR-41)
  ];
  current = { jumpTo };
  reset();
  return () => {
    offs.forEach(off => off()); aborter.abort(); sizes.disconnect(); clearInterval(clock); hidePill(); hideDown(); catching?.remove(); list.remove();
    if (current?.jumpTo === jumpTo) current = null;
  };
}

export function jumpTo(id) { return current?.jumpTo(id) ?? Promise.resolve(false); }
