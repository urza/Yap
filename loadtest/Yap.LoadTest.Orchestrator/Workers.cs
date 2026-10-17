using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>
/// Starts worker processes, drives them over stdin and reads their report lines. Worker k owns the accounts
/// k * perWorker .. (k + 1) * perWorker - 1 for the whole run; how many of them run is a "users N" line.
/// </summary>
public sealed class Workers(string workerDll, Aggregate aggregate) : IAsyncDisposable
{
    private readonly Dictionary<int, Process> _processes = [];

    public int Count => _processes.Count;
    public bool Has(int id) => _processes.ContainsKey(id);

    public void Start(int id, Uri server, string usersFile, int from, int count, double rate, int rooms, double visible, string prefix, string? links)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[] { workerDll, "--server", server.ToString(), "--users", usersFile, "--from", from.ToString(), "--count", count.ToString(), "--worker", id.ToString(),
                     "--rate", rate.ToString(CultureInfo.InvariantCulture), "--rooms", rooms.ToString(), "--visible", visible.ToString(CultureInfo.InvariantCulture), "--prefix", prefix })
            psi.ArgumentList.Add(arg);
        if (links is not null) { psi.ArgumentList.Add("--links"); psi.ArgumentList.Add(links); }
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the worker");
        _processes[id] = process;
        aggregate.Note($"worker {id}: started, accounts {from + 1}..{from + count}");
        _ = ReadAsync(process, id);
    }

    /// <summary>One command line: "users 120", "kinds text,links", "pace busy". A worker that is gone ignores it.</summary>
    public async Task SendAsync(int id, string line)
    {
        if (!_processes.TryGetValue(id, out var p) || p.HasExited) return;
        try { await p.StandardInput.WriteLineAsync(line); await p.StandardInput.FlushAsync(); }
        catch (IOException) { /* exited between the check and the write */ }
    }

    private async Task ReadAsync(Process process, int id)
    {
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (line.Length > 0) aggregate.Note($"worker {id}: {line}");
        });
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith('{')) { aggregate.Note($"worker {id}: {line}"); continue; }
            try { if (JsonSerializer.Deserialize<WorkerReport>(line, Json.Options) is { } report) aggregate.Add(report); }
            catch (JsonException) { aggregate.Note($"worker {id}: bad line"); }
        }
        await stderr;
        aggregate.Note($"worker {id}: exited ({process.ExitCode})");
    }

    /// <summary>"stop" on stdin lets every virtual user disconnect; a worker that is still there after 15 s is killed.</summary>
    public async Task StopAsync()
    {
        foreach (var p in _processes.Values.Where(p => !p.HasExited))
        {
            try { await p.StandardInput.WriteLineAsync("stop"); await p.StandardInput.FlushAsync(); } catch { /* already gone */ }
        }
        var deadline = Task.Delay(TimeSpan.FromSeconds(15));
        await Task.WhenAny(Task.WhenAll(_processes.Values.Select(p => p.WaitForExitAsync())), deadline);
        foreach (var p in _processes.Values.Where(p => !p.HasExited)) { try { p.Kill(entireProcessTree: true); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var p in _processes.Values) p.Dispose();
    }
}
