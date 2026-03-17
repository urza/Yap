using Yap.Services;

namespace Yap.Endpoints;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics", (ChatService chatService, CircuitTracker circuitTracker) =>
        {
            var diagnostics = chatService.GetDiagnostics();
            var (active, disconnected, totalCreated) = circuitTracker.GetStats();

            diagnostics.ActiveCircuits = active;
            diagnostics.DisconnectedCircuits = disconnected;
            diagnostics.TotalCircuitsCreated = totalCreated;

            return Results.Ok(diagnostics);
        });

        app.MapGet("/api/diagnostics/circuits", (CircuitTracker circuitTracker) =>
        {
            var circuits = circuitTracker.GetAllCircuits();
            return Results.Ok(new
            {
                circuits = circuits.Select(c => new
                {
                    c.CircuitId,
                    c.CreatedAt,
                    c.IsConnected,
                    c.DisconnectedAt,
                    AgeMinutes = (DateTime.UtcNow - c.CreatedAt).TotalMinutes
                }),
                summary = new
                {
                    active = circuits.Count(c => c.IsConnected),
                    disconnected = circuits.Count(c => !c.IsConnected)
                }
            });
        });

        app.MapGet("/api/test-exception", () =>
        {
            throw new InvalidOperationException("This is a test exception to verify error handling!");
        });
    }
}
