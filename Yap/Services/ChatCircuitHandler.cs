using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Yap.Services;

public class ChatCircuitHandler : CircuitHandler
{
    private readonly ChatService _chatService;
    private readonly UserStateService _userState;

    public ChatCircuitHandler(ChatService chatService, UserStateService userState)
    {
        _chatService = chatService;
        _userState = userState;
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Remove user from chat when their circuit disconnects
        if (!string.IsNullOrEmpty(_userState.SessionId))
        {
            await _chatService.RemoveUserAsync(_userState.SessionId);
            _userState.SessionId = null;
        }

        await base.OnCircuitClosedAsync(circuit, cancellationToken);
    }
}
