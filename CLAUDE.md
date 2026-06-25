# Yap - Blazor Server Chat Application

## Claude Instructions
- **Do NOT run `dotnet build` or `dotnet run`** - always ask the user to build/run and report results
- The dev environment uses .NET 10 which may not be available in the CLI environment
- **Project lives in a `Yap/` subfolder of the repo root.** The repo root is `/mnt/d/PROJECTS/Yap/` (has `.git`, `.sln`, this file); the .NET project is in `/mnt/d/PROJECTS/Yap/Yap/` (has `Yap.csproj`). All paths in the structure tree below are relative to that inner `Yap/`, so e.g. `Components/MessageInput.razor` → `/mnt/d/PROJECTS/Yap/Yap/Components/MessageInput.razor` (note the doubled `Yap/Yap/`).

## Coding Principles

### Core Values
- **Simplicity**: Prefer straightforward solutions over over-engineered ones. 
- **Maintainability**: Write code that's easy to change. Avoid tight coupling, keep components focused.
- **Understandability**: Think about the next developer (including future you) - will he understand this?
- **Elegance**: Strive for solutions that feel "right" - minimal moving parts, clear intent, no unnecessary complexity.

When in doubt, choose the simpler path. Refactor when patterns emerge, not in anticipation of hypothetical needs.

### Blazor-First Development
Embrace Blazor's declarative, binding-based model as the default approach:

- **Data binding**: Let UI reflect state automatically - modify properties, UI updates. Avoid manual DOM manipulation.
- **Component composition**: Break UI into reusable components with clear `[Parameter]` contracts.
- **Event callbacks**: Use `EventCallback<T>` for child-to-parent communication.
- **Cascading values**: Use for deeply-nested shared state (e.g., theme, user context).
- **State management**: Keep state in services (singleton for shared, scoped for per-user), let components subscribe to changes.
- **Read Blazor documentation**: Feel free to lookup Blazor Server best practices and Documentation.

### Blazor Server Latency Exceptions
Blazor Server's SignalR round-trip adds latency. Only escape to CSS/JS for **proven** performance issues:

- **Rapid hover effects**: Use CSS `:hover` when Blazor `@onmouseenter`/`@onmouseleave` causes visible lag (e.g., message action popups).
- **Key interception**: Avoid server-evaluated `@onkeydown:preventDefault` - use client-side JS for timing-critical input handling.
- **High-frequency events**: Scroll position, mouse movement - debounce or handle in JS, call back to Blazor sparingly.

When using JS interop, keep it minimal and focused. The goal is surgical fixes, not replacing Blazor's model.

### Mobile Considerations
- Use `@ontouchstart` for touch-specific behavior when needed
- CSS `:hover` becomes "sticky" after tap on mobile - may need JS class toggling for dismiss behaviors
- Test on actual devices - mobile browser behavior varies

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
│   │   └── ReconnectModal.razor       # Discord-style reconnection banner with auto-resume
│   ├── Pages/
│   │   ├── Login.razor                # Login/username entry
│   │   ├── RoomChat.razor             # Room chat page (/lobby, /room/{id})
│   │   ├── DmChat.razor               # DM chat page (/dm/{username})
│   │   ├── ChatBase.cs                # Shared base class for chat pages
│   │   ├── Settings.razor             # User profile settings (picture, display name, bio)
│   │   ├── Error.razor
│   │   └── NotFound.razor
│   ├── ChatHeader.razor               # Header with status dropdown, mailbox, user count
│   ├── ChatSidebar.razor              # Rooms list, users list with status dots
│   ├── MessageInput.razor             # Message input with typing, file upload
│   ├── MessageItem.razor              # Individual message display with avatars
│   ├── EmojiPicker.razor              # Emoji selection popup for reactions
│   ├── ImageGalleryModal.razor        # Lightbox for viewing uploaded images
│   ├── Avatar.razor                   # Reusable avatar (image or colored initials fallback)
│   ├── App.razor                      # Root component with Blazor.start() config
│   ├── Routes.razor
│   └── _Imports.razor
├── Configuration/
│   └── PersistenceSettings.cs
├── Data/
│   ├── ChatDbContext.cs               # EF Core DbContext
│   ├── ChatDbContextFactory.cs        # Design-time factory for migrations
│   ├── Migrations/
│   └── custom-emojis/                 # Drop image files here for custom emojis
├── Extensions/
│   └── PersistenceServiceExtensions.cs
├── Middleware/
│   ├── DeviceDetectionMiddleware.cs   # Detects mobile vs desktop
│   └── RequestLoggingMiddleware.cs    # HTTP request logging
├── Services/
│   ├── ChatService.cs                 # Core real-time functionality (singleton)
│   ├── ChatPersistenceService.cs      # Write-through database persistence
│   ├── ChatConfigService.cs           # UI text configuration from appsettings
│   ├── ChatNavigationState.cs         # Navigation state with [PersistentState]
│   ├── UserStateService.cs            # User identity with [PersistentState]
│   ├── UserService.cs                 # User management (join, leave, status)
│   ├── ChatCircuitHandler.cs          # Circuit lifecycle + auto-away detection
│   ├── CircuitTracker.cs              # Tracks active circuits per user
│   ├── PushSubscriptionStore.cs       # Push notification subscriptions
│   ├── PushNotificationService.cs     # Web push notifications
│   ├── EmojiService.cs                # Twemoji rendering + emoji-only message detection
│   ├── EmojiData.cs                   # Emoji definitions and categories
│   ├── CustomEmojiService.cs          # Custom emoji loading from Data/custom-emojis/
│   └── ImageService.cs                # Thumbnail generation (WebP)
├── Models/
│   ├── ChatMessage.cs
│   ├── Channel.cs
│   ├── ChannelType.cs
│   ├── ChannelReadState.cs
│   ├── Reaction.cs
│   ├── User.cs
│   ├── UserStatus.cs
│   ├── PushSubscription.cs
│   └── ChatDiagnostics.cs
├── wwwroot/
│   ├── js/chat.js                     # Tab notifications, badge API helpers
│   ├── uploads/                       # Image storage
│   ├── app.css                        # Base styles
│   ├── notif.mp3                      # Notification sound
│   ├── manifest.webmanifest           # PWA manifest
│   └── service-worker.js              # PWA service worker
├── Data/yap.db                        # SQLite database (when persistence enabled)
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
- Integrates with `ChatPersistenceService` for database persistence
- Supports paginated message loading for infinite scroll
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

