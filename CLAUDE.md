# Yap - Blazor Server Chat Application

## Claude Instructions
- **Do NOT run `dotnet build` or `dotnet run`** - always ask the user to build/run and report results
- The dev environment uses .NET 10 which may not be available in the CLI environment

## Overview
A real-time chat application built with Blazor Server (.NET 10), featuring instant messaging, image sharing, and resilient reconnection with persistent state.

## Architecture

### Single Project Structure
```
Yap/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor           # Base layout
│   │   ├── ChatLayout.razor           # Chat-specific layout (header, sidebar, body)
│   │   ├── ChatLayout.razor.css
│   │   ├── ReconnectModal.razor       # Discord-style reconnection banner
│   │   ├── ReconnectModal.razor.js    # Auto-resume, infinite retry logic
│   │   └── ReconnectModal.razor.css   # Banner styling
│   ├── Pages/
│   │   ├── Home.razor                 # Login/username entry
│   │   ├── RoomChat.razor             # Room chat page (/lobby, /room/{id})
│   │   ├── DmChat.razor               # DM chat page (/dm/{username})
│   │   ├── ChatBase.cs                # Shared base class for chat pages
│   │   ├── Error.razor
│   │   └── NotFound.razor
│   ├── ChatHeader.razor               # Header with status dropdown, mailbox, user count
│   ├── ChatSidebar.razor              # Rooms list, users list with status dots
│   ├── MessageInput.razor             # Message input with typing, file upload
│   ├── MessageItem.razor              # Individual message display
│   ├── App.razor                      # Root component with Blazor.start() config
│   ├── Routes.razor
│   └── _Imports.razor
├── Configuration/
│   └── PersistenceSettings.cs         # Database persistence configuration
├── Data/
│   ├── ChatDbContext.cs               # EF Core DbContext
│   ├── ChatDbContextFactory.cs        # Design-time factory for migrations
│   └── Migrations/                    # EF Core migrations
├── Extensions/
│   └── PersistenceServiceExtensions.cs # DI registration for persistence
├── Services/
│   ├── ChatService.cs                 # Core real-time functionality (singleton)
│   ├── ChatPersistenceService.cs      # Write-through database persistence
│   ├── ChatConfigService.cs           # UI text configuration
│   ├── ChatNavigationState.cs         # Navigation state with [PersistentState]
│   ├── UserStateService.cs            # User identity with [PersistentState]
│   ├── ChatCircuitHandler.cs          # Circuit lifecycle handling
│   ├── PushSubscriptionStore.cs       # Push notification subscriptions
│   ├── PushNotificationService.cs     # Web push notifications
│   └── EmojiService.cs                # Twemoji rendering
├── Models/
│   ├── ChatMessage.cs                 # Message model (EF entity)
│   ├── Channel.cs                     # Unified room/DM channel model (EF entity)
│   ├── Reaction.cs                    # Message reaction model (EF entity)
│   ├── PushSubscription.cs            # Push subscription model (EF entity)
│   └── UserStatus.cs                  # User presence status enum
├── wwwroot/
│   ├── js/chat.js                     # Tab notifications, badge API helpers
│   ├── uploads/                       # Image storage
│   ├── app.css                        # Base styles
│   ├── notif.mp3                      # Notification sound
│   ├── manifest.webmanifest           # PWA manifest
│   ├── service-worker.js              # PWA service worker
│   └── icon.svg                       # App icon (SVG)
├── Data/                              # SQLite database location (when enabled)
│   └── yap.db
├── appsettings.json                   # Chat config + persistence settings
├── Program.cs                         # Service registration, circuit config
└── Yap.csproj
```

## How It Works

### Real-time Communication
Blazor Server uses a persistent SignalR connection (circuit) for all UI updates. We leverage this existing connection for chat functionality:

1. **ChatService** (singleton) - Holds all chat state and raises events
2. Components subscribe to ChatService events
3. When a message is sent, ChatService notifies all subscribers
4. Each component calls `StateHasChanged()` to update its UI

No custom SignalR hub needed - Blazor's built-in circuit handles everything.

### Component Architecture
- **ChatLayout** - Real Blazor layout with header, sidebar, and `@Body`
- **ChatHeader** - Self-sufficient, injects services directly
- **ChatSidebar** - Self-sufficient, handles navigation internally
- **RoomChat/DmChat** - Thin pages focused on message display
- **ChatBase** - Shared base class for DI, auth guard, helpers

### Key Services

