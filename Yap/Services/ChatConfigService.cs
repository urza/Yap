namespace Yap.Services;

public class ChatConfigService
{
    private readonly IConfiguration _configuration;
    private readonly Random _random = new();

    public ChatConfigService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string ProjectName => _configuration["ChatSettings:ProjectName"] ?? "Yap";
    public string RoomName => _configuration["ChatSettings:RoomName"] ?? "lobby";

    public string GetRandomWelcomeMessage()
    {
        var messages = _configuration.GetSection("ChatSettings:FunnyTexts:WelcomeMessages").Get<string[]>()
            ?? ["Welcome to {0}!"];
        var message = messages[_random.Next(messages.Length)];
        return string.Format(message, ProjectName);
    }

    public string GetRandomJoinButtonText()
    {
        var texts = _configuration.GetSection("ChatSettings:FunnyTexts:JoinButtonTexts").Get<string[]>()
            ?? ["Join Chat"];
        return texts[_random.Next(texts.Length)];
    }

    public string GetRandomUsernamePlaceholder()
    {
        var placeholders = _configuration.GetSection("ChatSettings:FunnyTexts:UsernamePlaceholders").Get<string[]>()
            ?? ["Enter your username"];
        return placeholders[_random.Next(placeholders.Length)];
    }

    public string GetRandomMessagePlaceholder()
    {
        var placeholders = _configuration.GetSection("ChatSettings:FunnyTexts:MessagePlaceholders").Get<string[]>()
            ?? ["Type a message..."];
        return placeholders[_random.Next(placeholders.Length)];
    }

    public string GetRandomConnectionStatus(bool connected)
    {
        var section = connected ? "ChatSettings:FunnyTexts:ConnectionStatuses:Connected"
            : "ChatSettings:FunnyTexts:ConnectionStatuses:Disconnected";
        var statuses = _configuration.GetSection(section).Get<string[]>()
            ?? [connected ? "Connected" : "Disconnected"];
        return statuses[_random.Next(statuses.Length)];
    }

    public string GetRandomSystemMessage(string username, bool joined)
    {
        var section = joined ? "ChatSettings:FunnyTexts:SystemMessages:UserJoined"
            : "ChatSettings:FunnyTexts:SystemMessages:UserLeft";
        var messages = _configuration.GetSection(section).Get<string[]>()
            ?? [joined ? "{0} joined the chat" : "{0} left the chat"];
        var message = messages[_random.Next(messages.Length)];
        return string.Format(message, username);
    }

    public string GetRandomTypingIndicator(List<string> typingUsers, string currentUser)
    {
        var others = typingUsers.Where(u => u != currentUser).ToList();
        if (others.Count == 0) return "";

        var (configKey, defaultMsg, formatArgs) = others.Count switch
        {
            1 => ("Single", "{0} is typing..", new object[] { others[0] }),
            2 => ("Double", "{0} and {1} are typing..", new object[] { others[0], others[1] }),
            _ => ("Multiple", "{0} and {1} others are typing..", new object[] { others[0], others.Count - 1 })
        };

        var messages = _configuration.GetSection($"ChatSettings:FunnyTexts:TypingIndicators:{configKey}").Get<string[]>()
            ?? [defaultMsg];
        return string.Format(messages[_random.Next(messages.Length)], formatArgs);
    }

    public string GetRandomOnlineUsersHeader(int count)
    {
        var headers = _configuration.GetSection("ChatSettings:FunnyTexts:OnlineUsersHeader").Get<string[]>()
            ?? ["Online Users ({0})"];
        var header = headers[_random.Next(headers.Length)];
        return string.Format(header, count);
    }

    public string GetRandomRoomHeader()
    {
        var headers = _configuration.GetSection("ChatSettings:FunnyTexts:RoomHeaders").Get<string[]>()
            ?? ["# {0}"];
        var header = headers[_random.Next(headers.Length)];
        return string.Format(header, RoomName);
    }
}
