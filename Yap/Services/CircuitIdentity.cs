namespace Yap.Services;

/// <summary>
/// Scoped bridge between circuit infrastructure and components: Blazor components can't see
/// their own circuit id, so ChatCircuitHandler records it here (same DI scope) and telemetry
/// reporters (the latency probe hosted in ChatLayout) read it back.
/// </summary>
public sealed class CircuitIdentity
{
    public string? CircuitId { get; set; }
}
