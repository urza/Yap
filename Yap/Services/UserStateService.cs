namespace Yap.Services;

public class UserStateService
{
    public string? Username { get; set; }
    public bool IsLoggedIn => !string.IsNullOrEmpty(Username);
}
