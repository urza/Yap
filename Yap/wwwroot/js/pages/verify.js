// The verification page (AU-12, AU-14, AU-28): the ways a known name has on a new device. A passkey, a code (the
// login link's, or an older secret code), or both. The server's check says which; the page shows exactly those.

import { api } from '../api.js';
import { clone } from '../render/dom.js';
import * as session from '../session.js';
import * as router from '../router.js';
import * as passkeys from '../passkeys.js';

export function mount(root, { username }) {
  const page = clone('t-verify');
  page.querySelector('.verify-name').textContent = username;
  document.title = `Verify — ${username}`;
  const hint = page.querySelector('.verify-hint');
  const form = page.querySelector('.verify-form');
  const input = page.querySelector('.verify-input');
  const button = page.querySelector('.verify-button');
  const error = page.querySelector('.verify-form .validation-error');
  const passkeyBlock = page.querySelector('.verify-passkey');
  const passkeyButton = page.querySelector('.verify-passkey-button');
  const passkeyError = page.querySelector('.verify-passkey-error');
  const or = page.querySelector('.verify-or');

  // Until the server answers, only the heading shows. An answer that is not "verify" (the device moved networks,
  // the account changed) falls back to the code box, which refuses a wrong code on its own.
  api.get(`/api/auth/check?username=${encodeURIComponent(username)}`)
    .then(check => draw(check.state === 'verify' ? check : { code: true }))
    .catch(() => draw({ code: true }));

  function draw({ passkey = false, code = false }) {
    const usable = passkey && passkeys.supported();
    passkeyBlock.hidden = !usable;
    form.hidden = !code;
    or.hidden = !(usable && code);
    hint.textContent = code ? 'Type the code from your login link (the part after /invite/). An older secret code works too.'
      : usable ? 'Confirm with the passkey you saved for this account. A phone that holds it can sign this device in too.'
      : 'This account signs in with a passkey, but this browser cannot use passkeys on this address.';
    if (code) input.focus();
  }

  input.addEventListener('input', () => { button.disabled = !input.value.trim(); error.hidden = true; input.classList.remove('invalid'); });
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !button.disabled) { e.preventDefault(); signIn(); } });
  button.addEventListener('click', signIn);
  page.querySelector('.back-link').addEventListener('click', () => router.navigate('/login'));

  async function signIn() {
    const code = input.value.trim();
    if (!code) return;
    button.disabled = true;
    try {
      // The code travels in the body, never in the URL.
      const res = await fetch('/api/auth/signin', { method: 'POST', credentials: 'same-origin', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username, code }) });
      if (res.ok) { await session.signedIn(); return; }
      const problem = await res.json().catch(() => null);
      error.textContent = problem?.detail ?? 'Wrong code';
      error.hidden = false; input.classList.add('invalid');
    } catch { error.textContent = 'Needs a connection'; error.hidden = false; }
    finally { button.disabled = !input.value.trim(); }
  }

  passkeyButton.addEventListener('click', async () => {
    passkeyButton.disabled = true; passkeyError.hidden = true;
    try { if (await passkeys.signIn()) { await session.signedIn(); return; } }
    catch (e) { passkeyError.textContent = e.message; passkeyError.hidden = false; }
    finally { passkeyButton.disabled = false; }
  });

  root.replaceChildren(page);
  return () => page.remove();
}
