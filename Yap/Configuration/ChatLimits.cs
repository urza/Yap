namespace Yap.Configuration;

/// <summary>
/// Chat limits read from configuration and clamped to sane ranges. Most are enforced on the server;
/// MaxBatchBytes and AwayAfterMs are only advertised to the client in the bootstrap header.
/// </summary>
public sealed class ChatLimits(IConfiguration config)
{
    private int Value(string name, int fallback, int min, int max) => Math.Clamp(config.GetValue("ChatSettings:" + name, fallback), min, max);
    public int MaxTextLength => Value(nameof(MaxTextLength), 4000, 1, 100000);
    public int MaxOperationsPerBatch => Value(nameof(MaxOperationsPerBatch), 16, 1, 100);
    public int MaxPostBytes => Value(nameof(MaxPostBytes), 64 * 1024, 32 * 1024, 1024 * 1024);
    // Reserve room for JSON escaping/envelopes; the sender measures actual UTF-8 payload bytes.
    public int MaxBatchBytes => MaxPostBytes - 16 * 1024;
    public int MaxFilesPerMessage => Value(nameof(MaxFilesPerMessage), 20, 1, 100);
    public string[] AllowedExtensions => config.GetSection("ChatSettings:AllowedExtensions").Get<string[]>()
        ?? [".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp4", ".webm", ".mov", ".mkv"];
    public long MaxUploadBytes => (long)config.GetValue("ChatSettings:MaxUploadSizeMB", 100) * 1024 * 1024;
    /// <summary>Messages per conversation in a bootstrap or window; the client caches this many.</summary>
    public int RecentMessageLimit => Value(nameof(RecentMessageLimit), 100, 1, 500);
    public int HistoryPageSize => Math.Min(Value(nameof(HistoryPageSize), 50, 1, 5000), HistoryMaxMessages);
    public int HistoryMaxMessages => Value(nameof(HistoryMaxMessages), 500, 1, 5000);
    public int ReadBatch => Value(nameof(ReadBatch), 100, 1, 1000);
    public int TypingTimeoutMs => Value(nameof(TypingTimeoutMs), 3000, 1000, 30000);
    public int AwayAfterMs => Value(nameof(AwayAfterMs), 30000, 1000, 300000);
}
