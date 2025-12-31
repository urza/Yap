using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Yap.Models;
using Yap.Services;

namespace Yap.Components.Pages;

public abstract class ChatBase : ComponentBase, IAsyncDisposable
{
    // Injected services
    [Inject] protected ChatService ChatService { get; set; } = default!;
    [Inject] protected ChatConfigService ChatConfig { get; set; } = default!;
    [Inject] protected UserStateService UserState { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected IWebHostEnvironment Environment { get; set; } = default!;

    // Common state
    protected string CircuitId => UserState.CircuitId ?? "";
    protected string Username => UserState.Username ?? "";
    protected string messageInput = "";
    protected List<string> onlineUsers = new();
    protected bool sidebarOpen = false;
    protected System.Timers.Timer? typingTimer;
    protected bool isTyping = false;

    // Room state (needed by sidebar)
    protected List<Room> rooms = new();
    protected Guid currentRoomId;
    protected bool isAdmin = false;

    // DM unread counts (needed by sidebar and header)
    protected Dictionary<string, int> dmUnreadCounts = new();
    protected int totalUnreadDMs = 0;

    // Image modal state
    protected bool showImageModal = false;
    protected List<string> modalGallery = new();
    protected int modalImageIndex = 0;

    // Tab notifications
    protected int unreadCount = 0;
    protected string pageTitle = "Yap";
    protected string currentContext = "";

    // UI Text
    protected string messagePlaceholder = "";
    protected string onlineUsersHeader = "";
    protected string typingIndicatorText = "";

    // Abstract methods - must be implemented by derived classes
    protected abstract string GetHeaderTitle();
    protected abstract string GetMessagePlaceholder();
    protected abstract Task SendMessageAsync();
    protected abstract Task SendImagesAsync(List<string> imageUrls);
    protected abstract void UpdateTypingIndicator();
    protected abstract Task StartTypingAsync();
    protected abstract Task StopTypingAsync();
    protected abstract void SubscribeToEvents();
    protected abstract void UnsubscribeFromEvents();
    protected abstract Task OnInitializedChatAsync();

    protected override void OnInitialized()
    {
        messagePlaceholder = ChatConfig.GetRandomMessagePlaceholder();
        UpdateOnlineUsersHeader();
        pageTitle = ChatConfig.ProjectName;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            if (!UserState.IsLoggedIn)
            {
                Navigation.NavigateTo("/");
                return;
            }

            await InitializeChatAsync();
        }
    }

    protected virtual async Task InitializeChatAsync()
    {
        // Subscribe to common events
        ChatService.OnUserChanged += HandleUserChanged;
        ChatService.OnUsersListChanged += HandleUsersListChanged;
        ChatService.OnRoomCreated += HandleRoomCreated;
        ChatService.OnRoomDeleted += HandleRoomDeleted;
        ChatService.OnAdminChanged += HandleAdminChanged;
        ChatService.OnDirectMessageReceived += HandleDirectMessageReceivedForUnread;

        // Subscribe to specific events
        SubscribeToEvents();

        // Load rooms
        rooms = ChatService.GetRooms();
        currentRoomId = ChatService.LobbyId;

        // Load online users
        onlineUsers = ChatService.GetOnlineUsers();
        UpdateOnlineUsersHeader();

        // Join chat (only if not already joined)
        if (!UserState.IsJoinedChat)
        {
            UserState.CircuitId = Guid.NewGuid().ToString();
            await ChatService.AddUserAsync(CircuitId, Username);

            // Refresh users after join
            onlineUsers = ChatService.GetOnlineUsers();
            UpdateOnlineUsersHeader();
        }

        // Check if we're admin
        isAdmin = ChatService.IsAdmin(Username);

        // Setup tab notifications
        await SetupTabNotifications();

        // Let derived class do its initialization
        await OnInitializedChatAsync();

        await InvokeAsync(StateHasChanged);
        await ScrollToBottomAsync();
    }

    #region Common Event Handlers

