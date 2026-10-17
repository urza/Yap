using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>Times every hub call. A call over 100 ms counts as a slow event on the connection's row of the
/// diagnostics table (AD-26). A message send is HTTP and is timed by <see cref="ApiLatencyFilter"/>.</summary>
public sealed class HubLatencyFilter(PresenceService presence) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next(context);
        }
        finally
        {
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= LatencyStats.SlowMs) presence.ReportSlowEvent(context.Context.ConnectionId, ms);
        }
    }
}
