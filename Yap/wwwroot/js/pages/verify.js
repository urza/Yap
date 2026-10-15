// The verification page (AU-12, AU-14): the secret code, or the code of the login link, for a known name.

import { clone } from '../render/dom.js';
import * as session from '../session.js';
import * as router from '../router.js';

export function mount(root, { username }) {
  const page = clone('t-verify');
  page.querySelector('.verify-name').textContent = username;
  document.title = `Verify — ${username}`;
  const input = page.querySelector('.verify-input');
  const button = page.querySelector('.verify-button');
  const error = page.querySelector('.validation-error');

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

  root.replaceChildren(page);
  input.focus();
  return () => page.remove();
}