**ChatService.cs** (Singleton)
- Manages online users, messages, channels (rooms/DMs), typing indicators, reactions
- Tracks user status (Online, Away, Invisible)
- First user to join becomes admin (can create/delete rooms)
- Uses `ConcurrentDictionary` for thread-safe state
- Configurable max messages per channel via `appsettings.json`
- Integrates with `ChatPersistenceService` for database persistence
- Exposes events: `OnMessageReceived`, `OnMessageUpdated`, `OnMessageDeleted`, `OnReactionChanged`, `OnUserChanged`, `OnUsersListChanged`, `OnUserStatusChanged`, `OnTypingUsersChanged`, `OnAdminChanged`, `OnChannelCreated`, `OnChannelDeleted`

**ChatPersistenceService.cs** (Singleton)
- Write-through persistence to database (when enabled)
- All methods are no-ops when persistence is disabled
- Handles channels, messages, reactions, and push subscriptions
- Loads snapshot on startup via `LoadSnapshotAsync()`

**UserStateService.cs** (Scoped + Persistent)
- Holds current user's identity (Username, SessionId, Status)
- Properties marked with `[PersistentState]` survive circuit eviction

**ChatNavigationState.cs** (Scoped + Persistent)
- Tracks current room/DM context
- Properties marked with `[PersistentState]` for session restoration

## .NET 10 Circuit & Reconnection Features

### Circuit State Persistence
When a user disconnects (closes laptop, loses network), the circuit is kept alive for a configurable period. If evicted, properties marked with `[PersistentState]` are serialized and can be restored via `Blazor.resumeCircuit()`.

**Configuration in Program.cs:**
```csharp
.AddInteractiveServerComponents(options =>
{
    // Keep circuit alive for 4 hours (default: 3 minutes)
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(4);
    options.DisconnectedCircuitMaxRetained = 1000;
})
.RegisterPersistentService<UserStateService>(RenderMode.InteractiveServer)
.RegisterPersistentService<ChatNavigationState>(RenderMode.InteractiveServer);

// Keep persisted state for 48 hours after circuit eviction
builder.Services.Configure<CircuitOptions>(options =>
{
    options.PersistedCircuitInMemoryRetentionPeriod = TimeSpan.FromHours(48);
    options.PersistedCircuitInMemoryMaxRetained = 5000;
});
```

### Reconnection Banner
Custom Discord-style top banner (not blocking modal):
- Appears immediately when connection lost
- Infinite retries every 4 seconds
- Auto-resumes with persisted state when circuit evicted
- Animated loading dots during reconnection

**Key files:**
- `ReconnectModal.razor` - Banner HTML structure
- `ReconnectModal.razor.js` - Event handling, auto-resume logic
- `ReconnectModal.razor.css` - Banner styling
- `App.razor` - `Blazor.start()` with custom retry config

## Configuration

All settings in `appsettings.json`:

```json
{
  "ChatSettings": {
    "ProjectName": "Yap",
    "RoomName": "lobby",
    "ClearUploadsOnStart": true,
    "MaxMessagesPerChannel": 100,
    "Persistence": {
      "Enabled": true,
      "Provider": "SQLite",
      "ConnectionStrings": {
        "SQLite": "Data Source=Data/yap.db",
        "Postgres": "Host=localhost;Database=yap;Username=yap;Password=yap"
      }
    },
    "FunnyTexts": {
      "WelcomeMessages": [...],
      "JoinButtonTexts": [...],
      "SystemMessages": { "UserJoined": [...], "UserLeft": [...] },
      "TypingIndicators": { "Single": [...], "Double": [...], "Multiple": [...] }
    }
  }
}
```

## Running the Application

### Development
```bash
cd Yap
dotnet run
```

Access at `https://localhost:5001` (or the port shown in console).

### Docker
```bash
docker build -t yap .
docker run -p 8080:8080 -v ./uploads:/app/wwwroot/uploads yap
```

## Features

- **Real-time messaging** - Instant delivery via Blazor circuit
- **Multiple rooms** - Create and switch between chat rooms (admin only)
- **Admin system** - First user becomes admin, can manage rooms (🛡️ badge)
- **Direct messages** - Private conversations (persist permanently when DB enabled)
- **User status** - Online (green), Away (orange), Invisible (gray) with dropdown selector
- **Sign out** - Explicit sign out clears session and returns to login
- **Mailbox indicator** - Unread DM count in header, visible even with sidebar closed
- **Message actions** - Discord-style hover popup with reactions, edit, delete
- **Reactions** - ❤️ 😂 🥹 reactions on any message, shown as pills with counts
- **Edit/Delete** - Edit or delete your own messages (shows "edited" indicator)
- **Image sharing** - Direct file upload, up to 100MB, drag & drop support
- **Multiline input** - Discord-style auto-expanding textarea (Shift+Enter for newlines)
- **Emoji support** - Twemoji rendering
- **Tab notifications** - Unread count in title + audio
- **Online users** - Live user list with status dots, sorted by recent DM activity
- **Chat history** - Configurable max messages per channel (default 100)
- **Typing indicators** - See who's typing
- **Mobile responsive** - Collapsible sidebar
- **Resilient reconnection** - Auto-reconnect with persistent state restoration
- **Dark theme** - Discord-inspired UI
- **Auto-cleanup** - Configurable upload clearing on app start
- **PWA support** - Installable as app, badge notifications for unread DMs
- **Database persistence** - Optional SQLite/Postgres storage for messages, channels, reactions

