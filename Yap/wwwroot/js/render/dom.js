// DOM construction without HTML strings. Every value from the server lands in the DOM through textContent
// or an attribute, never through innerHTML (architecture principle 6, MR-10).

// el('div', { class: 'row', hidden: true, onclick: fn }, 'text', node, ...)
// Attribute rules: a function value becomes a listener ('onclick'), a boolean value toggles a boolean
// attribute, null and undefined are skipped, 'dataset' and 'style' objects spread.
export function el(tag, attrs = null, ...children) {
  const node = document.createElement(tag);
  if (attrs) for (const [name, value] of Object.entries(attrs)) {
    if (value === null || value === undefined || value === false) continue;
    if (name.startsWith('on') && typeof value === 'function') node.addEventListener(name.slice(2), value);
    else if (name === 'dataset') Object.assign(node.dataset, value);
    else if (name === 'style' && typeof value === 'object') Object.assign(node.style, value);
    else if (value === true) node.setAttribute(name, '');
    else node.setAttribute(name, value);
  }
  append(node, children);
  return node;
}

export function append(parent, children) {
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    parent.append(child instanceof Node ? child : String(child));
  }
  return parent;
}

const templates = new Map();   // id -> <template>, looked up once

// clone('t-room-row') returns the first element inside <template id="t-room-row"> in index.html.
export function clone(id) {
  let template = templates.get(id);
  if (!template) {
    template = document.getElementById(id);
    if (!template) throw new Error(`missing template ${id}`);
    templates.set(id, template);
  }
  return template.content.firstElementChild.cloneNode(true);
}

// Replaces the children of a node with a list. The one function every component uses to redraw.
export function replace(parent, children) {
  parent.replaceChildren();
  return append(parent, children);
}

// Class toggles from an object: classes(node, { open: true, muted: false })
export function classes(node, map) {
  for (const [name, on] of Object.entries(map)) node.classList.toggle(name, !!on);
  return node;
}
