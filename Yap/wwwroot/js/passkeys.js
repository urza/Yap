// Passkeys (AU-27 to AU-30): the browser half of the two WebAuthn ceremonies. The server hands out the options and
// checks the answer; this module drives navigator.credentials and converts the JSON shapes in between.

import { api } from './api.js';

// The API exists on https and on localhost only; elsewhere the browser does not define it (AU-27).
export const supported = () => typeof window.PublicKeyCredential === 'function';

// Passkey autofill (AU-28): the browser lists the saved passkeys of this site under the username box.
export async function autofillAvailable() {
  try { return supported() && !!PublicKeyCredential.isConditionalMediationAvailable && await PublicKeyCredential.isConditionalMediationAvailable(); }
  catch { return false; }
}

// Browsers from before late 2024 lack parseCreationOptionsFromJSON and toJSON; these do the same by hand.
const bytes = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
function b64url(buffer) {
  let s = '';
  for (const b of new Uint8Array(buffer)) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function parseOptions(options, kind) {
  const parse = kind === 'create' ? PublicKeyCredential.parseCreationOptionsFromJSON : PublicKeyCredential.parseRequestOptionsFromJSON;
  if (parse) return parse.call(PublicKeyCredential, options);
  const out = { ...options, challenge: bytes(options.challenge) };
  if (options.user) out.user = { ...options.user, id: bytes(options.user.id) };
  for (const key of ['excludeCredentials', 'allowCredentials']) if (options[key]) out[key] = options[key].map(c => ({ ...c, id: bytes(c.id) }));
  return out;
}
function serialize(credential) {
  if (typeof credential.toJSON === 'function') return credential.toJSON();
  const r = credential.response;
  const response = { clientDataJSON: b64url(r.clientDataJSON) };
  if (r.attestationObject) { response.attestationObject = b64url(r.attestationObject); response.transports = r.getTransports?.() ?? []; }
  if (r.signature) { response.authenticatorData = b64url(r.authenticatorData); response.signature = b64url(r.signature); response.userHandle = r.userHandle ? b64url(r.userHandle) : null; }
  return { id: credential.id, rawId: b64url(credential.rawId), type: credential.type, response, clientExtensionResults: credential.getClientExtensionResults() };
}

// Sign in with a passkey. `conditional` is the autofill: the promise waits until the person picks a passkey in the
// username box, or until `signal` aborts it. Otherwise the browser opens its passkey sheet at once.
// Resolves true when the server signed the device in, null when nothing was picked; throws with the server's reason.
export async function signIn({ conditional = false, signal } = {}) {
  const { challengeId, options } = await api.post('/api/auth/passkey/options');
  let credential;
  try { credential = await navigator.credentials.get({ publicKey: parseOptions(options, 'get'), mediation: conditional ? 'conditional' : undefined, signal }); }
  catch (e) { if (e.name === 'AbortError' || e.name === 'NotAllowedError') return null; throw e; }   // aborted, cancelled, or timed out
  if (!credential) return null;
  // A raw fetch, like the other sign-in posts: api.js treats a 401 as "you were signed out", which means nothing here.
  const res = await fetch('/api/auth/passkey', { method: 'POST', credentials: 'same-origin', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ challengeId, credential: serialize(credential) }) });
  if (res.ok) return true;
  const problem = await res.json().catch(() => null);
  throw new Error(problem?.detail ?? 'Could not sign in with the passkey');
}

// Add a passkey for the signed-in account (AU-29). Resolves the new row, or null when the person cancelled.
export async function register() {
  const { challengeId, options } = await api.post('/api/account/passkeys/options');
  let credential;
  try { credential = await navigator.credentials.create({ publicKey: parseOptions(options, 'create') }); }
  catch (e) {
    if (e.name === 'AbortError' || e.name === 'NotAllowedError') return null;
    if (e.name === 'InvalidStateError') throw new Error('This device already holds a passkey for this account.');   // excludeCredentials matched
    throw e;
  }
  if (!credential) return null;
  return api.post('/api/account/passkeys', { challengeId, credential: serialize(credential) });
}
