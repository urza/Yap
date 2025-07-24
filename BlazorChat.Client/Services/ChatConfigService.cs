using Microsoft.Extensions.Configuration;

namespace BlazorChat.Client.Services;

public class ChatConfigService
{
    private readonly IConfiguration _configuration;
    private readonly Random _random = new();
    
    public ChatConfigService(IConfiguration configuration)
    {
        _configuration = configuration;
    }
    
    public string ProjectName => _configuration["ChatSettings:ProjectName"] ?? "Chat";
    public string RoomName => _configuration["ChatSettings:RoomName"] ?? "lobby";
    
    public string GetRandomWelcomeMessage()
    {
        var messages = _configuration.GetSection("ChatSettings:FunnyTexts:WelcomeMessages").Get<string[]>() 
            ?? new[] { "Welcome to {0}!" };
        var message = messages[_random.Next(messages.Length)];
        return string.Format(message, ProjectName);
    }
    
    public string GetRandomJoinButtonText()
    {
        var texts = _configuration.GetSection("ChatSettings:FunnyTexts:JoinButtonTexts").Get<string[]>() 
            ?? new[] { "Join Chat" };
        return texts[_random.Next(texts.Length)];
    }
    
    public string GetRandomUsernamePlaceholder()
    {
        var placeholders = _configuration.GetSection("ChatSettings:FunnyTexts:UsernamePlaceholders").Get<string[]>() 
            ?? new[] { "Enter your username" };
        return placeholders[_random.Next(placeholders.Length)];
    }
    
    public string GetRandomMessagePlaceholder()
    {
        var placeholders = _configuration.GetSection("ChatSettings:FunnyTexts:MessagePlaceholders").Get<string[]>() 
            ?? new[] { "Type a message..." };
        return placeholders[_random.Next(placeholders.Length)];
    }
    
    public string GetRandomConnectionStatus(bool connected)
    {
        var section = connected ? "ChatSettings:FunnyTexts:ConnectionStatuses:Connected" 
            : "ChatSettings:FunnyTexts:ConnectionStatuses:Disconnected";
        var statuses = _configuration.GetSection(section).Get<string[]>() 
            ?? new[] { connected ? "Connected" : "Disconnected" };
        return statuses[_random.Next(statuses.Length)];
    }
    
    public string GetRandomSystemMessage(string username, bool joined)
    {
        var section = joined ? "ChatSettings:FunnyTexts:SystemMessages:UserJoined" 
            : "ChatSettings:FunnyTexts:SystemMessages:UserLeft";
        var messages = _configuration.GetSection(section).Get<string[]>() 
            ?? new[] { joined ? "{0} joined the chat" : "{0} left the chat" };
        var message = messages[_random.Next(messages.Length)];
        return string.Format(message, username);
    }
    
    public string GetRandomTypingIndicator(List<string> typingUsers, string currentUser)
    {
        var otherTypingUsers = typingUsers.Where(u => u != currentUser).ToList();
        if (!otherTypingUsers.Any()) return "";
        
        if (otherTypingUsers.Count == 1)
        {
            var messages = _configuration.GetSection("ChatSettings:FunnyTexts:TypingIndicators:Single").Get<string[]>() 
                ?? new[] { "{0} is typing.." };
            var message = messages[_random.Next(messages.Length)];
            return string.Format(message, otherTypingUsers[0]);
        }
        else if (otherTypingUsers.Count == 2)
        {
            var messages = _configuration.GetSection("ChatSettings:FunnyTexts:TypingIndicators:Double").Get<string[]>() 
                ?? new[] { "{0} and {1} are typing.." };
            var message = messages[_random.Next(messages.Length)];
            return string.Format(message, otherTypingUsers[0], otherTypingUsers[1]);
        }
        else
        {
            var messages = _configuration.GetSection("ChatSettings:FunnyTexts:TypingIndicators:Multiple").Get<string[]>() 
                ?? new[] { "{0} and {1} others are typing.." };
            var message = messages[_random.Next(messages.Length)];
            return string.Format(message, otherTypingUsers[0], otherTypingUsers.Count - 1);
        }
    }
    
    public string GetRandomOnlineUsersHeader(int count)
    {
        var headers = _configuration.GetSection("ChatSettings:FunnyTexts:OnlineUsersHeader").Get<string[]>() 
            ?? new[] { "Online Users ({0})" };
        var header = headers[_random.Next(headers.Length)];
        return string.Format(header, count);
    }
    
    public string GetRandomRoomHeader()
    {
        var headers = _configuration.GetSection("ChatSettings:FunnyTexts:RoomHeaders").Get<string[]>() 
            ?? new[] { "# {0}" };
        var header = headers[_random.Next(headers.Length)];
        return string.Format(header, RoomName);
    }
}