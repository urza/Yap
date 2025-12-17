# Migration from Blazor WebAssembly to Blazor Server (.NET 10)

## Overview

This document outlines the migration of BlazorChat from a multi-project Blazor WebAssembly + SignalR architecture to a single-project Blazor Server application running on .NET 10.

### Current Architecture (4 Projects)
```
BlazorChat.AppHost/        - Aspire orchestration
BlazorChat.Server/         - API + SignalR Hub (port 5224)
BlazorChat.Client/         - WebAssembly app
BlazorChat.Client.Serve/   - Host server for WASM (port 5221)
```

### Target Architecture (1 Project)
```
Yap/                       - Single Blazor Server app (.NET 10)
```

---

## .NET 10 Features We'll Leverage

| Feature | Benefit for Yap |
|---------|-----------------|
| **ReconnectModal component** | Built-in reconnection UI with better UX, already in template |
| **Circuit state persistence** | Sessions survive network interruptions |
| **[PersistentState] attribute** | Simpler state management during prerendering |
| **ResourcePreloader** | Optimized asset loading |
| **BlazorDisableThrowNavigationException** | Cleaner navigation handling |
| **NotFoundPage parameter** | Better 404 handling (already configured) |
| **Enhanced form validation** | Nested object validation without reflection |
| **JS interop improvements** | Constructor support, property access |

---

## Migration Plan

### Phase 1: Project Setup & Configuration

#### Step 1.1: Verify Yap Project Structure
The new Yap project already exists with .NET 10 template. Current structure:
```
Yap/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor
│   │   ├── ReconnectModal.razor      # .NET 10 built-in
│   │   └── ReconnectModal.razor.js
│   ├── Pages/
│   │   ├── Home.razor
│   │   ├── Error.razor
│   │   └── NotFound.razor
│   ├── App.razor
│   ├── Routes.razor
│   └── _Imports.razor
├── wwwroot/
├── Program.cs
└── Yap.csproj
```

#### Step 1.2: Create Target Structure
```
Yap/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor          # Migrate from Client
│   │   ├── MainLayout.razor.css
│   │   ├── ReconnectModal.razor      # Keep .NET 10 default
│   │   └── ReconnectModal.razor.js
│   ├── Pages/
│   │   ├── Chat.razor                # Main chat page
│   │   ├── Chat.razor.css            # Discord-style theme
│   │   ├── Error.razor
│   │   └── NotFound.razor
│   ├── App.razor
│   ├── Routes.razor
│   └── _Imports.razor
├── Services/
│   ├── ChatService.cs                # Core chat functionality
│   ├── ChatConfigService.cs          # UI text configuration
│   └── EmojiService.cs               # Emoji rendering
├── Models/
│   └── ChatMessage.cs                # Shared model
├── wwwroot/
│   ├── css/
│   │   └── app.css                   # Global styles
│   ├── uploads/                      # Image storage
│   ├── js/
│   │   └── chat.js                   # Minimal JS helpers
│   └── notif.mp3                     # Notification sound
├── appsettings.json                  # Chat config + funny texts
├── Program.cs
└── Yap.csproj
```

#### Step 1.3: Update Program.cs
```csharp
using Yap.Components;
using Yap.Services;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor Server services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Chat services (singleton for shared state)
builder.Services.AddSingleton<ChatService>();
builder.Services.AddScoped<ChatConfigService>();
builder.Services.AddScoped<EmojiService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

---

### Phase 2: Core Services Migration

#### Step 2.1: Create ChatMessage Model
**File: `Models/ChatMessage.cs`**
```csharp
namespace Yap.Models;

public record ChatMessage(
    string Username,
    string Content,
    DateTime Timestamp,
    bool IsImage = false
);
```

#### Step 2.2: Create ChatService (Replaces SignalR Hub)
**File: `Services/ChatService.cs`**

This is the heart of the migration - replacing SignalR with Blazor Server's built-in circuit.

```csharp
using System.Collections.Concurrent;
using Yap.Models;

namespace Yap.Services;

public class ChatService
{
    private readonly ConcurrentDictionary<string, UserSession> _users = new();
    private readonly ConcurrentQueue<ChatMessage> _messages = new();
    private readonly ConcurrentDictionary<string, DateTime> _typingUsers = new();
    private readonly int _maxMessages = 100;

