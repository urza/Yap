namespace Yap.Configuration;

public class PersistenceSettings
{
    public const string SectionName = "ChatSettings:Persistence";

    public bool Enabled { get; set; } = false;
    public ConnectionStringSettings ConnectionStrings { get; set; } = new();
}

public class ConnectionStringSettings
{
    public string SQLite { get; set; } = "Data Source=Data/yap.db";
}
