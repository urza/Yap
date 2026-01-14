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
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(1);

    private readonly ChatService _chatService;
    private readonly UserStateService _userState;
    private readonly ILogger<ChatCircuitHandler> _logger;

    private readonly Timer _idleTimer;

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
        _logger.LogInformation("Circuit opened, starting idle timer ({Timeout})", IdleTimeout);
        _idleTimer.Start();
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _idleTimer.Stop();

        // Remove user from chat when their circuit disconnects
        if (!string.IsNullOrEmpty(_userState.SessionId))
        {
            await _chatService.RemoveUserAsync(_userState.SessionId);
            _userState.SessionId = null;
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
        return context =>
        {
            // Reset idle timer on any activity (prevents going Away while active)
            _idleTimer.Stop();
            _idleTimer.Start();

            return next(context);
        };
    }

    private async void OnIdleTimeout(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (string.IsNullOrEmpty(_userState.SessionId) || string.IsNullOrEmpty(_userState.Username))
            return;

        // Don't override if already Away or Invisible
        var currentStatus = _chatService.GetUserStatus(_userState.Username);
        if (currentStatus is UserStatus.Away or UserStatus.Invisible)
            return;

        _logger.LogInformation("Auto-away: {Username} idle, setting to Away", _userState.Username);

        await _chatService.SetUserStatusAsync(_userState.SessionId, UserStatus.Away);
        _userState.Status = UserStatus.Away;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
    }
}
