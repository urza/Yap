namespace Yap.Services;

public class ChatConfigService
{
    private readonly IConfiguration _configuration;

    public ChatConfigService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string ProjectName => _configuration["ChatSettings:ProjectName"] ?? "Yap";
    public string RoomName => _configuration["ChatSettings:RoomName"] ?? "lobby";

    public string GetRandomWelcomeMessage()
        => GetRandomText("WelcomeMessages", "Welcome to {0}!", ProjectName);

    public string GetRandomJoinButtonText()
        => GetRandomText("JoinButtonTexts", "Join Chat");

    public string GetRandomUsernamePlaceholder()
        => GetRandomText("UsernamePlaceholders", "Enter your username");

    public string GetRandomMessagePlaceholder()
        => GetRandomText("MessagePlaceholders", "Type a message...");

    public string GetRandomConnectionStatus(bool connected)
    {
        var key = connected ? "ConnectionStatuses:Connected" : "ConnectionStatuses:Disconnected";
        var fallback = connected ? "Connected" : "Disconnected";
        return GetRandomText(key, fallback);
    }

    public string GetRandomSystemMessage(string username, bool joined)
    {
        var key = joined ? "SystemMessages:UserJoined" : "SystemMessages:UserLeft";
        var fallback = joined ? "{0} joined the chat" : "{0} left the chat";
        return GetRandomText(key, fallback, username);
    }

    public string GetRandomTypingIndicator(List<string> typingUsers, string currentUser)
    {
        var others = typingUsers.Where(u => u != currentUser).ToList();
        if (others.Count == 0) return "";

        var (configKey, defaultMsg, formatArgs) = others.Count switch
        {
            1 => ("TypingIndicators:Single", "{0} is typing..", new object[] { others[0] }),
            2 => ("TypingIndicators:Double", "{0} and {1} are typing..", new object[] { others[0], others[1] }),
            _ => ("TypingIndicators:Multiple", "{0} and {1} others are typing..", new object[] { others[0], others.Count - 1 })
        };

        return GetRandomText(configKey, defaultMsg, formatArgs);
    }

    public string GetRandomOnlineUsersHeader(int count)
        => GetRandomText("OnlineUsersHeader", "Online Users ({0})", count);

    public string GetRandomRoomHeader()
        => GetRandomRoomHeader(RoomName);

    public string GetRandomRoomHeader(string roomName)
        => GetRandomText("RoomHeaders", "# {0}", roomName);

    private string GetRandomText(string configKey, string fallback, params object[] args)
    {
        var items = _configuration.GetSection($"ChatSettings:FunnyTexts:{configKey}").Get<string[]>()
            ?? [fallback];
        var text = items[Random.Shared.Next(items.Length)];
        return args.Length > 0 ? string.Format(text, args) : text;
    }
}
