using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Yap.Services;

namespace Yap.Components.Pages;

/// <summary>
/// Thin base class for chat pages - just DI, auth, and shared helpers.
/// </summary>
public abstract class ChatBase : ComponentBase, IAsyncDisposable
{
    // Injected services
    [Inject] protected ChatService ChatService { get; set; } = default!;
    [Inject] protected ChatConfigService ChatConfig { get; set; } = default!;
    [Inject] protected UserStateService UserState { get; set; } = default!;
    [Inject] protected ChatNavigationState NavState { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    // Common accessors
    protected string Username => UserState.Username ?? "";

    // Tab notification state
    protected int unreadCount = 0;
    protected string currentContext = "";

    // Image modal state (each page manages its own)
    protected bool showImageModal = false;
    protected List<string> modalGallery = new();
    protected int modalImageIndex = 0;

    // Recent emojis for quick reactions
    protected List<string> recentEmojis = new();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Auth guard - layout also checks, but this is a fallback
            if (!UserState.IsLoggedIn)
            {
                Navigation.NavigateTo("/");
                return;
            }

            // Join chat if not already joined
            if (!UserState.IsJoinedChat)
            {
                UserState.CircuitId = Guid.NewGuid().ToString();
                await ChatService.AddUserAsync(UserState.CircuitId, Username);
            }

            // Setup tab notifications
            await SetupTabNotifications();

            // Load recent emojis from localStorage
            await LoadRecentEmojisAsync();

            // Let derived class initialize
            await OnInitializedChatAsync();

            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();
        }
    }

    /// <summary>
    /// Called after first render - derived classes load their messages here.
    /// </summary>
    protected virtual Task OnInitializedChatAsync() => Task.CompletedTask;

    #region UI Helpers

    protected async Task ScrollToBottomAsync()
    {
        try { await JS.InvokeVoidAsync("scrollToBottom"); } catch { }
    }

    protected void ShowGallery(List<string> gallery, int startIndex)
    {
        modalGallery = gallery;
        modalImageIndex = startIndex;
        showImageModal = true;
    }

    protected void CloseImageModal()
    {
        showImageModal = false;
        modalGallery = new();
        modalImageIndex = 0;
    }

    #endregion

    #region Tab Notifications

    private async Task SetupTabNotifications()
    {
        try { await JS.InvokeVoidAsync("setupVisibilityListener", DotNetObjectReference.Create(this)); } catch { }
    }

    protected async Task HandleNewMessageNotificationAsync(string messageUser, bool isDM)
    {
        try
        {
            var isVisible = await JS.InvokeAsync<bool>("isPageVisible");
            if (!isVisible)
            {
                unreadCount++;
                var title = $"({unreadCount}) {ChatConfig.ProjectName} | {currentContext}";

                if (isDM)
                {
                    await JS.InvokeVoidAsync("notifyNewMessage", title);
                }
                else
                {
                    await JS.InvokeVoidAsync("setDocumentTitle", title);
                }
            }
        }
        catch { }
    }

    [JSInvokable]
    public async Task OnPageBecameVisible()
    {
        if (unreadCount > 0)
        {
            unreadCount = 0;
            await UpdatePageTitleAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task UpdatePageTitleAsync()
    {
        var title = unreadCount > 0
            ? $"({unreadCount}) {ChatConfig.ProjectName} | {currentContext}"
            : $"{ChatConfig.ProjectName} | {currentContext}";

        try { await JS.InvokeVoidAsync("setDocumentTitle", title); } catch { }
    }

    #endregion

    #region Recent Emojis

    private const int MaxRecentEmojis = 20;

    protected async Task LoadRecentEmojisAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", "recentEmojis");
            if (!string.IsNullOrEmpty(json))
            {
                recentEmojis = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
            }
        }
        catch { }
    }

    protected async Task AddRecentEmojiAsync(string emoji)
    {
        // Remove if already exists (to move to front)
        recentEmojis.Remove(emoji);

        // Add to front
        recentEmojis.Insert(0, emoji);

        // Limit size
        if (recentEmojis.Count > MaxRecentEmojis)
        {
            recentEmojis = recentEmojis.Take(MaxRecentEmojis).ToList();
        }

        // Save to localStorage
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(recentEmojis);
            await JS.InvokeVoidAsync("localStorage.setItem", "recentEmojis", json);
        }
        catch { }
    }

    #endregion

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