    private async Task HandleUserChanged(string user, bool isJoining)
    {
        await InvokeAsync(() =>
        {
            OnUserChanged(user, isJoining);
            return Task.CompletedTask;
        });
    }

    protected virtual void OnUserChanged(string user, bool isJoining) { }

    private async Task HandleUsersListChanged()
    {
        await InvokeAsync(async () =>
        {
            onlineUsers = ChatService.GetOnlineUsers();
            UpdateOnlineUsersHeader();
            await OnUsersListChangedAsync();
            StateHasChanged();
        });
    }

    protected virtual Task OnUsersListChangedAsync() => Task.CompletedTask;

    private async Task HandleRoomCreated(Room room)
    {
        await InvokeAsync(() =>
        {
            rooms = ChatService.GetRooms();
            StateHasChanged();
            return Task.CompletedTask;
        });
    }

    private async Task HandleRoomDeleted(Guid roomId)
    {
        await InvokeAsync(() =>
        {
            rooms = ChatService.GetRooms();
            OnRoomDeleted(roomId);
            StateHasChanged();
            return Task.CompletedTask;
        });
    }

    protected virtual void OnRoomDeleted(Guid roomId) { }

    private async Task HandleAdminChanged(string? adminUser)
    {
        await InvokeAsync(() =>
        {
            isAdmin = ChatService.IsAdmin(Username);
            StateHasChanged();
            return Task.CompletedTask;
        });
    }

    // Track DM unread counts for all pages (header mailbox needs it)
    private async Task HandleDirectMessageReceivedForUnread(DirectMessage message)
    {
        await InvokeAsync(() =>
        {
            if (!message.ToUser.Equals(Username, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            var fromKey = message.FromUser.ToLowerInvariant();

            // Only increment if not currently viewing this DM
            if (!IsViewingDmWith(message.FromUser))
            {
                if (!dmUnreadCounts.ContainsKey(fromKey))
                    dmUnreadCounts[fromKey] = 0;
                dmUnreadCounts[fromKey]++;
                UpdateTotalUnreadDMs();
                StateHasChanged();
            }

            return Task.CompletedTask;
        });
    }

    protected virtual bool IsViewingDmWith(string user) => false;

    #endregion

    #region Room Management

    protected async Task SwitchToRoomAsync(Guid roomId)
    {
        // Navigate to room page
        var url = roomId == ChatService.LobbyId ? "/lobby" : $"/room/{roomId}";
        Navigation.NavigateTo(url);
    }

    protected async Task CreateRoomAsync(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;
        await ChatService.CreateRoomAsync(Username, roomName);
    }

    protected async Task DeleteRoomAsync(Guid roomId)
    {
        await ChatService.DeleteRoomAsync(Username, roomId);
    }

    #endregion

    #region DM Management

    protected void OpenDM(string targetUser)
    {
        if (targetUser.Equals(Username, StringComparison.OrdinalIgnoreCase)) return;
        Navigation.NavigateTo($"/dm/{Uri.EscapeDataString(targetUser)}");
    }

    protected int GetUnreadDMCount(string fromUser)
    {
        var key = fromUser.ToLowerInvariant();
        return dmUnreadCounts.TryGetValue(key, out var count) ? count : 0;
    }

    protected void UpdateTotalUnreadDMs()
    {
        totalUnreadDMs = dmUnreadCounts.Values.Sum();
    }

    protected void MarkDMAsRead(string otherUser)
    {
        ChatService.MarkDMsAsRead(Username, otherUser);
        dmUnreadCounts[otherUser.ToLowerInvariant()] = 0;
        UpdateTotalUnreadDMs();
    }

    protected void OpenSidebarForDMs()
    {
        sidebarOpen = true;
    }

    protected IEnumerable<string> GetSortedUsers()
    {
        var currentUserList = onlineUsers.Where(u => u.Equals(Username, StringComparison.OrdinalIgnoreCase));
        var otherUsers = onlineUsers.Where(u => !u.Equals(Username, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = otherUsers
            .Select(u => new
            {
                User = u,
                UnreadCount = GetUnreadDMCount(u),
                LastDM = ChatService.GetLastDMTimestamp(Username, u)
            })
            .OrderByDescending(x => x.UnreadCount > 0)
            .ThenByDescending(x => x.LastDM ?? DateTime.MinValue)
            .ThenBy(x => x.User)
            .Select(x => x.User);

        return currentUserList.Concat(sorted);
    }

    #endregion

    #region Send Messages

    protected async Task HandleSendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(messageInput)) return;

        await SendMessageAsync();
        messageInput = "";
        await StopTypingAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task HandleFileSelectedAsync(InputFileChangeEventArgs e)
    {
        var files = e.GetMultipleFiles(10);
        if (files.Count == 0) return;

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var imageUrls = new List<string>();

        try
        {
            var uploadsFolder = Path.Combine(Environment.WebRootPath, "uploads");
            Directory.CreateDirectory(uploadsFolder);

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file.Name).ToLowerInvariant();
                if (!allowedExtensions.Contains(extension)) continue;
                if (file.Size > 100 * 1024 * 1024) continue;

                var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                await using var stream = new FileStream(filePath, FileMode.Create);
                await file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024).CopyToAsync(stream);

                imageUrls.Add($"/uploads/{uniqueFileName}");
            }

            if (imageUrls.Count > 0)
            {
                await SendImagesAsync(imageUrls);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error uploading files: {ex.Message}");
        }
    }

    #endregion

    #region Typing Indicators

    protected async Task HandleInputChangedAsync()
    {
        await HandleTypingChangeAsync(messageInput);
    }

    private async Task HandleTypingChangeAsync(string currentValue)
    {
        if (!string.IsNullOrWhiteSpace(currentValue) && !isTyping)
        {
            await StartTypingAsync();
        }
        else if (string.IsNullOrWhiteSpace(currentValue) && isTyping)
        {
            await StopTypingAsync();
        }

        typingTimer?.Stop();
        typingTimer?.Dispose();

        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            typingTimer = new System.Timers.Timer(3000);
            typingTimer.Elapsed += async (s, e) =>
            {
                await StopTypingAsync();
                typingTimer?.Dispose();
            };
            typingTimer.Start();
        }
    }

