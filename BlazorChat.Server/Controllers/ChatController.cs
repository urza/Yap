using Microsoft.AspNetCore.Mvc;
using BlazorChat.Server.Services;

namespace BlazorChat.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatHistoryService _chatHistoryService;

    public ChatController(ChatHistoryService chatHistoryService)
    {
        _chatHistoryService = chatHistoryService;
    }

    [HttpGet("history")]
    public IActionResult GetHistory([FromQuery] int count = 50)
    {
        var messages = _chatHistoryService.GetRecentMessages(count);
        return Ok(messages);
    }
}