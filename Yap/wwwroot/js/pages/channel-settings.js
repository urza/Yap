// Create Channel and Channel Settings (admin.md, AD-29 to AD-35), inside the frame. Save and Create call the
// rooms routes; a move or a delete acts at once.

import { state, on, roomById, lobby as lobbyRoom } from '../store.js';
import { api } from '../api.js';
import { clone, replace } from '../render/dom.js';
import { inline } from '../render/text.js';
import * as router from '../router.js';
import * as frame from '../ui/frame.js';

export function mount(root, { isNew, channelId }) {
  const room = isNew ? null : roomById(channelId);
  if (!state.user?.isAdmin || (!isNew && !room)) { router.navigate('/lobby', { replace: true }); return () => {}; }   // AD-29
  const lobby = lobbyRoom();
  const page = clone('t-channel-settings');
  const heading = isNew ? 'New Channel' : `#${room.name} Settings`;
  page.querySelector('.page-heading').textContent = isNew ? 'Create Channel' : 'Channel Settings';
  frame.setTitle('page', { text: heading });
  document.title = heading;

  const name = page.querySelector('#channel-name');
  const description = page.querySelector('#channel-description');
  const counter = page.querySelector('.form-counter');
  const cards = [...page.querySelectorAll('.permission-card[data-permission]')];
  const pinCards = [...page.querySelectorAll('.permission-card[data-pin]')];
  const pinnersList = page.querySelector('.pinners-list');
  const history = page.querySelector('.history-limit');
  const sinceJoined = page.querySelector('.since-joined');
  const save = page.querySelector('.save-button');
  const error = page.querySelector('.error-message');
  const back = () => router.navigate(isNew || room.id === lobby?.id ? '/lobby' : router.roomPath(room, lobby?.id));

  // the loaded values, to know when something changed (AD-34)
  const loaded = { name: room?.name ?? '', description: room?.description ?? '', permission: room?.adminOnly ? 'adminOnly' : 'everyone', history: room?.historyLimit ?? 'oneMonth', sinceJoined: room?.sinceJoined ?? true,
                   pinPermission: room?.pinPermission ?? 'adminOnly', pinners: pinnersKey(room?.pinners ?? []) };   // AD-32, AD-37 defaults
  let permission = loaded.permission;
  let pinPermission = loaded.pinPermission;
  const pinners = new Set(room?.pinners ?? []);   // the checked usernames (AD-37)
  name.value = loaded.name; description.value = loaded.description; history.value = loaded.history; sinceJoined.checked = loaded.sinceJoined;

  function current() { return { name: name.value.trim().toLowerCase(), description: description.value, permission, history: history.value, sinceJoined: sinceJoined.checked, pinPermission, pinners: pinnersKey(pinners) }; }
  function dirty() { const c = current(); return isNew ? c.name.length > 0 : Object.keys(loaded).some(k => c[k] !== loaded[k]); }
  function draw() {
    counter.textContent = `${description.value.length}/500`;
    description.placeholder = `This is the beginning of #${name.value.trim().toLowerCase() || 'channel-name'}`;
    for (const card of cards) drawCard(card, card.dataset.permission === permission);
    for (const card of pinCards) drawCard(card, card.dataset.pin === pinPermission);
    pinnersList.hidden = pinPermission !== 'selected';
    save.querySelector('span').textContent = isNew ? 'Create Channel' : 'Save Changes';
    save.disabled = !dirty();
    const bad = /\s/.test(name.value);
    page.querySelector('.name-hint').style.color = bad ? 'var(--color-danger, #ed4245)' : '';   // AD-30
    if (bad) save.disabled = true;
  }
  function drawCard(card, on) {
    card.classList.toggle('selected', on);
    replace(card.querySelector('.radio-outer'), on ? [Object.assign(document.createElement('div'), { className: 'radio-inner' })] : []);
  }
  for (const card of cards) card.addEventListener('click', () => { permission = card.dataset.permission; draw(); });
  for (const card of pinCards) card.addEventListener('click', () => { pinPermission = card.dataset.pin; draw(); });

  // Everyone but the admin and the bots, as a checklist. The admin always may pin; a bot never pins.
  const people = Object.values(state.users).filter(u => !u.isAdmin && !u.isBot).sort((a, b) => a.username.localeCompare(b.username));
  replace(pinnersList, people.length ? people.map(u => {
    const row = clone('t-pinner-row');
    const box = row.querySelector('input');
    box.checked = pinners.has(u.username);
    box.addEventListener('change', () => { if (box.checked) pinners.add(u.username); else pinners.delete(u.username); draw(); });
    row.querySelector('.pinner-name').textContent = u.displayName || u.username;
    row.querySelector('.pinner-handle').textContent = `@${u.username}`;
    return row;
  }) : [Object.assign(document.createElement('div'), { className: 'pinners-empty', textContent: 'No other people yet.' })]);
  for (const n of [name, description, history, sinceJoined]) n.addEventListener('input', draw);
  history.addEventListener('change', draw); sinceJoined.addEventListener('change', draw);

  page.querySelector('.back-button').addEventListener('click', back);
  page.querySelector('.cancel-button').addEventListener('click', back);

  save.addEventListener('click', async () => {
    const c = current();
    if (!c.name) { showError('Channel name is required.'); return; }
    save.disabled = true; save.querySelector('span').textContent = isNew ? 'Creating...' : 'Saving...';
    const body = { name: c.name, description: c.description.trim() || null, adminOnly: c.permission === 'adminOnly', historyLimit: c.history, sinceJoined: c.sinceJoined,
                   pinPermission: c.pinPermission, pinners: [...pinners] };
    try {
      if (isNew) {
        const created = await api.post('/api/rooms', body);
        // the `rooms` message may land after the answer: wait for the room before opening it (AD-34)
        for (let i = 0; i < 20 && !roomById(created.id); i++) await new Promise(r => setTimeout(r, 100));
        router.navigate(`/room/${created.id}`);
      } else {
        await api.patch(`/api/rooms/${room.id}`, body);
        back();
      }
    } catch (e) {
      showError(e.message);   // the server's reason (taken name, bad name) or 'Needs a connection'
      draw();
    }
  });

  function showError(text) { error.textContent = text; error.hidden = false; }
  function pinnersKey(names) { return [...names].sort().join(','); }   // one string, so dirty() can compare the set

  // the order (AD-33) and the danger zone (AD-35), when editing
  const order = page.querySelector('.order-section');
  const danger = page.querySelector('.danger-section');
  if (!isNew) {
    order.hidden = false;
    const list = order.querySelector('.reorder-list');
    const drawOrder = () => replace(list, state.rooms.map((r, i) => {
      const item = clone('t-reorder-item');
      item.classList.toggle('current', r.id === room.id);
      replace(item.querySelector('.reorder-name'), inline(r.name));
      const up = item.querySelector('.up'), down = item.querySelector('.down');
      up.disabled = i === 0; down.disabled = i === state.rooms.length - 1;
      up.addEventListener('click', () => api.post(`/api/rooms/${r.id}/move`, { up: true }).catch(e => showError(e.message)));
      down.addEventListener('click', () => api.post(`/api/rooms/${r.id}/move`, { up: false }).catch(e => showError(e.message)));
      return item;
    }));
    drawOrder();
    var offRooms = on('rooms', () => { if (!roomById(room.id)) router.navigate('/lobby', { replace: true }); else drawOrder(); });

    if (room.id !== lobby?.id) {
      danger.hidden = false;
      const button = danger.querySelector('.delete-button');
      let armed = false;
      button.addEventListener('click', async () => {
        if (!armed) { armed = true; button.classList.add('confirm'); button.textContent = 'Click again to confirm'; return; }
        try { await api.delete(`/api/rooms/${room.id}`); router.navigate('/lobby', { replace: true }); }
        catch { armed = false; button.classList.remove('confirm'); button.textContent = 'Delete Channel'; showError('Failed to delete channel.'); }
      });
    }
  }

  draw();
  root.replaceChildren(page);
  return () => { offRooms?.(); page.remove(); };
}
