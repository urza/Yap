namespace Yap.LoadTest.Orchestrator;

/// <summary>--name value pairs; a flag without a value is "true".</summary>
public sealed class Args(string[] argv)
{
    private readonly Dictionary<string, string> _values = Parse(argv);

    private static Dictionary<string, string> Parse(string[] argv)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var key = argv[i][2..];
            var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--");
            values[key] = hasValue ? argv[++i] : "true";
        }
        return values;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public string Require(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");
    public int Int(string key, int fallback) => Get(key) is { } v ? int.Parse(v) : fallback;
    public double Double(string key, double fallback) => Get(key) is { } v ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    public bool Has(string key) => _values.ContainsKey(key);
}
