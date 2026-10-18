namespace Yap.Models.Chat;

/// <summary>
/// The voice message of a <see cref="ChatMessage"/>, stored as one JSON column. A message with a voice
/// attachment carries nothing else: no text, no other media (IB-70).
///
/// <see cref="Url"/> is the transcoded file under /uploads, always an .m4a (VoiceService). <see cref="Peaks"/>
/// is the waveform the row draws: one base-36 digit per bar, '0' silent to 'z' the loudest bar, about 48
/// characters. The recorder on the client measures it while the person speaks, so the pending row and the
/// stored row draw the same picture; the server only checks its shape.
///
/// A record: value equality is what the persistence change tracking compares (see ChatDbContext.StoreAsJson).
/// </summary>
public sealed record VoiceAttachment(string Url, int DurationMs, string Peaks);
