// The admin's home (docs/dmonly.md): every account on the server, one row each; a tap opens the conversation
// with that person. Inside the frame, in the conversation column. The rows follow the store: presence, the
// unread counts, the mutes and the last message of each conversation.

import { state, on, displayName, isMuted, showsBell } from '../store.js';
import * as router from '../router.js';
import * as frame from '../ui/frame.js';
import * as overlays from '../ui/overlays.js';
import { clone, el, replace, classes } from '../render/dom.js';
import { inline } from '../render/text.js';
import { avatar } from '../render/avatar.js';
import { ago } from '../render/time.js';
import { setTitle } from '../notify.js';

const narrow = matchMedia('(max-width: 768px)');   // the info button, as the sidebar rows had it (SH-34)

export function mount(root) {
  const page = clone('t-people');
  const list = page.querySelector('.people-list');
  const filter = page.querySelector('.people-filter');
  root.replaceChildren(page);
  frame.setTitle('home', { text: state.config.projectName });
  setTitle(state.config.projectName);

  filter.addEventListener('input', render);
  const offs = ['users', 'presence', 'dms', 'unread', 'mutes', 'user'].map(part => on(part, render));
  const clock = setInterval(render, 60_000);   // "5 minutes ago" follows the clock
  render();

  function render() {
    const me = state.user?.username;
    if (!me) return;
    const dmOf = Object.fromEntries(state.dms.map(d => [d.partner, d]));
    const needle = filter.value.trim().toLowerCase();
    const people = Object.values(state.users)
      .filter(u => u.username !== me)
      .filter(u => !needle || u.username.toLowerCase().includes(needle) || (u.displayName ?? '').toLowerCase().includes(needle))
      .sort(compare(dmOf));
    if (!people.length) { replace(list, [el('div', { class: 'people-empty' }, needle ? 'Nobody matches.' : 'Nobody has joined yet.')]); return; }
    replace(list, people.map(u => row(u, dmOf[u.username])));
  }

  return () => { offs.forEach(off => off()); clearInterval(clock); frame.closeProfile(); };
}

// The order: unread conversations, then people online, then everyone else, the bot last; inside a group the
// newest conversation first, then the name.
function compare(dmOf) {
  const rank = u => {
    const dm = dmOf[u.username];
    if (dm && (state.unread[dm.channelId] ?? 0) > 0 && !isMuted(dm.channelId)) return 0;
    if (u.isBot) return 3;
    return state.presence[u.username] ? 1 : 2;
  };
  return (a, b) => {
    const ra = rank(a), rb = rank(b);
    if (ra !== rb) return ra - rb;
    const ta = dmOf[a.username]?.lastMessageAt ?? '', tb = dmOf[b.username]?.lastMessageAt ?? '';
    if (ta !== tb) return ta < tb ? 1 : -1;
    return displayName(a.username).localeCompare(displayName(b.username), undefined, { sensitivity: 'base' });
  };
}

function row(u, dm) {
  const r = clone('t-person-row');
  const status = state.presence[u.username] ?? 'invisible';   // grey, as offline (SH-31)
  r.querySelector('.person-avatar').append(avatar(u.username, u.avatarUrl, 'medium'));
  r.querySelector('.user-status-dot').className = `user-status-dot ${status}`;
  replace(r.querySelector('.person-name-text'), inline(displayName(u.username)));
  const adminBadge = r.querySelector('.admin-badge'), botBadge = r.querySelector('.bot-badge');
  adminBadge.hidden = !u.isAdmin; replace(adminBadge, inline('👑'));
  botBadge.hidden = !u.isBot; replace(botBadge, inline('🤖'));
  r.querySelector('.person-meta').textContent = status === 'online' ? 'Online' : status === 'away' ? 'Away' : 'Offline';
  r.querySelector('.person-time').textContent = dm ? ago(dm.lastMessageAt) : '';

  const count = dm ? (state.unread[dm.channelId] ?? 0) : 0;
  const muted = dm ? isMuted(dm.channelId) : false;
  r.querySelector('.mute-bell').hidden = !(dm && showsBell(dm.channelId));
  r.querySelector('.unread-dot').hidden = !(count > 0 && muted);
  const badge = r.querySelector('.unread-badge');
  badge.hidden = !(count > 0 && !muted);
  badge.textContent = String(count);
  classes(r, { 'has-unread': count > 0 && !muted });

  const info = r.querySelector('.profile-info-btn');
  const needsButton = overlays.isTouch() || narrow.matches;   // SH-34
  info.hidden = !needsButton;
  info.addEventListener('click', e => { e.stopPropagation(); frame.toggleProfile(u.username, status, info); });
  const name = r.querySelector('.person-name');
  if (!needsButton) {
    name.addEventListener('pointerenter', e => frame.openProfile(u.username, status, e));
    name.addEventListener('pointerleave', frame.closeProfile);
  }
  const open = () => { frame.closeProfile(); router.navigate(router.dmPath(u.username)); };
  r.addEventListener('click', open);
  r.addEventListener('keydown', e => { if (e.target === r && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); open(); } });   // a div with role=link
  return r;
}
