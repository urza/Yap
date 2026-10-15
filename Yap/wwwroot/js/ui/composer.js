// The input box (input-box.md): the typing strip, the reply bar, the pill with +, the draft, the toggles and
// Send, the read-only bar, the pickers, drafts per conversation, uploads. Every action here is local first;
// the server learns of a message through sender.js (IB-10, IB-24).

import { state, on, displayName, roomById, isReadOnly, setDraft, dmByChannel } from '../store.js';
import { clone, replace } from '../render/dom.js';
import { inline, pick } from '../render/text.js';
import * as hub from '../hub.js';
import * as overlays from './overlays.js';
import * as sender from '../sender.js';
import { check as checkFiles } from '../upload.js';
import { EmojiPicker } from './emoji-picker.js';
import { setReplyTarget } from './message.js';
import { GifPicker } from './gif-picker.js';
import { keyOf } from '../offline/snapshot.js';

const MAX_TEXT = 4000;      // IB-14
const SPRITE_COLS = 20, SPRITE_COUNT = 73;   // the emoji toggle sprite: 48 px cells (IB-45)

// The pickers live once per page, not once per conversation: the GIF picker keeps its tab and folder, and
// the phone sheet keeps its tab, across a switch (PK-14, IB-41). They call the composer mounted now.
let pickers = null;

