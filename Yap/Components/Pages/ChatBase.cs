using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Yap.Models;
using Yap.Services;
using Yap.Services.Gifs;

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
    [Inject] protected SystemBotService BotService { get; set; } = default!;
    [Inject] protected LinkPreviewService LinkPreviewService { get; set; } = default!;
    [Inject] protected LinkPreviewSettingsService LinkPreviewSettings { get; set; } = default!;
    [Inject] protected MediaCacheService MediaCacheService { get; set; } = default!;
    [Inject] protected GifService GifService { get; set; } = default!;
    [Inject] protected ILogger<ChatBase> Logger { get; set; } = default!;

    // Common accessors
    protected Guid UserId => UserState.UserId ?? Guid.Empty;
    protected string Username => UserState.Username ?? "";

    /// <summary>
    /// Whether an incoming message on the currently-viewed channel should auto-mark it as read.
    /// True only when THIS device's page is foreground AND the user is not Away — so a
    /// backgrounded/locked/away device doesn't silently clear unread for the user's other devices
    /// (read state is shared per-user, Discord-style). Online and Invisible both qualify.
    /// </summary>
    protected bool ShouldMarkReadOnReceive() =>
        !string.IsNullOrEmpty(UserState.SessionId)
        && ChatService.IsSessionPageVisible(UserState.SessionId)
        && ChatService.GetUserStatus(Username) != UserStatus.Away;

    // Channel state - set by derived classes
    protected Guid channelId;
    protected List<ChatMessage> messages = new();

    // UI state
    protected Guid? editingMessageId = null;
    protected string editContent = "";

    // Tab notification state
    protected int unreadCount = 0;
    protected string currentContext = "";

    // Infinite scroll state
    protected bool isLoadingMore = false;
    protected bool hasMoreMessages = true;
    protected bool historyLimited = false;
    protected const int PageSize = 50;

    /// <summary>
    /// If consecutive messages from the same user are more than this apart,
    /// show the avatar/header again (as if it were a new message group).
    /// </summary>
    protected static readonly TimeSpan MessageGroupingTimeout = TimeSpan.FromHours(1);

    // Disposable references
    private DotNetObjectReference<ChatBase>? _visibilityRef;

    // Media modal state
    protected bool showImageModal = false;
    protected List<string> modalGallery = new();
    protected List<string> modalVideoGallery = new();
    protected int modalImageIndex = 0;

    // Reply state
    protected ChatMessage? replyingToMessage = null;

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

            // Detect client timezone and locale (once per session)
            if (UserState.TimeZone == null)
            {
                try
                {
                    var info = await JS.InvokeAsync<ClientLocaleInfo>("getClientLocaleInfo");
                    UserState.TimeZone = info.TimeZone;
                    UserState.Locale = info.Locale;
                    UserState.DateFormat ??= LocaleResolver.GuessDateFormatFromLocale(info.Locale);

                    // Persist to User model so admin page can see it
                    if (UserState.UserId.HasValue)
                        await UserService.UpdateLocaleAsync(UserState.UserId.Value, info.TimeZone, info.Locale, UserState.DateFormat);
                }
                catch { /* JS not available yet, will use fallbacks */ }
            }

            // Detect whether this client is running as an installed PWA (display-mode: standalone)
            // and record it on the User model so the admin page can see who has Yap installed.
            // Only persisted when standalone — non-PWA sessions never touch the timestamp.
            if (UserState.UserId.HasValue)
            {
                try
                {
                    var isPwa = await JS.InvokeAsync<bool>("isPwaInstalled");
                    if (isPwa)
                        await UserService.MarkPwaInstalledAsync(UserState.UserId.Value);
                }
                catch { /* JS not available yet, ignore */ }
            }

            // Auth guard - layout also checks, but this is a fallback
            // UserState is populated by AuthMiddleware before Blazor starts
            if (!UserState.IsLoggedIn)
            {
                var currentUrl = Navigation.ToBaseRelativePath(Navigation.Uri);
                var returnUrl = Uri.EscapeDataString("/" + currentUrl);
                Navigation.NavigateTo($"/?returnUrl={returnUrl}");
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
                    var clientIp = HttpContextAccessor.HttpContext?.Items["ClientIp"]?.ToString();
                    await ChatService.AddUserAsync(UserState.SessionId, UserState.UserId.Value, Username, UserState.Status, UserState.IsMobile, clientIp);
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

    #region Message Grouping

    /// <summary>
    /// Determines whether a message should show its avatar and username header,
    /// or be visually grouped with the previous message.
    /// </summary>
    protected bool ShouldShowHeader(int index)
    {
        if (index == 0) return true;

        var message = messages[index];
        var previous = messages[index - 1];

        // Different author — always show
        if (previous.Username != message.Username) return true;

        // Replies always start a new group
        if (message.ReplyToMessageId != null) return true;

        // Too much time passed — start a new group
        if ((message.Timestamp - previous.Timestamp) >= MessageGroupingTimeout) return true;

        return false;
    }

    #endregion

    #region UI Helpers

    protected async Task ScrollToBottomAsync()
    {
        try { await JS.InvokeVoidAsync("scrollToBottom"); }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to scroll: {ex.Message}"); }
    }

    /// <summary>
    /// Gets the profile picture URL for a username.
    /// </summary>
    protected string? GetProfilePictureUrl(string username)
    {
        var user = UserService.GetByUsername(username);
        return user?.ProfilePictureUrl;
    }

    /// <summary>
    /// Gets the display name for a username.
    /// </summary>
    protected string? GetDisplayName(string username)
    {
        var user = UserService.GetByUsername(username);
        return user?.EffectiveDisplayName;
    }

    protected void ShowGallery(List<string> gallery, int startIndex)
    {
        modalGallery = gallery;
        modalVideoGallery = new();
        modalImageIndex = startIndex;
        showImageModal = true;
    }

    protected void ShowVideoGallery(List<string> videos, int startIndex)
    {
        modalGallery = new();
        modalVideoGallery = videos;
        modalImageIndex = startIndex;
        showImageModal = true;
    }

    protected void CloseImageModal()
    {
        showImageModal = false;
        modalGallery = new();
        modalVideoGallery = new();
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
        var isAdmin = ChatService.IsAdmin(UserId);
        var (msgs, hasMore) = ChatService.GetMessagesPaginated(channelId, PageSize, isAdmin: isAdmin, userId: UserId);
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
            var isAdmin = ChatService.IsAdmin(UserId);
            var (olderMessages, hasMore) = ChatService.GetMessagesPaginated(
                channelId, PageSize, beforeTimestamp: oldestTimestamp, isAdmin: isAdmin, userId: UserId);

            if (olderMessages.Count > 0)
            {
                // Prepend to message list
                messages.InsertRange(0, olderMessages);
                hasMoreMessages = hasMore;

                // Queue link preview fetches for newly loaded messages
                QueuePreviewsForMessages(olderMessages);

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
        // Pure JS/CSS solution - no Blazor callbacks needed
        try
        {
            await JS.InvokeVoidAsync("setupScrollDismiss");
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to setup scroll dismiss: {ex.Message}"); }
    }

    /// <summary>
    /// Called on touch start to activate scroll watching (for mobile dismiss).
    /// </summary>
    protected void ActivateScrollWatch()
    {
        // Fire-and-forget JS call
        _ = JS.InvokeVoidAsync("activateScrollWatch");
    }

    private async Task CleanupScrollDismissAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("cleanupScrollDismiss");
        }
        catch (Exception ex) { Console.WriteLine($"[ChatBase] Failed to cleanup scroll dismiss: {ex.Message}"); }
    }

    #endregion

    #region Shared Event Handlers

    protected async void HandleMessageUpdated(ChatMessage message)
    {
        if (message.ChannelId != channelId) return;
        try
        {
            await InvokeAsync(() =>
            {
                var index = messages.FindIndex(m => m.Id == message.Id);
                if (index >= 0) messages[index] = message;
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException or InvalidOperationException)
                Logger.LogWarning("HandleMessageUpdated: {Message}", ex.Message);
            else
                Logger.LogError(ex, "Error in HandleMessageUpdated");
        }
    }

    protected async void HandleMessageDeleted(Guid messageId, Guid deletedChannelId)
    {
        if (deletedChannelId != channelId) return;
        try
        {
            await InvokeAsync(() =>
            {
                messages.RemoveAll(m => m.Id == messageId);
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException or InvalidOperationException)
                Logger.LogWarning("HandleMessageDeleted: {Message}", ex.Message);
            else
                Logger.LogError(ex, "Error in HandleMessageDeleted");
        }
    }

    protected async void HandleReactionChanged(ChatMessage message)
    {
        if (message.ChannelId != channelId) return;

        try
        {
            // Check if near bottom BEFORE updating (to decide if we should scroll after).
            // Best-effort: a canceled JS round-trip (busy/disconnecting circuit) must not skip
            // the reaction update + StateHasChanged below.
            bool wasNearBottom = false;
            try { wasNearBottom = await JS.InvokeAsync<bool>("isNearBottom", 100); }
            catch { /* couldn't measure scroll position; update anyway, skip auto-scroll */ }

            await InvokeAsync(() =>
            {
                var index = messages.FindIndex(m => m.Id == message.Id);
                if (index >= 0) messages[index] = message;
                StateHasChanged();
            });

            // If viewer was near bottom, scroll to reveal the reaction
            if (wasNearBottom)
            {
                await Task.Delay(50);
                await ScrollToBottomAsync();
            }
        }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException or InvalidOperationException or JSDisconnectedException or OperationCanceledException)
                Logger.LogWarning("HandleReactionChanged: {Message}", ex.Message);
            else
                Logger.LogError(ex, "Error in HandleReactionChanged");
        }
    }

    #endregion

    #region Message Actions

    protected async Task ToggleReaction(Guid messageId, string emoji)
    {
        AddRecentEmoji(emoji);
        IncrementEmojiCount(emoji);
        await ChatService.ToggleReactionAsync(messageId, channelId, UserId, Username, emoji);
        // Scroll handling is done in HandleReactionChanged for all viewers
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

    // Invoked (fire-and-forget from JS) when an emoji is inserted into the message input.
    // MUST stay render-free: the JS click handler splices the emoji into the textarea client-side,
    // so a StateHasChanged here could push the server's older messageText back over it and clobber
    // the inserted emoji. Keep these calls in-memory only (they persist via UserService dirty-flush).
    protected void HandleInputEmojiUsed(string emoji)
    {
        AddRecentEmoji(emoji);
        IncrementEmojiCount(emoji);
    }

    protected void StartReply(ChatMessage message)
    {
        replyingToMessage = message;
    }

    protected void CancelReply()
    {
        replyingToMessage = null;
    }

    protected ChatMessage? GetReplyTarget(ChatMessage message)
    {
        if (message.ReplyToMessageId == null) return null;
        var local = messages.FirstOrDefault(m => m.Id == message.ReplyToMessageId);
        if (local != null) return local;
        return ChatService.GetMessageById(message.ChannelId, message.ReplyToMessageId.Value);
    }

    protected async Task ScrollToMessageAsync(Guid messageId)
    {
        if (messages.Any(m => m.Id == messageId))
        {
            await JS.InvokeVoidAsync("scrollToMessage", messageId.ToString());
        }
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
    public async Task OnPageVisibilityChanged(bool visible)
    {
        if (!string.IsNullOrEmpty(UserState.SessionId))
            ChatService.SetPageVisibility(UserState.SessionId, visible);

        if (visible && unreadCount > 0)
        {
            unreadCount = 0;
            await UpdatePageTitleAsync();
            await InvokeAsync(StateHasChanged);
        }

        // Resuming the tab means we're now looking at this channel — advance read state.
        // Closes the gap where a backgrounded tab accumulated unread (the on-receive mark is
        // gated on visibility) and was then foregrounded without navigating. Not silent, so the
        // user's OTHER devices clear the badge promptly. Skipped while Away (consistent with the
        // on-receive gate); foregrounding usually restores Away→Online via inbound activity anyway.
        if (visible && channelId != Guid.Empty && UserId != Guid.Empty
            && ChatService.GetUserStatus(Username) != UserStatus.Away
            && ChatService.GetUnreadCount(UserId, channelId) > 0)
        {
            await ChatService.MarkChannelAsReadAsync(UserId, channelId, callerSessionId: UserState.SessionId);
        }

        // On PWA/tab resume, snap back to the bottom. Media (videos, link
        // previews) may have finished loading while backgrounded and shifted
        // the layout, leaving the user stranded above the real bottom.
        if (visible)
        {
            await ScrollToBottomAsync();
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

    protected Task LoadRecentEmojisAsync()
    {
        recentEmojis = UserService.GetRecentEmojis(Username);
        return Task.CompletedTask;
    }

    protected void AddRecentEmoji(string emoji)
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

        UserService.UpdateRecentEmojis(UserId, recentEmojis);
    }

    #endregion

    #region Emoji Counts (Quick Reactions)

    private static readonly string[] DefaultQuickEmojis = ["❤️", "😂", "👍"];

    private Task LoadEmojiCountsAsync()
    {
        emojiCounts = UserService.GetEmojiCounts(Username);

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

        return Task.CompletedTask;
    }

    private void IncrementEmojiCount(string emoji)
    {
        // Update count in memory
        emojiCounts.TryGetValue(emoji, out var count);
        emojiCounts[emoji] = count + 1;

        // Note: quickReactions is NOT updated here - it stays cached for the session

        UserService.UpdateEmojiCounts(UserId, emojiCounts);
    }

    #endregion

    #region Link Previews

    /// <summary>
    /// Gets cached link previews for a message (max 5). Returns null if previews disabled or no URLs.
    /// </summary>
    protected List<Models.LinkPreview>? GetLinkPreviews(Models.ChatMessage message)
    {
        if (!LinkPreviewSettings.Enabled || message.HasMedia || string.IsNullOrEmpty(message.Content))
            return null;

        var urls = LinkPreviewService.ExtractUrls(message.Content);
        if (urls.Count == 0)
            return null;

        var previews = new List<Models.LinkPreview>();
        foreach (var url in urls.Take(5))
        {
            var preview = LinkPreviewService.GetCachedPreview(url);

            // Check for cached media (memory + disk) and attach to preview
            var media = MediaCacheService.GetCachedMedia(url);
            if (media != null)
            {
                preview ??= LinkPreviewService.GetOrCreatePreview(url);
                preview.CachedMediaUrl = media.LocalUrl;
                preview.MediaType = media.MediaType;
                preview.MediaDurationSeconds = media.DurationSeconds;
                if (media.Width > 0 && media.Height > 0)
                {
                    preview.MediaWidth = media.Width;
                    preview.MediaHeight = media.Height;
                }
            }

            if (preview != null && (preview.HasContent || preview.CachedMediaUrl != null))
                previews.Add(preview);
        }

        return previews.Count > 0 ? previews : null;
    }

    /// <summary>
    /// Queues preview fetches for all messages that contain URLs. Called on page load / infinite scroll.
    /// </summary>
    protected void QueuePreviewsForMessages(List<Models.ChatMessage> msgs)
    {
        if (!LinkPreviewSettings.Enabled) return;

        foreach (var msg in msgs)
        {
            if (msg.HasMedia || string.IsNullOrEmpty(msg.Content)) continue;

            var urls = LinkPreviewService.ExtractUrls(msg.Content);
            foreach (var url in urls.Take(5))
            {
                LinkPreviewService.QueueFetch(msg.Id, url);

                // Also queue media caching (yt-dlp determines if URL is supported)
                if (LinkPreviewSettings.MediaCachingEnabled)
                    MediaCacheService.QueueDownload(msg.Id, url);
            }
        }
    }

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        await CleanupInfiniteScrollAsync();
        await CleanupScrollDismissAsync();
        _visibilityRef?.Dispose();
    }

    private record ClientLocaleInfo(string? TimeZone, string? Locale);
}
