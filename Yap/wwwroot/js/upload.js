// Files to the TUS endpoint, with one progress figure for the whole message (IB-29 to IB-37), and one file
// with metadata for the GIF library (PK-38, ST-22). lib/tus.min.js defines `tus`.

const EXTENSIONS = ['.jpg', '.jpeg', '.png', '.gif', '.webp', '.mp4', '.webm', '.mov', '.avi', '.mkv'];   // IB-31
const MAX_FILES = 10;                                                                              // IB-32

// Splits a pick into the files that go and the lines of the refusal dialog (IB-35).
export function check(files, maxMB) {
  const accepted = [], refused = [];
  if (files.length > MAX_FILES) return { accepted: [], refused: ['Choose at most 10 files.'] };
  for (const f of files) {
    const ext = '.' + f.name.split('.').pop().toLowerCase();
    const kindOk = EXTENSIONS.includes(ext);   // the kinds of IB-31, by extension; a pasted screenshot has no name and comes as image/png
    const pasted = !f.name.includes('.') && ['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(f.type);
    if (!kindOk && !pasted) refused.push(`"${f.name}" — unsupported file type`);
    else if (f.size > maxMB * 1024 * 1024) refused.push(`"${f.name}" — too large (${(f.size / 1024 / 1024).toFixed(0)} MB, max ${maxMB} MB)`);
    else accepted.push(f);
  }
  return { accepted, refused };
}

// The server processes a file after its last chunk; its info appears when it is done.
async function info(uploadUrl, attempts = 30) {
  const base = uploadUrl.slice(0, uploadUrl.lastIndexOf('/'));
  const id = uploadUrl.split('/').pop();
  for (let attempt = 0; attempt < attempts; attempt++) {
    const res = await fetch(`${base}/info/${id}`, { credentials: 'same-origin' });
    if (res.ok) return res.json();
    await new Promise(r => setTimeout(r, 1000));
  }
  return null;
}

// One file with metadata (the GIF library: kind, target, folder). Resolves to the server's info object.
export function uploadOne(file, endpoint, metadata = {}) {
  return new Promise((resolve, reject) => {
    const u = new tus.Upload(file, {
      endpoint, chunkSize: 5 * 1024 * 1024, retryDelays: [0, 1000, 3000], withCredentials: true,
      metadata: { filename: file.name, filetype: file.type, ...metadata },
      onError: reject,
      onSuccess: async () => { const result = await info(u.url, 60); if (result) resolve(result); else reject(new Error('timeout')); },
    });
    u.start();
  });
}

// Uploads every file; onProgress(percent, done, total). Returns { promise, cancel }: the promise resolves to
// { files: [info], errors: [line], retry: [file], cancelled }; cancel() aborts the rest and resolves the promise at
// once. `retry` holds the files the server never answered for (the network, or the server, is down): they are
// not failures, the sender keeps them for the next wake-up (OF-08). `errors` are the answers that refused a file.
export function upload(files, endpoint, onProgress) {
  const total = files.reduce((s, f) => s + f.size, 0);
  const sent = new Array(files.length).fill(0);
  let done = 0, cancelled = false, settle = null;
  const uploads = [];
  const report = () => onProgress(total ? Math.round(sent.reduce((a, b) => a + b, 0) / total * 100) : 0, done, files.length);

  const all = Promise.all(files.map((file, i) => new Promise(resolve => {
    const u = new tus.Upload(file, {
      endpoint, chunkSize: 5 * 1024 * 1024, retryDelays: [0, 1000, 3000, 5000], withCredentials: true,
      metadata: { filename: file.name, filetype: file.type },
      onProgress: bytes => { sent[i] = bytes; report(); },
      // tus gives up after its own retries (about nine seconds). No response at all means nobody was reached.
      onError: error => resolve(error?.originalResponse ? { error: `"${file.name}" — ${error?.message || 'Upload failed'}` } : { retry: file }),
      onSuccess: async () => {
        done++; sent[i] = file.size; report();
        const result = await info(u.url);
        resolve(result ? { info: result } : { error: cancelled ? null : `"${file.name}" — server processing timeout` });
      },
    });
    uploads.push(u);
    u.start();
  }))).then(results => ({ files: results.filter(r => r.info).map(r => r.info), errors: results.filter(r => r.error).map(r => r.error), retry: results.filter(r => r.retry).map(r => r.retry), cancelled }));

  const promise = new Promise(resolve => { settle = resolve; all.then(resolve); });
  // tus calls neither onSuccess nor onError after abort(), so the promise settles here.
  return { promise, cancel: () => { cancelled = true; uploads.forEach(u => u.abort(true).catch(() => {})); settle({ files: [], errors: [], retry: [], cancelled: true }); } };   // abort(true) deletes the upload on the server (IB-34)
}
