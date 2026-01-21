using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Yap.Middleware;

/// <summary>
/// Logs HTTP requests to daily CSV files with buffered writes.
/// Filters out static files, framework paths, and uploaded images.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestLogQueue _logQueue;

    private static readonly string[] SkipPrefixes = ["/_framework", "/_blazor", "/uploads"];
    private static readonly string[] SkipExtensions = [".js", ".css", ".webp", ".png", ".jpg", ".jpeg", ".gif", ".webmanifest"];

    public RequestLoggingMiddleware(RequestDelegate next, RequestLogQueue logQueue)
    {
        _next = next;
        _logQueue = logQueue;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (ShouldSkip(path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            var entry = new RequestLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Method = context.Request.Method,
                Path = path + context.Request.QueryString,
                StatusCode = context.Response.StatusCode,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ClientIp = GetClientIp(context),
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                Referer = context.Request.Headers.Referer.ToString(),
                Protocol = context.Request.Protocol,
                ConnectionId = context.Connection.Id
            };

            _logQueue.Enqueue(entry);
        }
    }

    private static bool ShouldSkip(string path)
    {
        foreach (var prefix in SkipPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var ext in SkipExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GetClientIp(HttpContext context)
    {
        // Check X-Forwarded-For header first (for proxies/load balancers)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // X-Forwarded-For can contain multiple IPs; take the first (original client)
            var firstIp = forwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstIp))
                return firstIp;
        }

        // Fall back to direct connection IP
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

/// <summary>
/// Log entry record for a single HTTP request.
/// </summary>
public record RequestLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public int StatusCode { get; init; }
    public long DurationMs { get; init; }
    public string ClientIp { get; init; } = "";
    public string UserAgent { get; init; } = "";
    public string Referer { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string ConnectionId { get; init; } = "";
}

/// <summary>
/// Thread-safe queue for buffering log entries.
/// Middleware writes here, background writer reads from here.
/// </summary>
public class RequestLogQueue
{
    private readonly ConcurrentQueue<RequestLogEntry> _queue = new();

    public void Enqueue(RequestLogEntry entry) => _queue.Enqueue(entry);

    public bool TryDequeue(out RequestLogEntry? entry) => _queue.TryDequeue(out entry);

    public bool IsEmpty => _queue.IsEmpty;
}

/// <summary>
/// Background service that periodically flushes the log queue to CSV files.
/// One file per day: Data/RequestLogs/yyyy-MM-dd.csv
/// </summary>
public class RequestLogWriter : IHostedService, IDisposable
{
    private readonly RequestLogQueue _queue;
    private readonly string _logDirectory;
    private Timer? _flushTimer;
    private readonly object _writeLock = new();

    private const string CsvHeader = "Timestamp,Method,Path,StatusCode,DurationMs,ClientIP,UserAgent,Referer,Protocol,ConnectionId";

    public RequestLogWriter(RequestLogQueue queue, IWebHostEnvironment env)
    {
        _queue = queue;
        _logDirectory = Path.Combine(env.ContentRootPath, "Data", "RequestLogs");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_logDirectory);
        _flushTimer = new Timer(FlushCallback, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _flushTimer?.Change(Timeout.Infinite, 0);
        Flush(); // Final flush on shutdown
        return Task.CompletedTask;
    }

    private void FlushCallback(object? state) => Flush();

    private void Flush()
    {
        if (_queue.IsEmpty) return;

        // Drain the queue
        var entries = new List<RequestLogEntry>();
        while (_queue.TryDequeue(out var entry))
        {
            if (entry != null) entries.Add(entry);
        }

        if (entries.Count == 0) return;

        // Group by date (in case entries span midnight)
        var byDate = entries.GroupBy(e => e.Timestamp.Date);

        lock (_writeLock)
        {
            foreach (var group in byDate)
            {
                var fileName = $"{group.Key:yyyy-MM-dd}.csv";
                var filePath = Path.Combine(_logDirectory, fileName);

                var isNewFile = !File.Exists(filePath);

                var sb = new StringBuilder();

                if (isNewFile)
                {
                    sb.AppendLine(CsvHeader);
                }

                foreach (var entry in group)
                {
                    sb.AppendLine(FormatCsvLine(entry));
                }

                File.AppendAllText(filePath, sb.ToString());
            }
        }

        Console.WriteLine($"[RequestLog] Flushed {entries.Count} entries to disk");
    }

    private static string FormatCsvLine(RequestLogEntry entry)
    {
        return string.Join(",",
            entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            EscapeCsv(entry.Method),
            EscapeCsv(entry.Path),
            entry.StatusCode,
            entry.DurationMs,
            EscapeCsv(entry.ClientIp),
            EscapeCsv(entry.UserAgent),
            EscapeCsv(entry.Referer),
            EscapeCsv(entry.Protocol),
            EscapeCsv(entry.ConnectionId)
        );
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
    }
}