    // Events for real-time updates
    public event Func<ChatMessage, Task>? OnMessageReceived;
    public event Func<string, bool, Task>? OnUserChanged;  // username, isJoining
    public event Func<Task>? OnUsersListChanged;
    public event Func<Task>? OnTypingUsersChanged;

    public record UserSession(string Username, string CircuitId);

    // User management
    public async Task AddUserAsync(string circuitId, string username)
    {
        _users[circuitId] = new UserSession(username, circuitId);
        if (OnUserChanged != null)
            await OnUserChanged.Invoke(username, true);
        if (OnUsersListChanged != null)
            await OnUsersListChanged.Invoke();
    }

    public async Task RemoveUserAsync(string circuitId)
    {
        if (_users.TryRemove(circuitId, out var session))
        {
            _typingUsers.TryRemove(session.Username, out _);
            if (OnUserChanged != null)
                await OnUserChanged.Invoke(session.Username, false);
            if (OnUsersListChanged != null)
                await OnUsersListChanged.Invoke();
            if (OnTypingUsersChanged != null)
                await OnTypingUsersChanged.Invoke();
        }
    }

    public List<string> GetOnlineUsers() =>
        _users.Values.Select(u => u.Username).Distinct().ToList();

    // Messaging
    public async Task SendMessageAsync(string username, string content, bool isImage = false)
    {
        var message = new ChatMessage(username, content, DateTime.UtcNow, isImage);
        _messages.Enqueue(message);

        while (_messages.Count > _maxMessages)
            _messages.TryDequeue(out _);

        if (OnMessageReceived != null)
            await OnMessageReceived.Invoke(message);
    }

    public List<ChatMessage> GetRecentMessages(int count = 50) =>
        _messages.TakeLast(Math.Min(count, _messages.Count)).ToList();

    // Typing indicators
    public async Task StartTypingAsync(string username)
    {
        _typingUsers[username] = DateTime.UtcNow;
        if (OnTypingUsersChanged != null)
            await OnTypingUsersChanged.Invoke();
    }

    public async Task StopTypingAsync(string username)
    {
        _typingUsers.TryRemove(username, out _);
        if (OnTypingUsersChanged != null)
            await OnTypingUsersChanged.Invoke();
    }

    public List<string> GetTypingUsers()
    {
        // Clean up stale typing indicators (> 3 seconds)
        var stale = _typingUsers
            .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > 3)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var user in stale)
            _typingUsers.TryRemove(user, out _);

        return _typingUsers.Keys.ToList();
    }
}
```

#### Step 2.3: Migrate ChatConfigService
**File: `Services/ChatConfigService.cs`**
Copy from `BlazorChat.Client/Services/ChatConfigService.cs` - no changes needed.

#### Step 2.4: Migrate EmojiService
**File: `Services/EmojiService.cs`**
Copy from `BlazorChat.Client/Services/EmojiService.cs` - no changes needed.

---

### Phase 3: Component Migration

#### Step 3.1: Update _Imports.razor
**File: `Components/_Imports.razor`**
```razor
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using Yap.Components
@using Yap.Components.Layout
@using Yap.Services
@using Yap.Models
```

#### Step 3.2: Create Chat.razor Page
**File: `Components/Pages/Chat.razor`**

Key changes from WebAssembly version:
- No SignalR HubConnection - use ChatService events directly
- No configuration discovery (`/api/config`) - direct service injection
- Simplified connection status - Blazor circuit handles it
- Direct file upload without HTTP multipart

```razor
@page "/"
@inject ChatService ChatService
@inject ChatConfigService ChatConfig
@inject EmojiService EmojiService
@inject IJSRuntime JS
@inject IWebHostEnvironment Environment
@implements IAsyncDisposable

<PageTitle>@pageTitle</PageTitle>

