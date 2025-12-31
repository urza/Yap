namespace Yap.Services;

public class UserStateService
{
    public string? Username { get; set; }
    public string? CircuitId { get; set; }
    public bool IsLoggedIn => !string.IsNullOrEmpty(Username);
    public bool IsJoinedChat => !string.IsNullOrEmpty(CircuitId);
}