## Database Persistence

Optional persistence layer using EF Core. When enabled, all chat data survives app restarts.

### Architecture
- **Write-through cache**: Fast in-memory reads, persist on every mutation
- **Load on startup**: Database snapshot loaded into memory when app starts
- **Graceful fallback**: When disabled, everything works in-memory only

### What's Persisted
- **Channels** (rooms and DMs)
- **Messages** (with trimming to `MaxMessagesPerChannel`)
- **Reactions** (stored in separate table, grouped by emoji for display)
- **Push subscriptions** (moved from JSON file to database)

### Database Schema
```
Channel                    ChatMessage                 Reaction
+------------------+       +------------------+        +------------------+
| Id (PK, Guid)    |<──────| Id (PK, Guid)    |<──────| Id (PK, int)     |
| Type (int)       |       | ChannelId (FK)   |       | MessageId (FK)   |
| Name             |       | Username         |       | Emoji            |
| CreatedAt        |       | Content          |       | Username         |
| CreatedBy        |       | Timestamp        |       +------------------+
| IsDefault        |       | IsEdited         |
| Participant1     |       | ImageUrls (JSON) |       PushSubscription
| Participant2     |       +------------------+       +------------------+
+------------------+                                  | Endpoint (PK)    |
                                                      | Username         |
                                                      | P256dh           |
                                                      | Auth             |
                                                      | CreatedAt        |
                                                      +------------------+
```

### Key Design Decisions
- **DMs persist permanently** (like Discord) - users see chat history when they return
- **Models = Tables** - No separate entity classes, models are EF-friendly
- **DbContextFactory** - Used by singleton `ChatService` to create short-lived DbContext instances
- **Pooled factory** - `AddPooledDbContextFactory` for singleton compatibility and performance

### Migrations
```powershell
# Package Manager Console
Add-Migration InitialCreate -Context ChatDbContext -OutputDir Data/Migrations
Update-Database -Context ChatDbContext
```

If no migrations exist, the app uses `EnsureCreatedAsync()` to create tables directly from the model.

## Technical Details

### .NET 10 Features Used
- `[PersistentState]` attribute for circuit state persistence
- `RegisterPersistentService<T>()` for scoped service persistence
- `Blazor.resumeCircuit()` for session restoration
- Custom `Blazor.start()` configuration for retry timing
- `ReconnectModal` component (customized as top banner)
- `ResourcePreloader` for optimized asset loading
- `MapStaticAssets()` for fingerprinted static files

### EF Core Packages
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0-*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0-*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0-*" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0-*" />
```

### File Upload
Images are uploaded directly in the component using `InputFile`:
```csharp
await file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024).CopyToAsync(stream);
```

No HTTP multipart, no API endpoint - just direct file I/O.

### Tab Notifications
Minimal JavaScript in `wwwroot/js/chat.js`:
- `setupVisibilityListener` - Detects when tab becomes visible
- `isPageVisible` - Checks current visibility state
- `playNotificationSound` - Plays notification audio
- `scrollToBottom` - Auto-scrolls message list

### PWA (Progressive Web App)
The app is installable on desktop and mobile:
- `manifest.webmanifest` - App metadata (name, icons, theme color)
- `service-worker.js` - Minimal SW for installability (no offline caching)
- `icon.svg` - Vector app icon (PNG versions needed for full iOS support)

**Badge API** for unread DM notifications:
- `setAppBadge(count)` / `clearAppBadge()` in chat.js
- Called from ChatHeader when unread count changes
- Support: Chrome/Edge on Windows/macOS, Safari on iOS 16.4+
- Badge only appears when app is installed as PWA

## Previous Architecture (Migrated From)

The app was migrated from a 4-project Blazor WebAssembly + SignalR architecture:
- BlazorChat.Server (SignalR hub + API)
- BlazorChat.Client (WebAssembly)
- BlazorChat.Client.Serve (WASM host)
- BlazorChat.AppHost (Aspire)

See `MIGRATION_TO_BLAZOR_SERVER.md` for migration details.
