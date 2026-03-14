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

    private readonly ChatService _chatService;
    private readonly UserStateService _userState;
    private readonly UserService _userService;
    private readonly CircuitTracker _circuitTracker;
    private readonly UserActionLogService _actionLog;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ChatCircuitHandler> _logger;

    // Idle timeout uses CancellationTokenSource + Task.Delay pattern:
    // - On any user activity, we cancel the current delay and start a new one
    // - If no activity for IdleTimeout, the delay completes and sets user to Away
    // - This avoids threading issues with System.Timers.Timer (which fires on ThreadPool)
    private CancellationTokenSource? _idleCts;
    private UserStatus? _statusBeforeDisconnect;
    private bool _isAutoAway;
    private UserStatus? _statusBeforeAway;
    private string? _circuitId;
    private string? _clientIp;

    public ChatCircuitHandler(
        ChatService chatService,
        UserStateService userState,
        UserService userService,
        CircuitTracker circuitTracker,
        UserActionLogService actionLog,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ChatCircuitHandler> logger)
    {
        _chatService = chatService;
        _userState = userState;
        _userService = userService;
        _circuitTracker = circuitTracker;
        _actionLog = actionLog;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitId = circuit.Id;
        _circuitTracker.OnCircuitOpened(circuit.Id);

        // Capture client IP from the initial HTTP request (available during circuit setup)
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            _clientIp = IpHelper.GetClientIp(httpContext);
        }

        _logger.LogDebug("Circuit {CircuitId} opened, starting idle timer ({Timeout})", circuit.Id, IdleTimeout);
        StartIdleTimer();
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitTracker.OnConnectionUp(circuit.Id);

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
        StopIdleTimer();

        // Save status for potential restore on reconnect.
        // Don't change user status here — circuit close handles cleanup via RemoveUserAsync.
        if (!string.IsNullOrEmpty(_userState.SessionId) && !string.IsNullOrEmpty(_userState.Username))
        {
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
            // Track activity for this session (used by AreAllSessionsIdle)
            if (!string.IsNullOrEmpty(_userState.SessionId))
            {
                _chatService.TouchSessionActivity(_userState.SessionId);
            }

            // Reset idle timer on any activity (cancels current delay, starts fresh)
            StartIdleTimer();

            // Restore from auto-away if user becomes active again
            if (_isAutoAway && _statusBeforeAway.HasValue && !string.IsNullOrEmpty(_userState.SessionId))
            {
                _isAutoAway = false;
                var restoreTo = _statusBeforeAway.Value;
                _statusBeforeAway = null;

                _logger.LogDebug("Auto-away: {Username} is back, restoring to {Status}",
                    _userState.Username, restoreTo);

                await _chatService.SetUserStatusAsync(_userState.SessionId, restoreTo);
                _userState.Status = restoreTo;
            }

            await next(context);
        };
    }

    /// <summary>
    /// Cancels any pending idle timeout and starts a fresh one.
    /// Called on circuit open, connection up, and any user activity.
    /// </summary>
    private void StartIdleTimer()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = new CancellationTokenSource();
        _ = IdleTimeoutAsync(_idleCts.Token);
    }

    /// <summary>
    /// Cancels any pending idle timeout without starting a new one.
    /// Called on connection down and circuit close.
    /// </summary>
    private void StopIdleTimer()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = null;
    }

    /// <summary>
    /// Waits for the idle timeout, then sets user to Away if not cancelled.
    /// </summary>
    private async Task IdleTimeoutAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(IdleTimeout, token);
            await SetAutoAwayAsync();
        }
        catch (OperationCanceledException)
        {
            // Timer was reset or stopped, ignore
        }
    }

    private async Task SetAutoAwayAsync()
    {
        if (string.IsNullOrEmpty(_userState.SessionId) || string.IsNullOrEmpty(_userState.Username))
            return;

        // Don't override if already auto-away, Away, or Invisible
        var currentStatus = _chatService.GetUserStatus(_userState.Username);
        if (_isAutoAway || currentStatus is UserStatus.Away or UserStatus.Invisible)
            return;

        // Only set auto-away if ALL sessions for this user are idle
        if (!_chatService.AreAllSessionsIdle(_userState.Username, IdleTimeout))
        {
            _logger.LogDebug("Auto-away skipped for {Username}: another session is still active", _userState.Username);
            return;
        }

        _statusBeforeAway = currentStatus;
        _isAutoAway = true;

        _logger.LogDebug("Auto-away: {Username} idle on all sessions, setting to Away", _userState.Username);

        await _chatService.SetUserStatusAsync(_userState.SessionId, UserStatus.Away);
        _userState.Status = UserStatus.Away;
    }

    public void Dispose()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
    }
}
