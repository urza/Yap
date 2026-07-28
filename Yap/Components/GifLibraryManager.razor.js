// GIF library uploads via tus, with library metadata (kind/target/folder) that routes them on
// the server. Deliberately separate from chat.js uploadFilesWithTus — that one's validation and
// result shape are chat-attachment-specific. tus-js-client is loaded globally in App.razor.
//
// Blazor hands us the <input type=file> ElementReference so we read .files directly; per-file
// results come from the one-shot /info/{fileId} endpoint, aggregate progress is pushed to .NET.
export async function uploadGifLibraryFiles(inputEl, endpoint, maxSizeMB, allowedExtensions, extraMeta, dotNetRef) {
    if (!inputEl || !inputEl.files || inputEl.files.length === 0) {
        return { success: false, errors: [], results: [] };
    }

    const files = Array.from(inputEl.files);
    // Blazor decides the allowlist (it shrinks to .gif/.webp when the server has no ffmpeg).
    const allowed = (allowedExtensions && allowedExtensions.length) ? allowedExtensions : ['.gif', '.webp'];
    const maxSize = (maxSizeMB || 50) * 1024 * 1024;

    const valid = [];
    const errors = [];
    for (const f of files) {
        const ext = '.' + f.name.split('.').pop().toLowerCase();
        if (!allowed.includes(ext)) {
            errors.push(`"${f.name}" — unsupported file type`);
        } else if (f.size > maxSize) {
            errors.push(`"${f.name}" — too large (${(f.size / 1024 / 1024).toFixed(0)} MB, max ${maxSizeMB} MB)`);
        } else {
            valid.push(f);
        }
    }
    if (valid.length === 0) {
        inputEl.value = '';
        return { success: false, errors, results: [] };
    }

    const totalBytes = valid.reduce((sum, f) => sum + f.size, 0);
    const progress = new Array(valid.length).fill(0);
    let completed = 0;
    const report = () => {
        const uploaded = progress.reduce((sum, b) => sum + b, 0);
        const percent = totalBytes > 0 ? Math.round((uploaded / totalBytes) * 100) : 0;
        dotNetRef?.invokeMethodAsync('OnUploadProgress', percent, completed, valid.length)
            .catch(() => { }); // circuit may be dead
    };

    const uploads = valid.map((file, index) => new Promise((resolve) => {
        const upload = new tus.Upload(file, {
            endpoint,
            chunkSize: 5 * 1024 * 1024,
            retryDelays: [0, 1000, 3000, 5000],
            withCredentials: true,
            metadata: { filename: file.name, filetype: file.type, ...extraMeta },
            onProgress: (bytesUploaded) => {
                progress[index] = bytesUploaded;
                report();
            },
            onSuccess: async () => {
                completed++;
                progress[index] = file.size;
                report();

                // The server finishes processing before answering the final PATCH, so the info
                // entry normally exists on the first try — the retries are pure paranoia.
                const fileId = upload.url.split('/').pop();
                const infoUrl = upload.url.substring(0, upload.url.lastIndexOf('/')) + '/info/' + fileId;
                for (let attempt = 0; attempt < 30; attempt++) {
                    try {
                        const resp = await fetch(infoUrl, { credentials: 'include' });
                        if (resp.ok) {
                            resolve(await resp.json());
                            return;
                        }
                    } catch { /* transient network error — retry */ }
                    await new Promise(r => setTimeout(r, 1000));
                }
                errors.push(`"${file.name}" — server processing timeout`);
                resolve(null);
            },
            onError: (error) => {
                errors.push(`"${file.name}" — ${error.message || 'upload failed'}`);
                resolve(null);
            }
        });
        upload.start();
    }));

    const results = (await Promise.all(uploads)).filter(r => r !== null);

    // Server-side rejections (quota, unprocessable file) arrive as type:"error" results.
    for (const r of results) {
        if (r.type === 'error' && r.error) errors.push(r.error);
    }

    inputEl.value = '';
    return {
        success: results.some(r => r.type !== 'error'),
        errors,
        results
    };
}
