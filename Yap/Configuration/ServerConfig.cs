namespace Yap.Configuration;

/// <summary>
/// The server's settings as the code reads them: the values of appsettings.json under ChatSettings, and the
/// configured text lists with their fallbacks (GEN-12). The client picks a text from a list when its context
/// appears; the server only hands the lists over.
/// </summary>
public sealed class ServerConfig(IConfiguration configuration)
{
    public string ProjectName => configuration["ChatSettings:ProjectName"] ?? "Yap";
    public bool WelcomePageEnabled => configuration.GetValue("ChatSettings:WelcomePageEnabled", true);
    public string UploadUrl => configuration["ChatSettings:UploadUrl"] ?? "";
    public string Ipv4BeaconUrl => configuration["ChatSettings:Ipv4BeaconUrl"] ?? "";
    public int MaxUploadSizeMB => configuration.GetValue("ChatSettings:MaxUploadSizeMB", 100);   // IB-32

    /// <summary>Every configured list a signed-in client uses. The room header, the people header and the lobby
    /// lines of the room build are not read: this build has no room (docs/dmonly.md).</summary>
    public TextsDto Texts() => new(
        Placeholders: List("MessagePlaceholders", "Type a message..."),                 // IB-16
        TypingOne: List("TypingIndicators:Single", "{0} is typing.."),                  // IB-60
        TypingTwo: List("TypingIndicators:Double", "{0} and {1} are typing.."),
        TypingMany: List("TypingIndicators:Multiple", "{0} and {1} others are typing.."),
        PillTexts: List("NewMessagesPill", "New messages"),                              // TL-17
        LoginHeadings: List("WelcomeMessages", "Welcome to {0}!"),                       // AU-07
        LoginButtons: List("JoinButtonTexts", "Join Chat"),
        LoginPlaceholders: List("UsernamePlaceholders", "Enter your username"));

    /// <summary>The lists a signed-out page needs, and nothing else.</summary>
    public LoginTextsDto LoginTexts() => new(
        List("WelcomeMessages", "Welcome to {0}!"), List("JoinButtonTexts", "Join Chat"), List("UsernamePlaceholders", "Enter your username"));

    private string[] List(string key, string fallback) =>
        configuration.GetSection($"ChatSettings:FunnyTexts:{key}").Get<string[]>() is { Length: > 0 } items ? items : [fallback];
}

public sealed record TextsDto(
    string[] Placeholders, string[] TypingOne, string[] TypingTwo, string[] TypingMany, string[] PillTexts,
    string[] LoginHeadings, string[] LoginButtons, string[] LoginPlaceholders);

public sealed record LoginTextsDto(string[] LoginHeadings, string[] LoginButtons, string[] LoginPlaceholders);
