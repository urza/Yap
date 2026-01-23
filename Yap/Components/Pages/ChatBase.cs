using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Yap.Models;
using Yap.Services;

namespace Yap.Components.Pages;

/// <summary>
/// Base class for chat pages with shared state, event handlers, and message actions.
/// </summary>
public abstract class ChatBase : ComponentBase, IAsyncDisposable
{
    // Injected services
    [Inject] protected ChatService ChatService { get; set; } = default!;
    [Inject] protected ChatConfigService ChatConfig { get; set; } = default!;
    [Inject] protected UserStateService UserState { get; set; } = default!;
    [Inject] protected UserService UserService { get; set; } = default!;
    [Inject] protected ChatNavigationState NavState { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;


    // Common accessors
    protected Guid UserId => UserState.UserId ?? Guid.Empty;
    protected string Username => UserState.Username ?? "";

    // Channel state - set by derived classes
    protected Guid channelId;
    protected List<ChatMessage> messages = new();

    // UI state
    protected Guid? hoveredMessageId = null;
    protected Guid? editingMessageId = null;
    protected string editContent = "";

    // Tab notification state
    protected int unreadCount = 0;
    protected string currentContext = "";

    // Infinite scroll state
    protected bool isLoadingMore = false;
    protected bool hasMoreMessages = true;
    protected const int PageSize = 50;

    // Disposable references
    private DotNetObjectReference<ChatBase>? _visibilityRef;
    private DotNetObjectReference<ChatBase>? _scrollDismissRef;

    // Image modal state
    protected bool showImageModal = false;
    protected List<string> modalGallery = new();
    protected int modalImageIndex = 0;

    // Recent emojis for full emoji drawer
    protected List<string> recentEmojis = new();

    // Emoji usage counts for quick reactions (loaded once per session)
    private Dictionary<string, int> emojiCounts = new();
    protected List<string> quickReactions = new();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Capture device type from middleware (only available during initial HTTP request)
            if (UserState.IsMobile == null)
            {
                var httpContext = HttpContextAccessor.HttpContext;
                if (httpContext?.Items.TryGetValue("IsMobile", out var isMobileObj) == true && isMobileObj is bool isMobile)
                {
                    UserState.IsMobile = isMobile;
                }
            }

            // Auth guard - layout also checks, but this is a fallback
            if (!UserState.IsLoggedIn)
            {
                Navigation.NavigateTo("/");
                return;
            }

            // Join chat - always create fresh session on page load
            // Old session may be stale (e.g., marked Invisible by circuit close during refresh)
            if (UserState.UserId.HasValue)
            {
                // Check if we need to rejoin (no session, or session doesn't exist in ChatService)
                var needsRejoin = string.IsNullOrEmpty(UserState.SessionId)
                    || !ChatService.HasSession(UserState.SessionId);

                if (needsRejoin)
                {
                    UserState.SessionId = Guid.NewGuid().ToString();
                    await ChatService.AddUserAsync(UserState.SessionId, UserState.UserId.Value, Username, UserState.Status, UserState.IsMobile);
                }
                else
                {
                    // Session exists - ensure status is correct (might have been set to Invisible during reconnect)
                    await ChatService.SetUserStatusAsync(UserState.SessionId, UserState.Status);
                }
            }

            // Setup tab notifications
            await SetupTabNotifications();

            // Load recent emojis from localStorage (for full drawer)
            await LoadRecentEmojisAsync();

            // Load emoji counts and compute quick reactions (cached for session)
            await LoadEmojiCountsAsync();

            // Let derived class initialize
            await OnInitializedChatAsync();

            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();

            // Setup infinite scroll after initial render
            await SetupInfiniteScrollAsync();

            // Setup scroll-to-dismiss for mobile message actions
            await SetupScrollDismissAsync();
        }
    }

    /// <summary>
    /// Called after first render - derived classes load their messages here.
    /// </summary>
    protected virtual Task OnInitializedChatAsync() => Task.CompletedTask;

    #region UI Helpers

    protected async Task ScrollToBottomAsync()
    {
        try { await JS.InvokeVoidAsync("scrollToBottom"); }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to scroll: {ex.Message}"); }
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

    #region Infinite Scroll

    private DotNetObjectReference<ChatBase>? _scrollRef;

    /// <summary>
    /// Loads initial messages (most recent page).
    /// </summary>
    protected void LoadInitialMessages()
    {
        var (msgs, hasMore) = ChatService.GetMessagesPaginated(channelId, PageSize);
        messages = msgs;
        hasMoreMessages = hasMore;
    }

    /// <summary>
    /// Resets pagination state for channel switching.
    /// </summary>
    protected void ResetPaginationState()
    {
        hasMoreMessages = true;
        isLoadingMore = false;
    }

    private async Task SetupInfiniteScrollAsync()
    {
        try
        {
            _scrollRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("setupInfiniteScroll", _scrollRef);
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to setup infinite scroll: {ex.Message}"); }
    }

    /// <summary>
    /// Called from JS when user scrolls near top of messages.
    /// </summary>
    [JSInvokable]
    public async Task OnScrollNearTop()
    {
        if (isLoadingMore || !hasMoreMessages || messages.Count == 0)
            return;

        isLoadingMore = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            // Brief delay for better UX - gives users time to notice the loading indicator
            // and mentally prepare for older messages appearing above
            await Task.Delay(500);

            // Get current scroll info before prepending
            var previousScrollHeight = await JS.InvokeAsync<double>("getScrollHeight");

            // Get messages older than our oldest
            var oldestTimestamp = messages.First().Timestamp;
            var (olderMessages, hasMore) = ChatService.GetMessagesPaginated(
                channelId, PageSize, beforeTimestamp: oldestTimestamp);

            if (olderMessages.Count > 0)
            {
                // Prepend to message list
                messages.InsertRange(0, olderMessages);
                hasMoreMessages = hasMore;

                // Render update
                await InvokeAsync(StateHasChanged);

                // Wait for DOM update then restore scroll position
                await Task.Delay(10); // Small delay for Blazor to update DOM
                await JS.InvokeVoidAsync("restoreScrollPosition", previousScrollHeight);
            }
            else
            {
                hasMoreMessages = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading more messages: {ex.Message}");
        }

        isLoadingMore = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task CleanupInfiniteScrollAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("cleanupInfiniteScroll");
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to cleanup infinite scroll: {ex.Message}"); }

        _scrollRef?.Dispose();
        _scrollRef = null;
    }

    #endregion

    #region Scroll-to-Dismiss (Mobile)

    private async Task SetupScrollDismissAsync()
    {
        try
        {
            _scrollDismissRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("setupScrollDismiss", _scrollDismissRef);
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to setup scroll dismiss: {ex.Message}"); }
    }

    /// <summary>
    /// Sets the hovered message. On mobile, also activates scroll watching for dismiss.
    /// </summary>
    protected void SetHoveredMessage(Guid? messageId)
    {
        hoveredMessageId = messageId;

        // Only do scroll watch on mobile (fire-and-forget to avoid blocking UI)
        if (UserState.IsMobile == true)
        {
            _ = ActivateScrollWatchAsync(messageId.HasValue);
        }
    }

    private async Task ActivateScrollWatchAsync(bool activate)
    {
        try
        {
            if (activate)
                await JS.InvokeVoidAsync("activateScrollWatch");
            else
                await JS.InvokeVoidAsync("deactivateScrollWatch");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Called from JS when user scrolls past threshold - dismisses message actions popup.
    /// </summary>
    [JSInvokable]
    public async Task DismissHoveredMessage()
    {
        await InvokeAsync(() =>
        {
            hoveredMessageId = null;
            StateHasChanged();
        });
    }

    private async Task CleanupScrollDismissAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("cleanupScrollDismiss");
        }
        catch { /* ignore */ }

        _scrollDismissRef?.Dispose();
        _scrollDismissRef = null;
    }

    #endregion

    #region Shared Event Handlers

    protected async Task HandleMessageUpdated(ChatMessage message)
    {
        await InvokeAsync(() =>
        {
            if (message.ChannelId == channelId)
            {
                var index = messages.FindIndex(m => m.Id == message.Id);
                if (index >= 0) messages[index] = message;
                StateHasChanged();
            }
        });
    }

    protected async Task HandleMessageDeleted(Guid messageId, Guid deletedChannelId)
    {
        await InvokeAsync(() =>
        {
            if (deletedChannelId == channelId)
            {
                messages.RemoveAll(m => m.Id == messageId);
                StateHasChanged();
            }
        });
    }

    protected async Task HandleReactionChanged(ChatMessage message)
    {
        await InvokeAsync(() =>
        {
            if (message.ChannelId == channelId)
            {
                var index = messages.FindIndex(m => m.Id == message.Id);
                if (index >= 0) messages[index] = message;
                StateHasChanged();
            }
        });
    }

    #endregion

    #region Message Actions

    protected async Task ToggleReaction(Guid messageId, string emoji)
    {
        await AddRecentEmojiAsync(emoji);
        await IncrementEmojiCountAsync(emoji);
        await ChatService.ToggleReactionAsync(messageId, channelId, UserId, Username, emoji);
    }

    protected void StartEdit(ChatMessage message)
    {
        editingMessageId = message.Id;
        editContent = message.Content;
    }

    protected async Task SaveEdit()
    {
        if (editingMessageId.HasValue && !string.IsNullOrWhiteSpace(editContent))
        {
            await ChatService.EditMessageAsync(editingMessageId.Value, channelId, Username, editContent);
        }
        CancelEdit();
    }

    protected void CancelEdit()
    {
        editingMessageId = null;
        editContent = "";
    }

    protected async Task DeleteMessage(Guid messageId)
    {
        await ChatService.DeleteMessageAsync(messageId, channelId, Username);
    }

    #endregion

    #region Tab Notifications

    private async Task SetupTabNotifications()
    {
        try
        {
            _visibilityRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("setupVisibilityListener", _visibilityRef);
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to setup tab notifications: {ex.Message}"); }
    }

    protected async Task HandleNewMessageNotificationAsync(string messageUser, bool isDM)
    {
        try
        {
            var isVisible = await JS.InvokeAsync<bool>("isPageVisible");
            if (!isVisible)
            {
                unreadCount++;
                var title = BuildPageTitle(isDM);

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
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to handle notification: {ex.Message}"); }
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
        var title = BuildPageTitle(NavState.CurrentDmUser != null);
        try { await JS.InvokeVoidAsync("setDocumentTitle", title); }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to update page title: {ex.Message}"); }
    }

    private string BuildPageTitle(bool isDM)
    {
        var prefix = isDM ? "@" : "#";
        return unreadCount > 0
            ? $"({unreadCount}) {ChatConfig.ProjectName} | {prefix}{currentContext}"
            : $"{ChatConfig.ProjectName} | {prefix}{currentContext}";
    }

    #endregion

    #region Recent Emojis

    private const int MaxRecentEmojis = 20;
    private string RecentEmojisKey => $"recentEmojis_{Username}";

    protected async Task LoadRecentEmojisAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", RecentEmojisKey);
            if (!string.IsNullOrEmpty(json))
            {
                recentEmojis = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
            }
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to load recent emojis: {ex.Message}"); }
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
            await JS.InvokeVoidAsync("localStorage.setItem", RecentEmojisKey, json);
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to save recent emojis: {ex.Message}"); }
    }

    #endregion

    #region Emoji Counts (Quick Reactions)

    private static readonly string[] DefaultQuickEmojis = ["❤️", "😂", "👍"];
    private string EmojiCountsKey => $"emojiCounts_{Username}";

    private async Task LoadEmojiCountsAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", EmojiCountsKey);
            if (!string.IsNullOrEmpty(json))
            {
                emojiCounts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatBase] Failed to load emoji counts: {ex.Message}");
            emojiCounts = new();
        }

        // Compute quick reactions once (cached for session)
        quickReactions = emojiCounts
            .OrderByDescending(x => x.Value)
            .Take(3)
            .Select(x => x.Key)
            .ToList();

        // Fill with defaults if needed
        foreach (var emoji in DefaultQuickEmojis)
        {
            if (quickReactions.Count >= 3) break;
            if (!quickReactions.Contains(emoji))
                quickReactions.Add(emoji);
        }
    }

    private async Task IncrementEmojiCountAsync(string emoji)
    {
        // Update count in memory
        emojiCounts.TryGetValue(emoji, out var count);
        emojiCounts[emoji] = count + 1;

        // Note: quickReactions is NOT updated here - it stays cached for the session

        // Save to localStorage
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(emojiCounts);
            await JS.InvokeVoidAsync("localStorage.setItem", EmojiCountsKey, json);
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to save emoji counts: {ex.Message}"); }
    }

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        await CleanupInfiniteScrollAsync();
        await CleanupScrollDismissAsync();
        _visibilityRef?.Dispose();
    }
}
