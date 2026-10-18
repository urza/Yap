// The app's own dialogs: a title, a text and the buttons, in the delete dialog's clothes (MR-65), so every question
// looks the same in every theme. The browser's confirm() and alert() ignore the theme, so nothing in the client
// uses them. confirm() resolves true on the red button and false on Cancel, Escape or a tap outside; the admin
// pages and the sign-out (SH-15) ask it before a loss that deserves a second look. `danger` puts the Danger Zone's
// red frame on the dialog: the admin's account deletion asks in the same colour it was offered in (AD-38).
// notice() has one button and resolves when the dialog closes. The message row has its own dialog with the
// message preview (message.js).

import { el } from '../render/dom.js';
import * as overlays from './overlays.js';

export function confirm({ title, text, action = 'Delete', danger = false }) {
  return new Promise(resolve => {
    let answer = false;
    const close = dialog({ title, text, danger, onClose: () => resolve(answer) },
      el('button', { class: 'confirm-cancel', type: 'button', onclick: () => close() }, 'Cancel'),
      el('button', { class: 'confirm-delete', type: 'button', onclick: () => { answer = true; close(); } }, action));
  });
}

export function notice({ title, text, action = 'OK' }) {
  return new Promise(resolve => {
    const close = dialog({ title, text, onClose: resolve },
      el('button', { class: 'confirm-ok', type: 'button', onclick: () => close() }, action));
  });
}

// The dialog and its overlay; returns the function that closes it. The last button has the focus: Enter takes it,
// Escape closes (overlays.js).
function dialog({ title, text, danger = false, onClose }, ...buttons) {
  const box = el('div', { class: `delete-confirm-dialog${danger ? ' danger' : ''}`, role: 'dialog', 'aria-modal': 'true' },
    el('h3', { class: 'delete-confirm-title' }, title),
    el('p', { class: 'delete-confirm-text' }, text),
    el('div', { class: 'delete-confirm-actions' }, ...buttons));
  const overlay = el('div', { class: 'delete-confirm-overlay' }, box);
  overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
  document.body.append(overlay);
  const close = overlays.open({ element: box, outsideCloses: false, onClose: () => { overlay.remove(); onClose(); } });
  buttons.at(-1).focus();
  return close;
}
