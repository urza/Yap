using System.Diagnostics;
using Yap.Services.Accounts;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>
/// Times a message send for the diagnostics table (AD-26). A send is HTTP, not a hub call, so
/// <see cref="HubLatencyFilter"/> never sees it. The request carries only the cookie, no connection id, so the
/// sample lands on the person and not on a connection row.
/// </summary>
public sealed class ApiLatencyFilter(PresenceService presence) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next(context);
        }
        finally
        {
            var me = context.HttpContext.User;
            if (me.IsSignedIn()) presence.ReportRequest(me.Id(), me.Username(), Wait.Post, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