    protected void SetTyping(bool typing)
    {
        isTyping = typing;
    }

    protected void StopTypingTimer()
    {
        typingTimer?.Stop();
        typingTimer?.Dispose();
    }

    #endregion

    #region Notifications

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

        pageTitle = title;
        try { await JS.InvokeVoidAsync("setDocumentTitle", title); } catch { }
    }

    #endregion

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

    protected void ToggleSidebar() => sidebarOpen = !sidebarOpen;

    protected void UpdateOnlineUsersHeader() =>
        onlineUsersHeader = ChatConfig.GetRandomOnlineUsersHeader(onlineUsers.Count);

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        typingTimer?.Stop();
        typingTimer?.Dispose();

        if (!string.IsNullOrEmpty(Username))
        {
            // Unsubscribe from common events
            ChatService.OnUserChanged -= HandleUserChanged;
            ChatService.OnUsersListChanged -= HandleUsersListChanged;
            ChatService.OnRoomCreated -= HandleRoomCreated;
            ChatService.OnRoomDeleted -= HandleRoomDeleted;
            ChatService.OnAdminChanged -= HandleAdminChanged;
            ChatService.OnDirectMessageReceived -= HandleDirectMessageReceivedForUnread;

            // Unsubscribe from specific events
            UnsubscribeFromEvents();

            // Stop typing if needed
            if (isTyping)
            {
                await StopTypingAsync();
            }

            // Note: We don't remove the user here because this DisposeAsync is called
            // when navigating between pages (e.g., RoomChat -> DmChat). The user should
            // only be removed when the circuit actually disconnects, which is handled
            // by the CircuitHandler in Program.cs.
        }
    }
}
