using System.Diagnostics;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Yap.Helpers;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Handles circuit lifecycle events and auto-away detection.
/// Uses CreateInboundActivityHandler to track ALL user activity (UI events, JS interop).
/// </summary>
public sealed class ChatCircuitHandler : CircuitHandler, IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DisconnectGracePeriod = TimeSpan.FromSeconds(30);

    // Inbound activities slower than this get reported to CircuitTracker and logged.
    private const double SlowInboundEventMs = 100;

    private readonly ChatService _chatService;
    private readonly UserStateService _userState;
    private readonly UserService _userService;
    private readonly CircuitTracker _circuitTracker;
    private readonly CircuitIdentity _identity;
    private readonly UserActionLogService _actionLog;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ChatCircuitHandler> _logger;

    // Idle timeout uses CancellationTokenSource + Task.Delay pattern:
    // - On any user activity, we cancel the current delay and start a new one
    // - If no activity for IdleTimeout, the delay completes and sets user to Away
    // - This avoids threading issues with System.Timers.Timer (which fires on ThreadPool)
    private CancellationTokenSource? _idleCts;
    private UserStatus? _statusBeforeDisconnect;
    private string? _circuitId;
    private string? _clientIp;

    public ChatCircuitHandler(
        ChatService chatService,
        UserStateService userState,
        UserService userService,
        CircuitTracker circuitTracker,
        CircuitIdentity circuitIdentity,
        UserActionLogService actionLog,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ChatCircuitHandler> logger)
    {
        _chatService = chatService;
        _userState = userState;
        _userService = userService;
        _circuitTracker = circuitTracker;
        _identity = circuitIdentity;
        _actionLog = actionLog;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitId = circuit.Id;
        _identity.CircuitId = circuit.Id; // components (the latency probe in ChatLayout) report telemetry against this id
        _circuitTracker.OnCircuitOpened(circuit.Id);

        // Capture client IP from the initial HTTP request (available during circuit setup)
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            _clientIp = IpHelper.GetClientIp(httpContext);
        }

        // Label the circuit for the admin diagnostics table. A non-WebSocket connection means
        // SignalR fell back to SSE/long-polling — the top suspect when one client feels laggy.
        _circuitTracker.SetUser(circuit.Id, _userState.Username, _clientIp, httpContext?.WebSockets.IsWebSocketRequest);

        _logger.LogDebug("Circuit {CircuitId} opened, starting idle timer ({Timeout})", circuit.Id, IdleTimeout);
        StartIdleTimer();
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitTracker.OnConnectionUp(circuit.Id);

        // Re-label on every connection-up: the username can hydrate after circuit open, and a
        // reconnect may arrive on a different transport than the original connection.
        _circuitTracker.SetUser(circuit.Id, _userState.Username, _clientIp,
            _httpContextAccessor.HttpContext?.WebSockets.IsWebSocketRequest);

        // Reconnected — re-assert this session as visible (a reconnecting circuit is an active tab).
        // The client's visibilitychange listener corrects this if the tab is later backgrounded.
        if (!string.IsNullOrEmpty(_userState.SessionId))
            _chatService.SetPageVisibility(_userState.SessionId, true);

        // User reconnected - restore their previous status if no other sessions changed it
        if (!string.IsNullOrEmpty(_userState.SessionId) && _statusBeforeDisconnect.HasValue)
        {
            var currentStatus = _chatService.GetUserStatus(_userState.Username!);
            // Only restore if user status hasn't been changed by another session
            if (currentStatus == null || currentStatus == UserStatus.Invisible || currentStatus == _statusBeforeDisconnect.Value)
            {
                _logger.LogDebug("Connection restored for {Username}, restoring status to {Status}",
                    _userState.Username, _statusBeforeDisconnect.Value);

                await _chatService.SetUserStatusAsync(_userState.SessionId, _statusBeforeDisconnect.Value);
                _userState.Status = _statusBeforeDisconnect.Value;
            }
            else
            {
                // Another session changed the status — sync local state
                _userState.Status = currentStatus.Value;
                _logger.LogDebug("Connection restored for {Username}, keeping current status {Status} (changed by another session)",
                    _userState.Username, currentStatus.Value);
            }
            _statusBeforeDisconnect = null;

            _actionLog.Log(_userState.UserId?.ToString(), UserActionLog.KnownActions.CIRCUIT_RECONNECT,
                info: _userState.Username, ip: _clientIp);
        }

        StartIdleTimer();
        await base.OnConnectionUpAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitTracker.OnConnectionDown(circuit.Id);

        // Don't stop the idle timer — start a shorter disconnect grace period instead.
        // If the user doesn't reconnect within 30 seconds, they'll be set to Away.
        // This avoids the bug where users stay green for hours after disconnecting.
        StartIdleTimer(DisconnectGracePeriod);

        // Save status for potential restore on reconnect.
        // Don't change user status here — circuit close handles cleanup via RemoveUserAsync.
        if (!string.IsNullOrEmpty(_userState.SessionId) && !string.IsNullOrEmpty(_userState.Username))
        {
            // A disconnected circuit can't be "foreground" — clear this session's page-visibility so a
            // dropped/closed device stops suppressing push to the user's other devices (e.g. phone).
            // Otherwise PageVisible stays true for the whole disconnected-circuit retention window (~4h).
            _chatService.SetPageVisibility(_userState.SessionId, false);

            var currentStatus = _chatService.GetUserStatus(_userState.Username);
            if (currentStatus.HasValue && currentStatus != UserStatus.Invisible)
            {
                _statusBeforeDisconnect = currentStatus;
                _logger.LogDebug("Connection lost for {Username}, saving status {Status} for restore",
                    _userState.Username, currentStatus);
            }

            if (_userState.UserId.HasValue)
            {
                await _userService.UpdateLastSeenAsync(_userState.UserId.Value);
            }

            _actionLog.Log(_userState.UserId?.ToString(), UserActionLog.KnownActions.CIRCUIT_DISCONNECT,
                info: _userState.Username, ip: _clientIp);
        }

        await base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitTracker.OnCircuitClosed(circuit.Id);
        StopIdleTimer();

        if (!string.IsNullOrEmpty(_userState.SessionId) && !string.IsNullOrEmpty(_userState.Username))
        {
            // Remove this session from ChatService
            await _chatService.RemoveUserAsync(_userState.SessionId);

            // If no other sessions remain, user status was already cleaned up by RemoveUserAsync.
            // If other sessions exist, status is preserved.
            _logger.LogDebug("Circuit closed for {Username}, session removed", _userState.Username);
        }

        await base.OnCircuitClosedAsync(circuit, cancellationToken);
    }

    /// <summary>
    /// Intercepts ALL inbound circuit activity (UI events, JS interop calls).
    /// Resets the idle timer on any activity.
    /// </summary>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            var start = Stopwatch.GetTimestamp();

            // Track activity for this session (used by AreAllSessionsIdle)
            if (!string.IsNullOrEmpty(_userState.SessionId))
            {
                _chatService.TouchSessionActivity(_userState.SessionId);
            }

            // Reset idle timer on any activity (cancels current delay, starts fresh)
            StartIdleTimer();

            // Restore from auto-away if user becomes active again.
            // TryRestoreFromAutoAway is per-user (in ChatService), so ANY circuit can restore —
            // works across new tabs, page refreshes, and multi-device.
            if (!string.IsNullOrEmpty(_userState.SessionId))
            {
                var restoredStatus = _chatService.TryRestoreFromAutoAway(_userState.SessionId);
                if (restoredStatus.HasValue)
                {
                    _userState.Status = restoredStatus.Value;
                }
            }

            await next(context);

            // Server-side processing time for this activity (dispatch + handler + renders; network
            // excluded). Compared with the client RTT probe this separates "slow link" from "busy
            // server". Information level on purpose — visible at prod's default log level.
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsedMs >= SlowInboundEventMs && _circuitId is not null)
            {
                _circuitTracker.ReportSlowEvent(_circuitId, elapsedMs);
                _logger.LogInformation("Slow inbound event: {ElapsedMs:F0}ms circuit={CircuitId} user={Username}",
                    elapsedMs, _circuitId, _userState.Username);
            }
        };
    }

    /// <summary>
    /// Cancels any pending idle timeout and starts a fresh one.
    /// Called on circuit open, connection up, and any user activity.
    /// </summary>
    private void StartIdleTimer(TimeSpan? timeout = null)
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = new CancellationTokenSource();
        _ = IdleTimeoutAsync(_idleCts.Token, timeout ?? IdleTimeout);
    }

    /// <summary>
    /// Cancels any pending idle timeout without starting a new one.
    /// Called on circuit close.
    /// </summary>
    private void StopIdleTimer()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = null;
    }

    /// <summary>
    /// Waits for the specified timeout, then sets user to Away if not cancelled.
    /// </summary>
    private async Task IdleTimeoutAsync(CancellationToken token, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, token);
            await SetAutoAwayAsync(timeout);
        }
        catch (OperationCanceledException)
        {
            // Timer was reset or stopped, ignore
        }
    }

    private async Task SetAutoAwayAsync(TimeSpan idleThreshold)
    {
        if (string.IsNullOrEmpty(_userState.SessionId) || string.IsNullOrEmpty(_userState.Username))
            return;

        // Don't override if already Away or Invisible
        var currentStatus = _chatService.GetUserStatus(_userState.Username);
        if (currentStatus is UserStatus.Away or UserStatus.Invisible)
            return;

        // Only set auto-away if ALL sessions for this user are idle
        // Uses the same threshold as the timer that triggered this (5 min for idle, 30s for disconnect)
        if (!_chatService.AreAllSessionsIdle(_userState.Username, idleThreshold))
        {
            _logger.LogDebug("Auto-away skipped for {Username}: another session is still active", _userState.Username);
            return;
        }

        _logger.LogDebug("Auto-away: {Username} idle on all sessions, setting to Away", _userState.Username);

        // Pass current status as autoAwayPreviousStatus — ChatService records it for restoration
        await _chatService.SetUserStatusAsync(_userState.SessionId, UserStatus.Away,
            autoAwayPreviousStatus: currentStatus ?? UserStatus.Online);
        _userState.Status = UserStatus.Away;
    }

    public void Dispose()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
    }
}