@if (string.IsNullOrEmpty(username))
{
    <div class="username-container">
        <h2>@welcomeMessage</h2>
        <div class="username-form">
            <input type="text" @bind="usernameInput" @bind:event="oninput"
                   placeholder="@usernamePlaceholder" class="username-input"
                   @onkeypress="HandleUsernameKeyPress" />
            <button class="join-button" @onclick="JoinChat"
                    disabled="@(string.IsNullOrWhiteSpace(usernameInput))">
                @joinButtonText
            </button>
        </div>
    </div>
}
else
{
    <div class="chat-container">
        <div class="chat-header">
            <h3>@roomHeader</h3>
            <div class="header-controls">
                <span class="connection-status connected">@connectionStatus</span>
                <button class="users-toggle" @onclick="ToggleSidebar" title="Toggle users list">
                    <svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor">
                        <path d="M14 8.00598C14 10.211 12.206 12.006 10 12.006C7.795 12.006 6 10.211 6 8.00598C6 5.80098 7.795 4.00598 10 4.00598C12.206 4.00598 14 5.80098 14 8.00598ZM2 19.006C2 15.473 5.29 13.006 10 13.006C14.711 13.006 18 15.473 18 19.006V20.006H2V19.006Z"/>
                        <path d="M20.0001 20.006H22.0001V19.006C22.0001 16.4433 20.2697 14.4415 17.5213 13.5352C19.0621 14.9127 20.0001 16.8059 20.0001 19.006V20.006Z"/>
                        <path d="M14.8834 11.9077C16.6657 11.5044 18.0001 9.9077 18.0001 8.00598C18.0001 5.96916 16.4693 4.28218 14.4971 4.0367C15.4322 5.09511 16.0001 6.48524 16.0001 8.00598C16.0001 9.44888 15.4889 10.7742 14.6378 11.8102C14.7203 11.8734 14.8022 11.9388 14.8834 11.9077Z"/>
                    </svg>
                    <span class="online-count">@onlineUsers.Count</span>
                </button>
            </div>
        </div>

        <div class="chat-main">
            <div class="messages-container">
                <div class="messages" @ref="messagesElement">
                    @for (int i = 0; i < messages.Count; i++)
                    {
                        var message = messages[i];
                        var isSystemMessage = message.Username == "System";
                        var showHeader = !isSystemMessage && (i == 0 || messages[i - 1].Username != message.Username);

                        <div class="message-group @(isSystemMessage ? "system-message" : "")">
                            @if (showHeader)
                            {
                                <div class="message-header">
                                    <strong class="message-username">@message.Username</strong>
                                    <span class="message-time">@message.Timestamp.ToLocalTime().ToString("HH:mm")</span>
                                </div>
                            }
                            <div class="message-content">
                                @if (message.IsImage)
                                {
                                    <img src="@message.Content" alt="Uploaded image"
                                         class="message-image" @onclick="() => ShowFullImage(message.Content)" />
                                }
                                else
                                {
                                    @EmojiService.ConvertEmojisToTwemoji(message.Content)
                                }
                            </div>
                        </div>
                    }
                </div>

                <div class="message-input-container">
                    <input type="text" @bind="messageInput" @bind:event="oninput"
                           placeholder="@messagePlaceholder" class="message-input"
                           @onkeypress="HandleMessageKeyPress"
                           @oninput="HandleTypingChange" />
                    <label class="image-upload-button">
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none"
                             stroke="currentColor" stroke-width="2">
                            <rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect>
                            <circle cx="8.5" cy="8.5" r="1.5"></circle>
                            <polyline points="21 15 16 10 5 21"></polyline>
                        </svg>
                        <InputFile OnChange="HandleFileSelected" accept="image/*" style="display: none;" />
                    </label>
                </div>

                <div class="typing-indicator-container">
                    @if (!string.IsNullOrEmpty(typingIndicatorText))
                    {
                        <div class="typing-indicator">
                            <span style="display: flex; align-items: center; gap: 0.5rem; color: #72767d;">
                                <span class="typing-dots">
                                    <span class="dot"></span>
                                    <span class="dot"></span>
                                    <span class="dot"></span>
                                </span>
                                <span>@typingIndicatorText</span>
                            </span>
                        </div>
                    }
                </div>
            </div>

            <div class="users-sidebar @(sidebarOpen ? "sidebar-open" : "")">
                <h4>@onlineUsersHeader</h4>
                <div class="users-list">
                    @foreach (var user in onlineUsers)
                    {
                        <div class="user-item @(user == username ? "current-user" : "")">
                            @user
                        </div>
                    }
                </div>
            </div>
        </div>
        <div class="sidebar-backdrop @(sidebarOpen ? "show" : "")" @onclick="ToggleSidebar"></div>
    </div>
}

@if (showImageModal)
{
    <div class="image-modal" @onclick="CloseImageModal">
        <img src="@modalImageUrl" alt="Full size image" />
    </div>
}

