// The avatar: the picture, or a coloured disc with the first letter (MR-01 anatomy, SH-35). The colours come
// from the username with the same hash as the old server helper, so every reader sees the same pair.

import { clone } from './dom.js';

const GRADIENTS = [
  ['#667eea', '#764ba2'], ['#f093fb', '#f5576c'], ['#4facfe', '#00f2fe'], ['#43e97b', '#38f9d7'], ['#fa709a', '#fee140'],
  ['#a18cd1', '#fbc2eb'], ['#fccb90', '#d57eeb'], ['#e0c3fc', '#8ec5fc'], ['#f6d365', '#fda085'], ['#96fbc4', '#f9f586'],
  ['#ff9a9e', '#fecfef'], ['#a1c4fd', '#c2e9fb'], ['#d4fc79', '#96e6a1'], ['#84fab0', '#8fd3f4'], ['#fbc2eb', '#a6c1ee'],
  ['#ff6a88', '#ff99ac'], ['#ffd89b', '#19547b'], ['#c471f5', '#fa71cd'], ['#48c6ef', '#6f86d6'], ['#feada6', '#f5efef'],
];

// hash = c + (hash << 5) - hash, over UTF-16 units; the index is the unsigned value mod 20.
// The same formula as the old server helper, so stored colours stay stable.
export function gradient(username, angle = 135) {
  let hash = 0;
  for (let i = 0; i < (username ?? '').length; i++) hash = (username.charCodeAt(i) + ((hash << 5) - hash)) | 0;
  const [from, to] = GRADIENTS[username ? (hash >>> 0) % GRADIENTS.length : 0];
  return `linear-gradient(${angle}deg, ${from}, ${to})`;
}

// size: 'small' (24 px), 'medium' (40 px), 'large' (80 px)
export function avatar(username, pictureUrl, size = 'medium', title = username) {
  const node = clone('t-avatar');
  node.className = `avatar avatar-${size}`;
  node.title = title ?? '';   // the display name where the caller has it (MR-07 wording)
  const img = node.querySelector('.avatar-image');
  const initials = node.querySelector('.avatar-initials');
  if (pictureUrl) {
    img.src = pictureUrl; img.alt = username ?? ''; img.hidden = false;
    initials.remove();
  } else {
    img.remove();
    node.style.background = gradient(username);
    initials.textContent = username ? username[0].toUpperCase() : '?';
  }
  return node;
}