**ImageService.cs** (Singleton)
- Generates WebP thumbnails on image upload

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

## Configuration

Settings in `appsettings.json` under `ChatSettings`:
- `ProjectName`, `RoomName` - Basic app identity
- `ClearUploadsOnStart` - Whether to clear uploads folder on startup
- `Persistence.Enabled`, `Persistence.Provider` - SQLite or Postgres
- `FunnyTexts` - Randomized UI text (welcome messages, join buttons, typing indicators)

## Features

- **Real-time messaging** - Instant delivery via Blazor circuit
- **Multiple rooms** - Create and switch between chat rooms (admin only)
- **Admin system** - First user becomes admin, can manage rooms (👑 badge)
- **Direct messages** - Private conversations (persist permanently when DB enabled)
- **User profiles** - Profile picture, display name, bio; avatars shown in messages (Discord-style)
- **User status** - Online (green), Away (orange), Invisible (gray) with dropdown selector
- **Auto-away** - Automatically sets status to Away after 5 minutes idle
- **Sign out** - Explicit sign out clears session and returns to login
- **Mailbox indicator** - Unread DM count in header, visible even with sidebar closed
- **Message actions** - Discord-style hover popup with reactions, edit, delete
- **Reactions** - ❤️ 😂 🥹 reactions on any message, shown as pills with counts
- **Edit/Delete** - Edit or delete your own messages (shows "edited" indicator)
- **Image sharing** - Direct file upload, up to 100MB, drag & drop support, WebP thumbnails
- **Multiline input** - Discord-style auto-expanding textarea (Shift+Enter for newlines)
- **Emoji support** - Twemoji rendering with Discord-style picker (category sidebar + scrollable grid)
- **Custom emojis** - Drop images into `Data/custom-emojis/`, auto-loaded as `:shortcode:` (filename = shortcode)
- **Tab notifications** - Unread count in title + audio
- **Online users** - List of users in sidebar with status
- **Infinite scroll** - Load older messages on scroll, Discord-style
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
- **User** - Username, status, last activity (survives app restarts)
- **Channel** → has many **ChatMessage** → has many **Reaction**
- **PushSubscription** - Web push notification endpoints per user
- DMs are identified by `Participant1`/`Participant2` fields on Channel

### Key Design Decisions
- **DMs persist permanently** (like Discord) - users see chat history when they return
- **Models = Tables** - No separate entity classes, models are EF-friendly
- **DbContextFactory** - Used by singleton `ChatService` to create short-lived DbContext instances
- **Pooled factory** - `AddPooledDbContextFactory` for singleton compatibility and performance

## Technical Details

### .NET 10 Features Used
- `[PersistentState]` attribute for circuit state persistence
- `RegisterPersistentService<T>()` for scoped service persistence
- `Blazor.resumeCircuit()` for session restoration
- Custom `Blazor.start()` configuration for retry timing
- `ReconnectModal` component (customized as top banner)
- `ResourcePreloader` for optimized asset loading
- `MapStaticAssets()` for fingerprinted static files
- `CreateInboundActivityHandler` for circuit activity tracking (auto-away)


### Custom Emojis
Drop image files (PNG, SVG, GIF, WebP, JPG) into `Data/custom-emojis/`. The filename (without extension) becomes the shortcode. For example, `pepe.png` becomes `:pepe:`.

- **Scanned on startup** by `CustomEmojiService` (singleton)
- **Filename rules**: alphanumeric, hyphens, underscores only (`^[a-zA-Z0-9_-]+$`)
- **Served via** `/custom-emojis/{filename}` static file route
- **Used in**: messages (`:shortcode:` syntax), emoji picker (shown as first category when present), reactions
- **EmojiPicker**: when custom emojis exist, a "Custom" section appears first in the picker with the first custom emoji as the sidebar icon

### File Upload & Thumbnails
Images are uploaded directly in the component using `InputFile`:
```csharp
await file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024).CopyToAsync(stream);
```

No HTTP multipart, no API endpoint - just direct file I/O.

**Thumbnail generation:**
- Medium (800px) generated immediately for fast gallery display
- Large (1600px) generated in background for lightbox
- All thumbnails are WebP for optimal compression
- Uses parallel processing when generating for multiple images

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