@code {
    // Circuit ID for user identification
    [CascadingParameter]
    public HttpContext? HttpContext { get; set; }

    private string circuitId = Guid.NewGuid().ToString();

    // State
    private string username = "";
    private string usernameInput = "";
    private string messageInput = "";
    private List<ChatMessage> messages = new();
    private List<string> onlineUsers = new();
    private List<string> typingUsers = new();
    private ElementReference messagesElement;
    private bool showImageModal = false;
    private string modalImageUrl = "";
    private bool sidebarOpen = false;
    private System.Timers.Timer? typingTimer;
    private bool isTyping = false;

    // Tab notifications
    private int unreadCount = 0;
    private string pageTitle = "Yap";

    // UI Text
    private string welcomeMessage = "";
    private string joinButtonText = "";
    private string usernamePlaceholder = "";
    private string messagePlaceholder = "";
    private string connectionStatus = "";
    private string roomHeader = "";
    private string onlineUsersHeader = "";
    private string typingIndicatorText = "";

    protected override void OnInitialized()
    {
        welcomeMessage = ChatConfig.GetRandomWelcomeMessage();
        joinButtonText = ChatConfig.GetRandomJoinButtonText();
        usernamePlaceholder = ChatConfig.GetRandomUsernamePlaceholder();
        messagePlaceholder = ChatConfig.GetRandomMessagePlaceholder();
        roomHeader = ChatConfig.GetRandomRoomHeader();
        connectionStatus = ChatConfig.GetRandomConnectionStatus(true);
        UpdateOnlineUsersHeader();
        pageTitle = ChatConfig.ProjectName;
    }

    private async Task HandleUsernameKeyPress(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await JoinChat();
    }

    private async Task HandleMessageKeyPress(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SendMessage();
    }

    private async Task JoinChat()
    {
        if (string.IsNullOrWhiteSpace(usernameInput)) return;

        username = usernameInput.Trim();

        // Subscribe to events
        ChatService.OnMessageReceived += HandleMessageReceived;
        ChatService.OnUserChanged += HandleUserChanged;
        ChatService.OnUsersListChanged += HandleUsersListChanged;
        ChatService.OnTypingUsersChanged += HandleTypingUsersChanged;

        // Load history
        messages = ChatService.GetRecentMessages().ToList();
        onlineUsers = ChatService.GetOnlineUsers();
        UpdateOnlineUsersHeader();

        // Join chat
        await ChatService.AddUserAsync(circuitId, username);

        // Setup tab notifications
        await SetupTabNotifications();

        await InvokeAsync(StateHasChanged);
        await ScrollToBottom();
    }

    private async Task HandleMessageReceived(ChatMessage message)
    {
        await InvokeAsync(async () =>
        {
            messages.Add(message);
            StateHasChanged();
            await ScrollToBottom();
            await HandleNewMessageNotification(message.Username);
        });
    }

    private async Task HandleUserChanged(string user, bool isJoining)
    {
        await InvokeAsync(() =>
        {
            messages.Add(new ChatMessage(
                "System",
                ChatConfig.GetRandomSystemMessage(user, isJoining),
                DateTime.UtcNow
            ));
            StateHasChanged();
        });
    }

    private async Task HandleUsersListChanged()
    {
        await InvokeAsync(() =>
        {
            onlineUsers = ChatService.GetOnlineUsers();
            UpdateOnlineUsersHeader();
            StateHasChanged();
        });
    }

    private async Task HandleTypingUsersChanged()
    {
        await InvokeAsync(() =>
        {
            typingUsers = ChatService.GetTypingUsers();
            UpdateTypingIndicator();
            StateHasChanged();
        });
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(messageInput)) return;

        await ChatService.SendMessageAsync(username, messageInput);
        messageInput = "";
        await StopTyping();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file == null) return;

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();

        if (!allowedExtensions.Contains(extension))
        {
            // Could show error message
            return;
        }

        if (file.Size > 100 * 1024 * 1024) return;

        try
        {
            var uploadsFolder = Path.Combine(Environment.WebRootPath, "uploads");
            Directory.CreateDirectory(uploadsFolder);

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024).CopyToAsync(stream);

            var imageUrl = $"/uploads/{uniqueFileName}";
            await ChatService.SendMessageAsync(username, imageUrl, isImage: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error uploading file: {ex.Message}");
        }
    }

    private void HandleTypingChange(ChangeEventArgs e)
    {
        var value = e.Value?.ToString() ?? "";
        _ = HandleTypingChangeAsync(value);
    }

    private async Task HandleTypingChangeAsync(string currentValue)
    {
        if (!string.IsNullOrWhiteSpace(currentValue) && !isTyping)
        {
            await StartTyping();
        }
        else if (string.IsNullOrWhiteSpace(currentValue) && isTyping)
        {
            await StopTyping();
        }

        // Reset typing timer
        typingTimer?.Stop();
        typingTimer?.Dispose();

        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            typingTimer = new System.Timers.Timer(3000);
            typingTimer.Elapsed += async (s, e) =>
            {
                await StopTyping();
                typingTimer?.Dispose();
            };
            typingTimer.Start();
        }
    }

    private async Task StartTyping()
    {
        if (!isTyping)
        {
            isTyping = true;
            await ChatService.StartTypingAsync(username);
        }
    }

    private async Task StopTyping()
    {
        if (isTyping)
        {
            isTyping = false;
            await ChatService.StopTypingAsync(username);
            typingTimer?.Stop();
            typingTimer?.Dispose();
        }
    }

    // Tab notification methods
    private async Task SetupTabNotifications()
    {
        try
        {
            await JS.InvokeVoidAsync("setupVisibilityListener",
                DotNetObjectReference.Create(this));
        }
        catch { }
    }

    private async Task HandleNewMessageNotification(string messageUser)
    {
        if (messageUser != username && messageUser != "System")
        {
            try
            {
                var isVisible = await JS.InvokeAsync<bool>("isPageVisible");
                if (!isVisible)
                {
                    unreadCount++;
                    await UpdateTabTitle();
                    await JS.InvokeVoidAsync("playNotificationSound");
                }
            }
            catch { }
        }
    }

    [JSInvokable]
    public async Task OnPageBecameVisible()
    {
        if (unreadCount > 0)
        {
            unreadCount = 0;
            await UpdateTabTitle();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task UpdateTabTitle()
    {
        var title = unreadCount > 0
            ? $"({unreadCount}) {ChatConfig.ProjectName}"
            : ChatConfig.ProjectName;
        pageTitle = title;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ScrollToBottom()
    {
        try
        {
            await JS.InvokeVoidAsync("scrollToBottom", messagesElement);
        }
        catch { }
    }

    private void ShowFullImage(string imageUrl)
    {
        modalImageUrl = imageUrl;
        showImageModal = true;
    }

    private void CloseImageModal()
    {
        showImageModal = false;
        modalImageUrl = "";
    }

    private void ToggleSidebar() => sidebarOpen = !sidebarOpen;

    private void UpdateOnlineUsersHeader() =>
        onlineUsersHeader = ChatConfig.GetRandomOnlineUsersHeader(onlineUsers.Count);

    private void UpdateTypingIndicator() =>
        typingIndicatorText = ChatConfig.GetRandomTypingIndicator(typingUsers, username);

    public async ValueTask DisposeAsync()
    {
        typingTimer?.Stop();
        typingTimer?.Dispose();

        if (!string.IsNullOrEmpty(username))
        {
            ChatService.OnMessageReceived -= HandleMessageReceived;
            ChatService.OnUserChanged -= HandleUserChanged;
            ChatService.OnUsersListChanged -= HandleUsersListChanged;
            ChatService.OnTypingUsersChanged -= HandleTypingUsersChanged;

            if (isTyping)
            {
                await ChatService.StopTypingAsync(username);
            }
            await ChatService.RemoveUserAsync(circuitId);
        }
    }
}
```

#### Step 3.3: Copy Chat.razor.css
Copy the entire CSS file from `BlazorChat.Client/Pages/Chat.razor.css` to `Yap/Components/Pages/Chat.razor.css` with no changes needed.

---

### Phase 4: Static Assets & JavaScript

#### Step 4.1: Create Minimal JavaScript
**File: `wwwroot/js/chat.js`**

```javascript
// Tab notification helpers
let dotNetRef = null;

window.setupVisibilityListener = (ref) => {
    dotNetRef = ref;
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible' && dotNetRef) {
            dotNetRef.invokeMethodAsync('OnPageBecameVisible');
        }
    });
};

