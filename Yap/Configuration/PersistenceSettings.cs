namespace Yap.Configuration;

public class PersistenceSettings
{
    public const string SectionName = "ChatSettings:Persistence";

    public bool Enabled { get; set; } = false;
    public ConnectionStringSettings ConnectionStrings { get; set; } = new();

    /// <summary>What the startup load reads per conversation. Absent keys mean no limit.</summary>
    public LoadSettings Load { get; set; } = new();
}

/// <summary>
/// The bounds of the startup load (ChatSettings:Persistence:Load). Rows beyond them stay in the database and never
/// reach memory, so nobody can read them, the admin included, until the bounds change and the server restarts.
/// The per-room history windows (HistoryLimit) are a separate rule on top. 0 = no limit.
/// </summary>
public class LoadSettings
{
    /// <summary>The newest messages of each room and direct conversation, at most this many.</summary>
    public int MaxMessagesPerChannel { get; set; }

    /// <summary>And none older than this many days, counted from the start of the server.</summary>
    public int MaxDays { get; set; }

    public bool Any => MaxMessagesPerChannel > 0 || MaxDays > 0;

    /// <summary>"5000 per conversation, 90 days", for the startup log.</summary>
    public string Describe() => string.Join(", ", new[]
    {
        MaxMessagesPerChannel > 0 ? $"{MaxMessagesPerChannel} per conversation" : null,
        MaxDays > 0 ? $"{MaxDays} days" : null,
    }.Where(s => s is not null));
}

public class ConnectionStringSettings
{
    public string SQLite { get; set; } = "Data Source=Data/yap.db";
}