export function Composer(root) {
  const box = clone('t-composer');
  root.append(box);
  const aborter = new AbortController();
  const { signal } = aborter;
  const container = box.querySelector('.message-input-container');
  const area = box.querySelector('.message-input');
  const fileInput = box.querySelector('input[type=file]');
  const sendButton = box.querySelector('.send-button');
  const replyBar = box.querySelector('.reply-bar');
  const readonly = box.querySelector('.readonly-bar');
  const typing = box.querySelector('.typing-indicator');
  const emojiToggle = box.querySelector('.emoji-toggle-button');
  const gifToggle = box.querySelector('.gif-toggle-button');
  const emojiPane = box.querySelector('[data-picker-pane="emoji"].emoji-picker-container');
  const gifPane = box.querySelector('[data-picker-pane="gif"].emoji-picker-container');

  let key = null;              // the open conversation as the device names it: a room id, or 'dm:' and the partner (offline/snapshot.js)
  let target = null;           // { channelId, partner } for the sender; channelId is null for a direct message opened offline for the first time (OF-08)
  let replyTo = null;          // the message replied to (IB-49)
  let placeholderPick = { channelId: null, text: '' };
  let typingPick = { key: '', text: '' };
  let caret = null;            // where an emoji pick goes (IB-44)
  let typingSentAt = 0, typingTimer = 0;
  let sendWanted = false;      // Enter came before the first open: the text goes when the conversation is there

  // ---- the conversation ----

  function onOpen() {
    const open = state.open;
    const next = open ? keyOf(open) : null;
    target = open ? { channelId: open.channelId ?? null, partner: open.partner ?? null } : null;   // a direct message gains its channel id when the connection returns; its key stays
    if (next === key) { renderReadOnly(); return; }
    const typedBeforeOpen = key === null && area.value;   // text typed while the first conversation was still opening
    if (key) saveDraft();
    key = next;
    closePicker();
    if (!typedBeforeOpen) restoreDraft();
    renderPlaceholder();
    renderReadOnly();
    renderTyping();
    sender.showFor(key);
    if (sendWanted && key) { sendWanted = false; send(); }
  }

  function renderPlaceholder() {
    const open = state.open;
    if (!open) return;
    const dm = dmByChannel(open.channelId) ?? (open.partner ? { partner: open.partner } : null);
    if (dm) { area.placeholder = `Message @${dm.partner}`; return; }                      // IB-16
    if (placeholderPick.channelId !== open.channelId) {
      const list = state.texts?.placeholders;
      placeholderPick = { channelId: open.channelId, text: list?.length ? (pick(list, '') || 'Say something…') : 'Type a message...' };   // IB-16 wording
    }
    area.placeholder = placeholderPick.text;
  }

  function renderReadOnly() {
    const room = state.open ? roomById(state.open.channelId) : null;
    const locked = isReadOnly(room);                                                      // IB-62
    readonly.hidden = !locked;
    container.hidden = locked;
    renderReply();
    if (locked) closePicker();
  }

  // ---- drafts (IB-53) ----

  function saveDraft() { if (key) setDraft(key, { text: area.value, replyToId: replyTo?.id ?? null, replyToName: replyTo?.username ?? null }); }
  function restoreDraft() {
    const draft = state.drafts[key];
    area.value = draft?.text ?? '';
    // the original may be above the loaded tail after a reload: the bar still shows the name (IB-53)
    replyTo = draft?.replyToId ? state.open?.messages.find(m => m.id === draft.replyToId) ?? { id: draft.replyToId, username: draft.replyToName ?? '' } : null;
    renderReply();
    grow();
    caret = null;
  }

  // ---- the draft ----

  function grow() {
    area.style.height = 'auto';
    area.style.height = `${Math.min(area.scrollHeight, 200)}px`;                           // IB-04
  }

  area.addEventListener('input', () => {
    if (area.value.length > MAX_TEXT) area.value = area.value.slice(0, MAX_TEXT);           // IB-14
    grow();
    caret = area.selectionStart;
    saveDraft();
    noteTyping();
  });
  area.addEventListener('click', () => { caret = area.selectionStart; closePicker(); });  // a tap on the draft closes the picker (IB-42)
  area.addEventListener('keyup', () => { caret = area.selectionStart; });
  area.addEventListener('focus', () => { container.dataset.composing = ''; });
  area.addEventListener('blur', () => { delete container.dataset.composing; });
  area.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || e.isComposing) return;                                       // IB-13
    if (overlays.isTouch() || e.shiftKey) return;                                          // IB-11, IB-12
    e.preventDefault();
    send();
  });
  area.addEventListener('paste', e => {                                                    // IB-15
    const files = [...(e.clipboardData?.items ?? [])].filter(i => i.type.startsWith('image/') || i.type.startsWith('video/')).map(i => i.getAsFile()).filter(Boolean);
    if (!files.length) return;
    e.preventDefault();
    sendFiles(files);
  });

  // ---- typing (IB-59) ----

  function noteTyping() {
    if (!target?.channelId) return;   // no channel id means no connection either: nobody to tell
    clearTimeout(typingTimer);
    if (!area.value.trim()) { stopTyping(); return; }
    const now = Date.now();
    if (now - typingSentAt > 2000) { hub.send('typing', target.channelId, true); typingSentAt = now; }
    typingTimer = setTimeout(stopTyping, 3000);
  }
  function stopTyping() {
    clearTimeout(typingTimer);
    if (typingSentAt && target?.channelId) hub.send('typing', target.channelId, false);
    typingSentAt = 0;
  }
  document.addEventListener('visibilitychange', () => { if (document.visibilityState !== 'visible') stopTyping(); }, { signal });

  // The text of the strip is picked once per set of names (GEN-12), not on every refresh.
  function renderTyping() {
    const names = Object.keys(state.open?.typists ?? {}).filter(n => n !== state.user?.username);
    typing.hidden = names.length === 0;
    if (!names.length) return;
    const shown = names.map(displayName);
    const dm = state.open && (dmByChannel(state.open.channelId) || state.open.partner);
    const key = `${dm ? 'dm' : 'room'}:${shown.join(',')}`;
    if (typingPick.key !== key) {
      let text;
      if (dm) text = `${shown[0]} is typing...`;                                                               // IB-60
      else if (shown.length === 1) text = pick(state.texts?.typingOne, '{0} is typing..', shown[0]);
      else if (shown.length === 2) text = pick(state.texts?.typingTwo, '{0} and {1} are typing..', shown[0], shown[1]);
      else text = pick(state.texts?.typingMany, '{0} and {1} others are typing..', shown[0], shown.length - 1);
      typingPick = { key, text };
    }
    replace(typing.querySelector('.typing-text'), inline(typingPick.text));
  }

  // ---- the reply bar (IB-49 to IB-52) ----

  function renderReply() {
    replyBar.hidden = !replyTo || !readonly.hidden;
    container.classList.toggle('has-reply-bar', !!replyTo);
    if (replyTo) replace(replyBar.querySelector('strong'), inline(displayName(replyTo.username)));
    setReplyTarget(replyTo?.id ?? null);                                                                 // MR-37
  }
  function setReply(m) { replyTo = m; renderReply(); saveDraft(); }
  document.addEventListener('yap:reply', e => { setReply(e.detail); area.focus(); }, { signal });       // IB-50
  replyBar.querySelector('.reply-bar-close').addEventListener('pointerdown', e => e.preventDefault());   // no focus move (IB-51)
  replyBar.querySelector('.reply-bar-close').addEventListener('click', () => setReply(null));

  // ---- send (IB-18 to IB-28) ----

  function send() {
    if (!area.value.trim()) return;                                                         // IB-21
    if (!key) { sendWanted = true; return; }                                               // the conversation is still opening
    const text = area.value.slice(0, MAX_TEXT);                                             // the same cut as the input handler, for a draft that got in by another path (IB-14)
    area.value = ''; grow(); saveDraft();
    const reply = replyTo; setReply(null);
    closePicker();
    stopTyping();
    const row = sender.queueMessage({ ...target, text, replyToId: reply?.id ?? null });
    row.tappedAt = performance.now();
  }
  sendButton.addEventListener('pointerdown', e => e.preventDefault());                     // never moves the focus (IB-22)
  sendButton.addEventListener('click', send);

  // ---- attachments (IB-29 to IB-37) ----

  fileInput.addEventListener('change', () => { const files = [...fileInput.files]; fileInput.value = ''; if (files.length) sendFiles(files); });
  // + is a button that opens the chooser itself; a label around the input took no keyboard focus (U03 of the 2026-10-15 user test).
  const uploadButton = box.querySelector('.image-upload-button');
  uploadButton.addEventListener('pointerdown', e => e.preventDefault());                   // never moves the focus, like Send (IB-22)
  uploadButton.addEventListener('click', () => {
    if (container.dataset.picker) closePicker();                                            // a tap on + with a picker open only closes it (IB-42)
    else fileInput.click();
  });

  function sendFiles(files) {
    if (!key) return;
    const maxMB = state.config?.maxUploadMB ?? 100;
    const { accepted, refused } = checkFiles(files, maxMB);
    if (refused.length) warn(accepted.length ? `${accepted.length} of ${files.length} files uploaded` : 'Upload failed', refused.join('\n'));   // IB-35
    if (!accepted.length) return;
    if (state.connection !== 'connected' && accepted.reduce((s, f) => s + f.size, 0) > 25 * 1024 * 1024) { overlays.reason(container, 'Needs a connection'); return; }   // IB-64, OF-09
    const reply = replyTo; setReply(null);                                                  // IB-36
    sender.queueMessage({ ...target, replyToId: reply?.id ?? null, files: accepted });      // the sender uploads and posts (IB-34)
  }

  // drag and drop (IB-30)
  let dragDepth = 0;
  const isFileDrag = e => e.dataTransfer?.types?.includes('Files');
  container.addEventListener('dragenter', e => { if (isFileDrag(e) && ++dragDepth === 1) container.dataset.dragging = ''; });
  container.addEventListener('dragleave', () => { if (dragDepth > 0 && --dragDepth === 0) delete container.dataset.dragging; });
  container.addEventListener('dragover', e => { if (isFileDrag(e)) e.preventDefault(); });
  container.addEventListener('drop', e => {
    e.preventDefault(); dragDepth = 0; delete container.dataset.dragging;
    const files = [...(e.dataTransfer?.files ?? [])].filter(f => f.type.startsWith('image/') || f.type.startsWith('video/'));
    if (files.length) sendFiles(files);
  });
  document.addEventListener('dragover', e => e.preventDefault(), { signal });
  document.addEventListener('drop', e => e.preventDefault(), { signal });                  // a drop elsewhere does nothing

  function warn(title, message) {
    const modal = clone('t-warning-modal');
    modal.querySelector('.warning-modal-title').textContent = title;
    modal.querySelector('.warning-modal-message').textContent = message;
    const close = () => layer();
    for (const b of modal.querySelectorAll('button')) b.addEventListener('pointerdown', e => e.preventDefault());   // the dialog never moves the focus (IB-35)
    modal.querySelector('.warning-modal-close').addEventListener('click', close);
    modal.querySelector('.warning-modal-button').addEventListener('click', close);
    modal.addEventListener('click', e => { if (e.target === modal) close(); });
    box.prepend(modal);
    const layer = overlays.open({ element: modal.querySelector('.warning-modal'), outsideCloses: false, onClose: () => modal.remove() });
  }

  // ---- the pickers (IB-38 to IB-48) ----

  const handlers = { insert: insertEmoji, sendGif, warn };

  function mountPickers() {
    pickers ??= {
      emoji: EmojiPicker({ onPick: v => pickers.current.insert(v) }),
      gif: GifPicker({ onSend: g => pickers.current.sendGif(g), onWarn: (t, m) => pickers.current.warn(t, m) }),
      combined: null, current: handlers,
    };
    pickers.current = handlers;
    layoutPickers();
  }

  // Desktop: two panes. Phone: one sheet with two tabs in the emoji pane (IB-41, PK-45).
  function layoutPickers() {
    if (!pickers) return;
    const { emoji, gif } = pickers;
    if (overlays.isPhone()) {
      if (!pickers.combined) {
        const combined = clone('t-combined-picker');
        for (const tab of combined.querySelectorAll('.combined-tab')) tab.addEventListener('click', () => { combined.dataset.activeTab = tab.dataset.combinedTab; });
        pickers.combined = combined;
      }
      // Every time, not only when the sheet is made: the unmount below takes the two pickers out of the sheet,
      // and the desktop layout takes them into its own panes. An append of an element that is there already moves nothing.
      pickers.combined.querySelector('[data-combined-pane="gifs"]').append(gif.element);
      pickers.combined.querySelector('[data-combined-pane="emoji"]').append(emoji.element);
      replace(emojiPane, [pickers.combined]);
      replace(gifPane, []);
    } else {
      replace(emojiPane, [emoji.element]);
      replace(gifPane, [gif.element]);
    }
  }
  overlays.phone.addEventListener('change', () => { closePicker(); layoutPickers(); }, { signal });   // IB-43

  function openPicker(which) {
    mountPickers();
    container.dataset.picker = which;
    if (which === 'emoji') pickers.emoji.opened();
    if (which === 'gif' || overlays.isPhone()) pickers.gif.opened();
    emojiToggle.classList.toggle('open', which === 'emoji');
  }
  function closePicker() {
    delete container.dataset.picker;
    emojiToggle.classList.remove('open');
  }
  for (const toggle of [emojiToggle, gifToggle]) {
    toggle.addEventListener('pointerdown', e => e.preventDefault());                       // never moves the focus (IB-58)
    toggle.addEventListener('click', () => {
      const which = toggle.dataset.pickerToggle;
      if (container.dataset.picker) closePicker(); else openPicker(which);                 // IB-42
    });
  }
  for (const backdrop of box.querySelectorAll('.emoji-picker-backdrop')) backdrop.addEventListener('click', closePicker);
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && container.dataset.picker && !overlays.anyOpen()) closePicker(); }, { signal });   // IB-17

  // IB-44: at the caret, before a selection, at the end when the draft was never focused.
  function insertEmoji(value) {
    if (area.value.length + value.length > MAX_TEXT) return;                                // the limit holds for a pick too; no input event runs here (IB-14)
    const pos = caret ?? area.value.length;
    area.value = area.value.slice(0, pos) + value + area.value.slice(pos);
    caret = pos + value.length;
    area.setSelectionRange(caret, caret);
    grow(); saveDraft(); noteTyping();
  }

  function sendGif(gif) {                                                                  // IB-47, PK-20
    if (!key) return;
    closePicker();
    const reply = replyTo; setReply(null);
    const row = sender.queueMessage({ ...target, replyToId: reply?.id ?? null, gif });
    row.tappedAt = performance.now();
  }

  // the toggle's random silhouette (IB-45)
  const icon = box.querySelector('.emoji-toggle-icon');
  const randomCell = () => {
    const index = Math.floor(Math.random() * SPRITE_COUNT);
    const col = index % SPRITE_COLS, row = Math.floor(index / SPRITE_COLS);
    for (const [k, v] of [['--color-col', col], ['--color-row', row], ['--grey-col', col], ['--grey-row', row]]) icon.style.setProperty(k, String(v));
  };
  randomCell();
  emojiToggle.addEventListener('mouseenter', randomCell);

  const offs = [on('messages', d => { if (d?.type === 'reset') onOpen(); }), on('typists', renderTyping), on('rooms', renderReadOnly), on('user', renderReadOnly), on('texts', renderPlaceholder), on('users', renderTyping)];
  if (pickers) { pickers.current = handlers; layoutPickers(); }
  onOpen();
  return () => {
    saveDraft(); stopTyping(); offs.forEach(off => off()); aborter.abort();
    if (pickers) for (const p of [pickers.emoji.element, pickers.gif.element, pickers.combined]) p?.remove();   // the pickers survive; the box does not
    box.remove();
  };
}
