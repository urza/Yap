// One stack of open layers: menus, pickers, sheets, dialogs, the gallery. Escape closes the top layer only
// (GEN-17). A tap outside a layer closes it. The 600 px tier decides sheet or popup (GEN-18, GEN-14).

const stack = [];

export const phone = matchMedia('(max-width: 600px)');
export const touch = matchMedia('(hover: none)');
export const isPhone = () => phone.matches;
export const isTouch = () => touch.matches;

// open({ element, onClose, outsideCloses = true, keep = [] }) returns the function that closes the layer.
// `keep` lists elements whose taps do not count as outside (the button that opened the layer).
export function open(layer) {
  const entry = { outsideCloses: true, keep: [], ...layer, closed: false };
  entry.close = () => {
    if (entry.closed) return;
    entry.closed = true;
    const i = stack.indexOf(entry);
    if (i >= 0) stack.splice(i, 1);
    entry.onClose?.();
  };
  stack.push(entry);
  return entry.close;
}

export function top() { return stack.at(-1) ?? null; }
function closeTop() { top()?.close(); }
export function closeAll() { while (stack.length) stack.at(-1).close(); }
export function anyOpen() { return stack.length > 0; }

document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && stack.length) { e.preventDefault(); closeTop(); }
});

// pointerdown, not click: a tap outside must close the layer and never press what is under it (MR-55).
document.addEventListener('pointerdown', e => {
  const layer = top();
  if (!layer || !layer.outsideCloses) return;
  const inside = layer.element.contains(e.target) || layer.keep.some(k => k?.contains(e.target));
  if (!inside) { layer.close(); if (layer.swallowOutsideTap) { e.preventDefault(); e.stopPropagation(); } }
}, true);

// Places a popup below its anchor, or above when there is no space below, and inside the window (MR-42, MR-54).
// On a phone the CSS makes the popup a bottom sheet, and this does nothing.
export function place(popup, anchor, { align = 'right', gap = 6 } = {}) {
  if (isPhone()) return;
  const a = anchor.getBoundingClientRect();
  popup.style.position = 'fixed';
  popup.style.visibility = 'hidden';
  popup.style.top = '0px'; popup.style.left = '0px';
  const w = popup.offsetWidth, h = popup.offsetHeight;
  const below = a.bottom + gap + h <= innerHeight;
  const top = below ? a.bottom + gap : Math.max(8, a.top - gap - h);
  let left = align === 'right' ? a.right - w : a.left;
  left = Math.min(Math.max(8, left), innerWidth - w - 8);
  popup.style.top = `${top}px`;
  popup.style.left = `${left}px`;
  popup.style.visibility = 'visible';
}

// A short text at an element for two seconds: the reason of a refusal (GEN-02, MR-75).
export function reason(host, text, ms = 2000) {
  host.querySelector('.row-reason')?.remove();
  const node = document.createElement('span');
  node.className = 'row-reason';
  node.textContent = text;
  host.append(node);
  setTimeout(() => node.remove(), ms);
}