window.isPageVisible = () => document.visibilityState === 'visible';

window.playNotificationSound = () => {
    const audio = new Audio('/notif.mp3');
    audio.volume = 0.5;
    audio.play().catch(() => {});
};

window.scrollToBottom = (element) => {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
};
```

#### Step 4.2: Update App.razor
**File: `Components/App.razor`**

```razor
<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <ResourcePreloader />
    <link rel="stylesheet" href="@Assets["app.css"]" />
    <link rel="stylesheet" href="@Assets["Yap.styles.css"]" />
    <link rel="icon" type="image/x-icon" href="favicon.ico" />
    <ImportMap />
    <HeadOutlet />
</head>

<body>
    <Routes />
    <ReconnectModal />
    <script src="@Assets["_framework/blazor.web.js"]"></script>
    <script src="js/chat.js"></script>
</body>

</html>
```

#### Step 4.3: Copy Static Assets
- Copy `notif.mp3` from `BlazorChat.Client/wwwroot/` to `Yap/wwwroot/`
- Create `Yap/wwwroot/uploads/` directory for image storage

#### Step 4.4: Create Base Styles
**File: `wwwroot/app.css`**
```css
/* Base styles for Discord-style dark theme */
* {
    box-sizing: border-box;
}

html, body {
    margin: 0;
    padding: 0;
    font-family: Whitney, "Helvetica Neue", Helvetica, Arial, sans-serif;
    background: #36393f;
    color: #dcddde;
    height: 100%;
}
```

---

### Phase 5: Configuration Migration

#### Step 5.1: Create appsettings.json
**File: `appsettings.json`**

Copy the ChatSettings section from `BlazorChat.Client/wwwroot/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ChatSettings": {
    "ProjectName": "Yap",
    "RoomName": "lobby",
    "FunnyTexts": {
      // ... copy entire FunnyTexts section from existing config
    }
  }
}
```

---

### Phase 6: Cleanup & Testing

#### Step 6.1: Delete Old Projects
After successful migration and testing:
```bash
rm -rf BlazorChat.AppHost/
rm -rf BlazorChat.Server/
rm -rf BlazorChat.Client/
rm -rf BlazorChat.Client.Serve/
```

#### Step 6.2: Update Solution File
Update `BlazorChat.sln` to only include the Yap project.

#### Step 6.3: Update CLAUDE.md
Update documentation to reflect new single-project architecture.

---

## What Gets Removed (Simplification)

| Removed | Reason |
|---------|--------|
| SignalR Hub | Blazor Server circuit handles real-time |
| `/api/config` endpoint | No longer needed - same server |
| `/api/images/upload` endpoint | Direct file handling in component |
| `/api/chat/history` endpoint | Direct service access |
| CORS configuration | Same-origin requests |
| HttpClient service | No cross-server calls |
| Aspire orchestration | Single project |
| WebAssembly runtime | Server-rendered |
| Configuration discovery pattern | Direct service injection |

---

## Feature Comparison

| Feature | WebAssembly Version | Blazor Server Version |
|---------|---------------------|----------------------|
| Real-time messages | SignalR Hub | ChatService events |
| Image upload | HTTP POST to API | Direct InputFile + FileStream |
| Tab notifications | Complex JS interop | Simplified JS + server state |
| Typing indicators | SignalR broadcast | ChatService events |
| Online users | SignalR broadcast | ChatService state |
| Reconnection | Manual SignalR retry | .NET 10 ReconnectModal (built-in) |
| Connection status | HubConnection.State | Always connected (circuit) |
| Chat history | HTTP GET from API | Direct service access |

---

## Testing Checklist

- [ ] Username entry and join chat
- [ ] Send/receive text messages
- [ ] Send/receive images
- [ ] Typing indicators appear/disappear
- [ ] Online users list updates
- [ ] Tab title shows unread count
- [ ] Notification sound plays when tab not active
- [ ] User join/leave system messages
- [ ] Image modal opens on click
- [ ] Mobile responsive sidebar
- [ ] Reconnection modal appears on disconnect
- [ ] Chat history loads on join
- [ ] Emoji rendering with Twemoji

---

## Benefits After Migration

1. **Single project** - One codebase to maintain
2. **No SignalR configuration** - Blazor circuit handles everything
3. **No CORS issues** - Same-origin requests
4. **Simpler deployment** - One container/service
5. **Better debugging** - Full server-side debugging
6. **Built-in reconnection UI** - .NET 10 ReconnectModal
7. **Reduced complexity** - ~60% less code
8. **Faster development** - F5 to run everything
9. **Standard ASP.NET Core patterns** - Familiar to all .NET developers
