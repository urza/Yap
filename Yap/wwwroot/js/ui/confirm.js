// A yes/no question in the delete dialog's clothes (MR-65): a title, a text and two buttons. It resolves true
// on the red button and false on Cancel, Escape or a tap outside. The admin pages ask it before a loss that
// deserves a second look; the message row has its own dialog with the message preview (message.js).
// `danger` puts the Danger Zone's red frame on the dialog: the admin's account deletion asks in the same colour
// it was offered in (AD-38).

import { el } from '../render/dom.js';
import * as overlays from './overlays.js';

export function confirm({ title, text, action = 'Delete', danger = false }) {
  return new Promise(resolve => {
    let answer = false;
    const dialog = el('div', { class: `delete-confirm-dialog${danger ? ' danger' : ''}`, role: 'dialog', 'aria-modal': 'true' },
      el('h3', { class: 'delete-confirm-title' }, title),
      el('p', { class: 'delete-confirm-text' }, text),
      el('div', { class: 'delete-confirm-actions' },
        el('button', { class: 'confirm-cancel', type: 'button', onclick: () => close() }, 'Cancel'),
        el('button', { class: 'confirm-delete', type: 'button', onclick: () => { answer = true; close(); } }, action)));
    const overlay = el('div', { class: 'delete-confirm-overlay' }, dialog);
    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    document.body.append(overlay);
    const close = overlays.open({ element: dialog, outsideCloses: false, onClose: () => { overlay.remove(); resolve(answer); } });
    dialog.querySelector('.confirm-delete').focus();   // Enter confirms, Escape cancels
  });
}
