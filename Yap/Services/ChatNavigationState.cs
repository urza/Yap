namespace Yap.Services;

/// <summary>
/// Scoped service for sharing navigation state between pages and layout components.
/// Pages update the state, layout components read and react to changes.
/// </summary>
public class ChatNavigationState
{
    public string Title { get; private set; } = "";
    public Guid? CurrentRoomId { get; private set; }
    public string? CurrentDmUser { get; private set; }
    public bool SidebarOpen { get; set; }

    public event Action? OnChange;

    public void SetRoomContext(Guid roomId, string roomName)
    {
        CurrentRoomId = roomId;
        CurrentDmUser = null;
        Title = $"# {roomName}";
        NotifyStateChanged();
    }

    public void SetDmContext(string dmUser)
    {
        CurrentRoomId = null;
        CurrentDmUser = dmUser;
        Title = $"DM with {dmUser}";
        NotifyStateChanged();
    }

    public void ToggleSidebar()
    {
        SidebarOpen = !SidebarOpen;
        NotifyStateChanged();
    }

    public void OpenSidebar()
    {
        SidebarOpen = true;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
