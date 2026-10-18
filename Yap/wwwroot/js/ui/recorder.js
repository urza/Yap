// The microphone recorder of the input box (IB-67 to IB-69). record() asks for the microphone and starts at once.
// The job it returns ends in one of four ways: Send (stop), the trash (cancel), the length cap, or the system taking
// the microphone away (a call, a switch of app on a phone). `done` resolves with the recording, or with null when
// nothing is to be sent.
//
// The browser decides the container: Chrome and Firefox record Opus in WebM or Ogg, Safari AAC in MP4. The server
// transcodes each of them to one .m4a (VoiceService), so the blob goes up as it is. The waveform is measured here
// while the person speaks, one level per tenth of a second, and reduced to BARS digits at the end
// (VoiceAttachment.Peaks): the pending row and the accepted row then draw the same picture.

const MIME_TYPES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
const BARS = 48;
const SAMPLE_MS = 100;

export const supported = () => !!(navigator.mediaDevices?.getUserMedia && window.MediaRecorder);

// record({ maxMs, onLevel(levels), onTick(elapsedMs) }) -> { ready, done, stop, cancel }. `ready` resolves when the
// recording runs and rejects when the microphone is refused; `done` resolves at the end, with { blob, durationMs,
// peaks } or null. Call it inside the tap: Safari starts the audio context of the level meter only in a user gesture.
export function record({ maxMs, onLevel, onTick }) {
  const ctx = new AudioContext();   // before the first await, see above
  ctx.resume().catch(() => {});
  const levels = [], chunks = [];
  let recorder = null, stream = null, cancelled = false, startedAt = 0, meter = 0, cap = 0;
  let finish;
  const done = new Promise(resolve => { finish = resolve; });

  const release = () => {
    clearInterval(meter); clearTimeout(cap);
    stream?.getTracks().forEach(t => t.stop());   // the browser's red microphone mark goes with the tracks
    ctx.close().catch(() => {});
  };

  const start = async () => {
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: true }); }
    catch (e) { release(); finish(null); throw e; }
    if (cancelled) { release(); finish(null); return; }   // the trash came before the permission answer: the microphone goes back at once
    try { begin(); }
    catch (e) { release(); finish(null); throw e; }   // no recorder for this stream on this browser: the microphone goes back, the composer hears nothing
  };

  const begin = () => {
    const mimeType = MIME_TYPES.find(t => MediaRecorder.isTypeSupported(t));
    recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
    recorder.addEventListener('dataavailable', e => { if (e.data.size) chunks.push(e.data); });
    recorder.addEventListener('stop', () => {
      release();
      if (cancelled) { finish(null); return; }
      finish({ blob: new Blob(chunks, { type: recorder.mimeType }), durationMs: Date.now() - startedAt, peaks: peaksOf(levels) });
    });
    stream.getAudioTracks()[0]?.addEventListener('ended', () => recorder.state === 'recording' && recorder.stop());   // the system took the microphone: what is recorded goes
    // The level meter: the loudest sample of each window, 0 to 1. A failure here costs the bars, not the recording.
    try {
      const analyser = ctx.createAnalyser();
      analyser.fftSize = 512;
      ctx.createMediaStreamSource(stream).connect(analyser);   // not connected to the output: nothing plays back
      const buf = new Uint8Array(analyser.fftSize);
      meter = setInterval(() => {
        analyser.getByteTimeDomainData(buf);
        let peak = 0;
        for (const v of buf) peak = Math.max(peak, Math.abs(v - 128));
        levels.push(peak / 128);
        onLevel?.(levels);
        onTick?.(Date.now() - startedAt);
      }, SAMPLE_MS);
    } catch { meter = setInterval(() => onTick?.(Date.now() - startedAt), SAMPLE_MS); }
    startedAt = Date.now();
    recorder.start();
    cap = setTimeout(stop, maxMs);   // the cap stops and sends (IB-69)
  };

  const stop = () => { if (recorder?.state === 'recording') recorder.stop(); };
  // Before the stream arrived the job ends now, so the composer is free again even if the permission prompt never
  // answers (Firefox keeps getUserMedia pending when its prompt is dismissed); start() still releases a late stream.
  const cancel = () => { cancelled = true; stop(); if (!stream) finish(null); };
  return { ready: start(), done, stop, cancel };
}

// The stored waveform: BARS bars, each the loudest level of its slice of the recording, scaled so the loudest bar
// is 'z'. One base-36 digit per bar (VoiceAttachment.Peaks). Empty when the meter never ran.
export function peaksOf(levels, bars = BARS) {
  if (!levels.length) return '';
  let max = 0;
  for (const l of levels) max = Math.max(max, l);
  if (!max) return '0'.repeat(bars);
  let out = '';
  for (let i = 0; i < bars; i++) {
    const from = Math.floor(i * levels.length / bars), to = Math.max(from + 1, Math.floor((i + 1) * levels.length / bars));
    let peak = 0;
    for (let j = from; j < to; j++) peak = Math.max(peak, levels[j]);
    out += Math.round(peak / max * 35).toString(36);
  }
  return out;
}
