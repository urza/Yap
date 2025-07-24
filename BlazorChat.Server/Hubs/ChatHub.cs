using Microsoft.AspNetCore.SignalR;
using BlazorChat.Server.Services;

namespace BlazorChat.Server.Hubs;

public class ChatHub : Hub
{
    private static readonly Dictionary<string, string> _connections = new();
    private static readonly Dictionary<string, DateTime> _typingUsers = new();
    private readonly ChatHistoryService _chatHistoryService;

    public ChatHub(ChatHistoryService chatHistoryService)
    {
        _chatHistoryService = chatHistoryService;
    }

    public async Task SendMessage(string user, string message)
    {
        var timestamp = DateTime.UtcNow;
        _chatHistoryService.AddMessage(user, message, false);
        await Clients.All.SendAsync("ReceiveMessage", user, message, timestamp);
    }

    public async Task SendImage(string user, string imageUrl)
    {
        var timestamp = DateTime.UtcNow;
        _chatHistoryService.AddMessage(user, imageUrl, true);
        await Clients.All.SendAsync("ReceiveImage", user, imageUrl, timestamp);
    }

    public async Task UserJoined(string username)
    {
        _connections[Context.ConnectionId] = username;
        await Clients.All.SendAsync("UserJoined", username);
        await SendOnlineUsers();
    }

    public async Task StartTyping(string username)
    {
        _typingUsers[username] = DateTime.UtcNow;
        await SendTypingUsers();
    }

    public async Task StopTyping(string username)
    {
        _typingUsers.Remove(username);
        await SendTypingUsers();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connections.TryGetValue(Context.ConnectionId, out var username))
        {
            _connections.Remove(Context.ConnectionId);
            _typingUsers.Remove(username);
            await Clients.All.SendAsync("UserLeft", username);
            await SendOnlineUsers();
            await SendTypingUsers();
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task SendOnlineUsers()
    {
        var users = _connections.Values.Distinct().ToList();
        await Clients.All.SendAsync("OnlineUsers", users);
    }

    private async Task SendTypingUsers()
    {
        // Clean up stale typing indicators (older than 3 seconds)
        var staleUsers = _typingUsers
            .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > 3)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var user in staleUsers)
        {
            _typingUsers.Remove(user);
        }

        var typingList = _typingUsers.Keys.ToList();
        await Clients.All.SendAsync("TypingUsers", typingList);
    }
}