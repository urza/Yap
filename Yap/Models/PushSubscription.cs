namespace Yap.Models;

public class PushSubscription
{
    public string Endpoint { get; set; } = "";
    public string Username { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
