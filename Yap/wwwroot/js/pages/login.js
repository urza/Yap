// The login page (sessions.md, AU-02 to AU-09, AU-24): one box, one button. The server's check decides what
// the button does for a name: create, sign in through smart login, ask for a code, or show the guide.

import { state } from '../store.js';
import { api, ApiError } from '../api.js';
import { clone, el, replace } from '../render/dom.js';
import { pick } from '../render/text.js';
import * as session from '../session.js';
import { installed } from '../push.js';
import * as router from '../router.js';
import * as passkeys from '../passkeys.js';

const RULES = [
  [u => u.length < 2, 'Username must be at least 2 characters'],
  [u => u.length > 23, 'Username must be at most 23 characters'],
  [u => /[^a-z0-9_.]/.test(u), 'Only lowercase letters, numbers, _ and . allowed'],
  [u => u.includes('..'), 'Cannot have consecutive periods'],
];

// The box lowercases and trims as the person types (AU-02). Returns the failing rule or null.
export function usernameRule(value, { blurred = true } = {}) {
  for (const [fails, text] of RULES) if (fails(value)) return text.startsWith('Username must be at least') && !blurred ? null : text;
  return null;
}

export function mount(root) {
  const page = clone('t-login');
  const texts = state.texts ?? {};
  const project = state.config?.projectName ?? 'Yap';
  page.querySelector('.login-heading').textContent = pick(texts.loginHeadings, 'Welcome to {0}!', project);
  const button = page.querySelector('.join-button');
  const joinText = pick(texts.loginButtons, 'Join Chat');
  button.textContent = joinText;
  const input = page.querySelector('.username-input');
  input.placeholder = pick(texts.loginPlaceholders, 'Enter your username');
  page.querySelector('.build-info').textContent = document.documentElement.dataset.build ?? '';
  const error = page.querySelector('.validation-error');
  const hint = page.querySelector('.new-account-hint');
  const closed = page.querySelector('.closed-message');
  const guide = page.querySelector('.multidevice-guide');
  const form = page.querySelector('.username-form');
  const waiting = page.querySelector('.waiting-message');
  document.title = project;

  let check = null;        // the server's answer for the current name
  let checkTimer = 0, seq = 0, blurred = false, poll = 0;
  let joinWanted = false;  // Enter came before the server's answer: join as soon as it arrives

  function username() { return input.value; }

  function show(part, text) {
    for (const n of [error, hint, closed, guide]) n.hidden = n !== part;
    if (part === error) error.textContent = text;
    if (part === hint) hint.querySelector('strong').textContent = username();
    input.classList.toggle('invalid', part === error);
  }

  // Every keystroke: the shape rules at once (AU-02), the server's answer a moment later.
  input.addEventListener('input', () => {
    const normalised = input.value.toLowerCase().trim();
    if (normalised !== input.value) { const pos = input.selectionStart; input.value = normalised; input.setSelectionRange(pos, pos); }
    check = null;
    button.disabled = true;
    clearTimeout(checkTimer);
    const rule = usernameRule(username(), { blurred });
    if (rule) { show(error, rule); return; }
    if (!username()) { show(null); return; }
    show(null);
    checkTimer = setTimeout(runCheck, 250);
  });
  // An empty box has nothing to judge: leaving it untouched shows no error (and moves nothing under a pointer).
  input.addEventListener('blur', () => { blurred = true; const rule = usernameRule(username()); if (rule && username()) show(error, rule); });
  input.addEventListener('keydown', e => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    if (!button.disabled) { join(); return; }
    if (!usernameRule(username()) && username()) { joinWanted = true; clearTimeout(checkTimer); runCheck(); }   // the answer is on its way
  });

  async function runCheck() {
    const name = username(), my = ++seq;
    if (!name) return;
    let answer;
    try { answer = await api.get(`/api/auth/check?username=${encodeURIComponent(name)}`); }
    catch (e) { if (my === seq) show(error, e instanceof ApiError && e.status === 0 ? 'Needs a connection' : e.message); return; }
    if (my !== seq || name !== username()) return;
    check = answer;
    switch (answer.state) {
      case 'invalid': show(error, answer.reason ?? 'That username is not allowed.'); break;
      case 'pending': show(error, 'This username is already waiting for approval'); break;
      case 'closed': show(closed); break;                                       // AU-05: the form stays for members
      case 'free': show(hint); button.disabled = false; break;
      default: show(null); button.disabled = false;                             // smart, verify, guide
    }
    if (joinWanted) { joinWanted = false; if (!button.disabled) join(); }
  }

  button.addEventListener('click', join);

  async function join() {
    if (!check) return;
    const name = username();
    if (check.state === 'verify') { router.navigate(`/verify/${encodeURIComponent(name)}`); return; }   // AU-08
    if (check.state === 'guide') { showGuide(name); return; }
    button.disabled = true;
    try {
      const res = await fetch('/api/auth/signin', { method: 'POST', credentials: 'same-origin', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: name }) });
      if (res.status === 202) { wait(name); return; }                           // approval required (AU-05); 202 counts as ok, so it comes first
      if (res.ok) { await signedIn(); return; }
      const problem = await res.json().catch(() => null);
      show(error, problem?.detail ?? 'Something went wrong');
    } catch { show(error, 'Needs a connection'); }
    finally { button.disabled = !check; }
  }

  // A device that sent no zone yet tells the server what the browser knows (ST-19).
  async function signedIn() {
    const ok = await session.signedIn();
    if (ok && state.user && !state.user.timeZone) {
      const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
      api.patch('/api/account', { timeZone: zone && zone !== 'Etc/Unknown' ? zone : null, locale: navigator.language }).catch(() => {});
    }
  }

  // AU-24: the guide, with the wording of the installed app when it runs as one.
  function showGuide(name) {
    const app = installed();
    replace(guide.querySelector('.guide-intro'), app
      ? ['This installed app starts signed out — your login for ', el('strong', null, name), ' stayed in the browser you joined from.']
      : ['To log in as ', el('strong', null, name), ' on this device:']);
    replace(guide.querySelector('.guide-steps'), app
      ? [el('li', null, 'Open ', el('strong', null, project), ' in your browser — you should still be logged in there'),
         el('li', null, 'In ', el('strong', null, 'Settings → Login Link'), ', copy your link (create one if there isn\'t one), or just read its code'),
         el('li', null, 'Open the link here, or come back and type the code')]
      : [el('li', null, 'Open the app on the device where you\'re already logged in'),
         el('li', null, 'In ', el('strong', null, 'Settings → Login Link'), ', copy your link (create one if there isn\'t one), or just read its code'),
         el('li', null, 'Open the link on this device, or come back here and type the code')]);
    show(guide);
    button.disabled = true;
  }
  guide.querySelector('.retry-link').addEventListener('click', () => { show(null); runCheck(); });

  // Passkeys (AU-28): the autofill offers them in the box as soon as it is tapped; the button opens the browser's
  // own passkey sheet, for a browser without the autofill and for a phone that holds the key (QR code).
  const passkeyButton = page.querySelector('.passkey-button');
  passkeyButton.hidden = !passkeys.supported();
  let autofill = null;   // the pending autofill request; aborted before the button's request and on unmount
  async function startAutofill() {
    if (!await passkeys.autofillAvailable()) return;
    input.autocomplete = 'username webauthn';   // the token the browser looks for before it lists passkeys in the box
    const mine = autofill = new AbortController(), started = Date.now();
    let outcome;
    try { outcome = await passkeys.signIn({ conditional: true, signal: mine.signal }); }
    catch (e) { if (!mine.signal.aborted) show(error, e.message); return; }   // the server refused or the connection failed: the button stays as the way to try again
    if (outcome) { await signedIn(); return; }
    // Nothing picked (a browser's timeout): offer again, with a fresh challenge. A browser that answers at once
    // would spin here, so an answer that came within two seconds ends the autofill instead.
    if (autofill === mine && !mine.signal.aborted && Date.now() - started > 2000) startAutofill();
  }
  // The button must not take the focus: the box's blur would show its error line above the button and move the
  // button down before the mouseup, and the click would land on the form (seen in the browser check).
  passkeyButton.addEventListener('mousedown', e => e.preventDefault());
  passkeyButton.addEventListener('click', async () => {
    autofill?.abort();
    passkeyButton.disabled = true;
    try { if (await passkeys.signIn()) { await signedIn(); return; } }
    catch (e) { show(error, e.message); }
    finally { passkeyButton.disabled = false; }
    startAutofill();
  });
  startAutofill();

  // Approval required: the page asks every 3 s until the admin decides (AU-05).
  function wait(name) {
    form.hidden = true; waiting.hidden = false;
    page.querySelector('.login-heading').hidden = true;
    waiting.querySelector('.waiting-name').textContent = name;
    clearInterval(poll);
    poll = setInterval(async () => {
      try {
        const answer = await api.get(`/api/auth/pending/${encodeURIComponent(name)}`);
        if (answer.state === 'approved') {
          clearInterval(poll);
          const res = await fetch('/api/auth/signin', { method: 'POST', credentials: 'same-origin', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: name }) });
          if (res.ok) await signedIn(); else rejected();
        } else if (answer.state === 'rejected') { clearInterval(poll); rejected(); }
      } catch { /* the next tick tries again */ }
    }, 3000);
  }
  function rejected() {
    waiting.querySelector('.waiting-spinner').hidden = true;
    waiting.querySelector('.rejection-notice').hidden = false;
    waiting.querySelector('.try-again').hidden = false;
  }
  waiting.querySelector('.try-again').addEventListener('click', () => {
    waiting.hidden = true; form.hidden = false; page.querySelector('.login-heading').hidden = false;
    waiting.querySelector('.waiting-spinner').hidden = false; waiting.querySelector('.rejection-notice').hidden = true; waiting.querySelector('.try-again').hidden = true;
    input.value = ''; check = null; button.disabled = true; show(null);
    page.querySelector('.login-heading').textContent = pick(texts.loginHeadings, 'Welcome to {0}!', project);   // a new pick (as before)
    button.textContent = pick(texts.loginButtons, 'Join Chat');
    input.focus();
  });

  root.replaceChildren(page);
  input.focus();
  return () => { clearInterval(poll); clearTimeout(checkTimer); autofill?.abort(); page.remove(); };
}
