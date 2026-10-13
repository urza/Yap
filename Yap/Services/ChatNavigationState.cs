using Microsoft.AspNetCore.Components;

namespace Yap.Services;

/// <summary>
/// Scoped service for sharing navigation state between pages and layout components.
/// Pages update the state, layout components read and react to changes.
///
/// .NET 10 PERSISTENT STATE:
/// Properties marked with [PersistentState] are automatically serialized when
/// the circuit is evicted and restored when the user reconnects.
///
/// Since the chat page moved to the JS client, the only Blazor pages under ChatLayout are
/// Settings and ChannelSettings, so this holds the header title and the mobile sidebar state.
///
/// Note: The OnChange event is NOT persisted (events can't be serialized).
/// Components need to re-subscribe after circuit restoration.
/// </summary>
public class ChatNavigationState
{
    /// <summary>
    /// Display title for the current view (e.g., "# lobby" or "@alice").
    /// Persisted so the header shows correct title after reconnection.
    /// Note: Setter must be public for [PersistentState] to work.
    /// </summary>
    [PersistentState]
    public string Title { get; set; } = "";

    /// <summary>
    /// Whether the sidebar is currently open (for mobile responsiveness).
    /// Persisted for convenience, though less critical than navigation state.
    /// </summary>
    [PersistentState]
    public bool SidebarOpen { get; set; }

    /// <summary>
    /// Event fired when any navigation state changes.
    /// Note: Event handlers are NOT persisted - components must re-subscribe after reconnection.
    /// </summary>
    public event Action? OnChange;

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

    /// <summary>
    /// Closes the mobile slide-in sidebar. Safe to call unconditionally: the
    /// sidebar-open class only has effect under the max-width:768px media query,
    /// so on desktop (where the sidebar is always visible) this changes nothing.
    /// </summary>
    public void CloseSidebar()
    {
        if (!SidebarOpen) return;
        SidebarOpen = false;
        NotifyStateChanged();
    }

    /// <summary>
    /// Whether the header should show a back arrow (e.g. Settings, Admin pages).
    /// Not persisted — transient UI state.
    /// </summary>
    public bool ShowBackButton { get; set; }

    public void SetTitle(string title)
    {
        Title = title;
        ShowBackButton = false;
        NotifyStateChanged();
    }

    public void SetTitleWithBack(string title)
    {
        Title = title;
        ShowBackButton = true;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
