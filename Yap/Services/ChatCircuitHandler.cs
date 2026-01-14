using Microsoft.AspNetCore.Components.Server.Circuits;
using Yap.Models;
using Timer = System.Timers.Timer;

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
    private readonly ILogger<ChatCircuitHandler> _logger;

    private readonly Timer _idleTimer;
    private UserStatus? _statusBeforeDisconnect;
    private bool _isAutoAway;
    private UserStatus? _statusBeforeAway;

    public ChatCircuitHandler(
        ChatService chatService,
        UserStateService userState,
        ILogger<ChatCircuitHandler> logger)
    {
        _chatService = chatService;
        _userState = userState;
        _logger = logger;

        _idleTimer = new Timer
        {
            Interval = IdleTimeout.TotalMilliseconds,
            AutoReset = false
        };
        _idleTimer.Elapsed += OnIdleTimeout;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Circuit opened, starting idle timer ({Timeout})", IdleTimeout);
        _idleTimer.Start();
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // User reconnected - restore their previous status
        if (!string.IsNullOrEmpty(_userState.SessionId) && _statusBeforeDisconnect.HasValue)
        {
            _logger.LogDebug("Connection restored for {Username}, restoring status to {Status}",
                _userState.Username, _statusBeforeDisconnect.Value);

            await _chatService.SetUserStatusAsync(_userState.SessionId, _statusBeforeDisconnect.Value);
            _userState.Status = _statusBeforeDisconnect.Value;
            _statusBeforeDisconnect = null;
        }

        _idleTimer.Start();
        await base.OnConnectionUpAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _idleTimer.Stop();

        // Mark user as Invisible (grey) when connection drops
        if (!string.IsNullOrEmpty(_userState.SessionId))
        {
            var currentStatus = _chatService.GetUserStatus(_userState.Username!);
            if (currentStatus.HasValue && currentStatus != UserStatus.Invisible)
            {
                _statusBeforeDisconnect = currentStatus;
                _logger.LogDebug("Connection lost for {Username}, marking as Invisible", _userState.Username);
                await _chatService.SetUserStatusAsync(_userState.SessionId, UserStatus.Invisible);
            }
        }

        await base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _idleTimer.Stop();

        // Circuit fully closed - mark as Invisible (same as disconnect)
        // Don't remove user - they stay grey in the list
        if (!string.IsNullOrEmpty(_userState.SessionId))
        {
            var currentStatus = _chatService.GetUserStatus(_userState.Username!);
            if (currentStatus.HasValue && currentStatus != UserStatus.Invisible)
            {
                _logger.LogDebug("Circuit closed for {Username}, marking as Invisible", _userState.Username);
                await _chatService.SetUserStatusAsync(_userState.SessionId, UserStatus.Invisible);
            }
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
            // Reset idle timer on any activity (prevents going Away while active)
            _idleTimer.Stop();
            _idleTimer.Start();

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

    private async void OnIdleTimeout(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (string.IsNullOrEmpty(_userState.SessionId) || string.IsNullOrEmpty(_userState.Username))
            return;

        // Don't override if already auto-away, Away, or Invisible
        var currentStatus = _chatService.GetUserStatus(_userState.Username);
        if (_isAutoAway || currentStatus is UserStatus.Away or UserStatus.Invisible)
            return;

        _statusBeforeAway = currentStatus;
        _isAutoAway = true;

        _logger.LogDebug("Auto-away: {Username} idle, setting to Away", _userState.Username);

        await _chatService.SetUserStatusAsync(_userState.SessionId, UserStatus.Away);
        _userState.Status = UserStatus.Away;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
    }
}
