# Changelog

All notable changes to Yap are documented in this file.

## [2.5.0] - 2026-01-01

### User Status & Sign Out

#### New Features
- **User status system** - Set your presence status with visual indicators
  - Online (green dot) - Default active state
  - Away (orange dot) - Manually set or for future auto-away
  - Invisible (gray dot) - Appear offline but still connected
- **Status dropdown** - Click username in header to open status selector
  - Shows current status with checkmark
  - Settings option (placeholder for future)
  - Sign out option
- **Sign out functionality** - Explicit sign out clears session and returns to login
- **Status indicators in sidebar** - See everyone's status at a glance
- **Unified Channel model** - Rooms and DMs now use same `Channel` class with factory methods
- **Configurable message history** - `MaxMessagesPerChannel` setting in appsettings.json

#### Technical Changes
- Added `UserStatus` enum (Online, Away, Invisible)
- Added `Status` property to `UserStateService` with `[PersistentState]`
- Added `UserSession` record now includes `Status`
- Added `OnUserStatusChanged` event to `ChatService`
- Added `SetUserStatusAsync()` and `GetUserStatus()` methods
- Added `GetAllUsersWithStatus()` for sidebar display
- Renamed `CircuitId` to `SessionId` for clarity (it's not the Blazor circuit ID)
- Replaced `Room` and `DirectMessage` models with unified `Channel` model
- `Channel.CreateRoom()` and `Channel.CreateDM()` factory methods

#### UI Changes
- Username in header is now a button with dropdown menu
- Status button shows current status color (green/orange/gray)
- Dropdown includes status options, settings placeholder, and sign out
- Sidebar users show colored status dots
- Click-outside closes the dropdown

---

## [2.4.0] - 2026-01-01

### Resilient Reconnection & Persistent State (.NET 10)

#### New Features
- **Discord-style reconnection banner** - Non-blocking top banner instead of modal
  - Appears immediately when connection lost
  - Shows animated loading dots during reconnection
  - Countdown timer between retry attempts
  - No dots shown for terminal states (failed, resume failed)
- **Infinite reconnection retries** - Never gives up trying to reconnect
  - Immediate first attempt, then every 4 seconds
  - Configurable via `Blazor.start()` in App.razor
- **Auto-resume on circuit eviction** - When circuit expires, automatically restores session
  - Uses `Blazor.resumeCircuit()` API
  - No user action required (no "Resume" button click needed)
- **Persistent state across disconnections** - User stays logged in even after long disconnections
  - Username preserved via `[PersistentState]` on UserStateService
  - Current room/DM preserved via `[PersistentState]` on ChatNavigationState
  - State kept in memory for 48 hours after circuit eviction

#### Configuration
- **Circuit retention**: 4 hours (default was 3 minutes)
- **Persisted state retention**: 48 hours (default was 2 hours)
- **Max disconnected circuits**: 1000 (default was 100)
- **Max persisted states**: 5000 (default was 1000)

#### Technical Changes
- Added `[PersistentState]` attribute to `UserStateService.Username` and `UserStateService.SessionId`
- Added `[PersistentState]` attribute to `ChatNavigationState` properties (Title, CurrentRoomId, CurrentDmUser, SidebarOpen)
- Registered services with `RegisterPersistentService<T>(RenderMode.InteractiveServer)` in Program.cs
- Configured `CircuitOptions.PersistedCircuitInMemoryRetentionPeriod` and related options
- Rewrote `ReconnectModal.razor` as top banner (was dialog modal)
- Rewrote `ReconnectModal.razor.js` with auto-resume logic and visibility control
- Rewrote `ReconnectModal.razor.css` with banner styling and animated dots
- Added custom `Blazor.start()` configuration in App.razor for retry timing

#### Architecture Improvements
- Components now inject services directly (self-sufficient)
- Real Blazor layout (`ChatLayout.razor`) with header, sidebar, and `@Body`
- Thin pages (`RoomChat.razor`, `DmChat.razor`) focused on message display
- Reduced prop-drilling between components

---

## [2.3.0] - 2025-12-30

### Rooms, Admin & DM Improvements

#### New Features
- **Multiple chat rooms** - Create and switch between different chat rooms
  - Default "lobby" room always exists
  - Each room has its own message history
  - Rooms listed in sidebar with # prefix
- **Admin system** - First user to join becomes admin
  - Admin badge (🛡️) displayed next to admin's name
  - Only admin can create/delete rooms
  - Admin status persists until server restart
- **Mailbox icon in header** - Shows total unread DM count even when sidebar is closed
  - Outline icon when no unreads, filled with badge when messages waiting
  - Click to open sidebar
- **User sorting** - Users in sidebar sorted by last DM message time, unread conversations always on top
- **DM ephemeral notice** - System message at top of DM conversations explaining messages disappear when either user leaves
- **Discord-style multiline input** - Textarea that auto-expands as you type
  - Enter sends message, Shift+Enter for new line
  - Auto-resizes up to 200px max height
  - Hidden scrollbar for cleaner look
- **Long text wrapping** - Messages and input now properly wrap long text without spaces (URLs, hashes, etc.)
- **Clear uploads on start** - Configurable option to delete all uploaded files when app starts (default: true)

#### UI Polish
- Reduced typing indicator height for more compact footer
- Fixed scrollbar flash when sending messages
- Fixed first keypress sometimes not registering after sending

#### Technical Changes
- Added `Room` model with Id, Name, CreatedAt, CreatedBy, IsDefault
- Added admin tracking with `OnAdminChanged` event
- Added `CreateRoomAsync()`, `DeleteRoomAsync()`, `GetRooms()` to ChatService
- Added `GetLastDMTimestamp()` and `GetTotalUnreadDMCount()` to ChatService
- Changed message input from `<input>` to `<textarea>` with JS auto-resize
- Added `autoResizeTextarea` and `resetTextareaHeight` JS functions
- New config option: `ChatSettings:ClearUploadsOnStart`

---

## [2.2.0] - 2025-12-17

### Multi-Image Upload & Drag-Drop

#### New Features
- **Multiple image upload** - Select multiple images at once (up to 10 files)
- **Drag and drop** - Drop images directly onto the message input area
- **Gallery preview** - Compact single-row thumbnail display
  - 1 image: larger preview (up to 300px)
  - 2 images: medium thumbnails (100x100px)
  - 3-4 images: compact thumbnails (80x80px)
  - 5+ images: shows 4 thumbnails with "+N" overlay on the last one
- **Image modal navigation** - Click any thumbnail to open full-size viewer
  - Prev/next buttons for multi-image galleries
  - Image counter (e.g., "3 / 7")
  - Keyboard support: Arrow keys to navigate, Escape to close
- **Visual drop feedback** - Input area highlights when dragging files over it

#### Technical Changes
- `ChatMessage` model changed from `bool IsImage` to `List<string> ImageUrls`
- Added `HasImages` computed property
- `ChatService.SendMessageAsync` now accepts `List<string>? imageUrls` parameter
- New CSS classes: `.image-gallery`, `.gallery-item`, `.gallery-more-overlay`
- New JS functions: `setupDropZone`, `setupModalKeyboard`, `removeModalKeyboard`

---

## [2.1.0] - 2025-12-17

### Discord-Style Message Actions

#### New Features
- **Message hover popup** - Actions appear when hovering over messages
- **Reactions** - ❤️ 😂 🥹 emoji reactions on any message
- **Reaction pills** - Display under messages with count and who reacted (tooltip)
- **Edit messages** - Edit your own text messages (shows "edited" indicator)
- **Delete messages** - Delete your own messages

#### Technical Changes
- `ChatMessage` model now has `Id`, `IsEdited`, and `Reactions` dictionary
- `ChatService` uses `ConcurrentDictionary` for O(1) message lookups
- New events: `OnMessageUpdated`, `OnMessageDeleted`, `OnReactionChanged`
- New methods: `EditMessageAsync`, `DeleteMessageAsync`, `ToggleReactionAsync`

---

## [2.0.0] - 2025-12-17

### Complete Rewrite to Blazor Server (.NET 10)

This release represents a complete architectural rewrite from Blazor WebAssembly to Blazor Server.

#### Architecture Changes
- **Single Project**: Consolidated from 4 projects (Server, Client, Client.Serve, AppHost) to 1 project (Yap)
- **No Custom SignalR Hub**: Real-time now handled by Blazor Server's built-in circuit
- **No API Endpoints**: File uploads handled directly in components
- **No CORS Configuration**: Same-origin requests only
- **No Aspire Orchestration**: Single `dotnet run` to start

#### .NET 10 Features
- **ReconnectModal**: Built-in reconnection UI with circuit resume support
- **ResourcePreloader**: Optimized asset loading
- **BlazorDisableThrowNavigationException**: Cleaner navigation handling
- **MapStaticAssets()**: Fingerprinted static file serving

#### Technical Implementation
- `ChatService` singleton with C# events replaces SignalR hub
- Components subscribe to events and call `StateHasChanged()`
- Direct `InputFile` + `FileStream` for image uploads
- ~60% code reduction

#### What's Removed
| Removed | Reason |
|---------|--------|
| SignalR Hub | Blazor circuit handles real-time |
| `/api/config` endpoint | Direct service injection |
| `/api/images/upload` endpoint | Direct file handling |
| `/api/chat/history` endpoint | Direct service access |
| CORS configuration | Same-origin requests |
| HttpClient service | No cross-server calls |
| Aspire orchestration | Single project |
| WebAssembly runtime | Server-rendered |

#### Benefits
- Single codebase to maintain
- Simpler deployment (one container)
- Better debugging (full server-side)
- Faster development cycle
- Standard ASP.NET Core patterns

---

## [1.2.2] - 2025-07-24

### ✨ New Features

#### Tab Notifications System
- ✅ **Tab Title Notifications**: Shows unread message count in browser tab title format "(3) ProjectName"
- ✅ **Audio Notifications**: Plays custom sound notification when messages arrive in background tabs
- ✅ **Smart Behavior**: Only triggers for messages from other users when tab is inactive
- ✅ **Auto-Reset**: Both title and count reset when user returns to tab
- ✅ **Discord-Style Experience**: Similar notification behavior to Discord desktop app

### Technical Implementation
- Added `tabNotifications` JavaScript helper with page visibility detection
- Integrated with SignalR message handlers for real-time notification triggers
- Uses Document Visibility API for accurate tab state detection
- Proper cleanup and memory management for event listeners
- Support for custom MP3 notification sounds in `wwwroot/` folder
- Browser autoplay policy handling with graceful fallbacks

### Files Modified
- `BlazorChat.Client/wwwroot/chat.js` - Added notification system
- `BlazorChat.Client/Pages/Chat.razor` - Integrated notification tracking
- `BlazorChat.Client/wwwroot/notif.mp3` - Custom notification sound file

## [1.2.1] - 2025-07-21

### ✨ New Features

#### Emoji Support
- ✅ Ultra-minimal custom emoji replacement system
- ✅ Converts Unicode emojis to Twemoji SVGs for consistent rendering
- ✅ Uses Twitter's Twemoji CDN for reliable emoji display
- ✅ Simple regex-based detection with proper surrogate pair handling
- ✅ Maintains simplicity - users type emojis normally with their keyboards
- ✅ No emoji picker UI - keeps with project's minimalist philosophy

### Technical Implementation
- Added `EmojiService` with precise emoji detection
- Integrated into message rendering without changing storage
- Fallback to original emoji if SVG not found
- Proper Unicode code point conversion for Twemoji compatibility
- Uses Twemoji v16 from https://github.com/jdecked/twemoji (the new official repository maintained by original authors)
- Added Discord-style large emoji rendering for emoji-only messages
- Replaced paperclip emoji with clean SVG icon for image upload button

## [1.2.0] - 2025-07-15

### 🎨 UI/UX Overhaul

#### Gen Z/Alpha Text Variations
- ✅ Complete UI text transformation with Gen Z/Alpha slang
- ✅ Randomized text variations for all UI elements
- ✅ Project renamed from "BlazorChat" to "Yap"
- ✅ Fun connection status indicators (vibin 🟢, ratioed ❌)
- ✅ Creative typing indicators ("X is yapping", "X and Y are cooking")
- ✅ Modern join/leave messages ("X pulled up", "X dipped")

### 🏗️ Architecture Changes

#### Disabled Pre-rendering
- ✅ Removed server-side pre-rendering to fix UI flashing
- ✅ Eliminated configuration duplication between server and client
- ✅ Simplified architecture with client-only configuration
- ✅ All settings now in client's `wwwroot/appsettings.json`

### Technical Details
- Removed `ChatConfigService` from server project
- Updated render mode to `InteractiveWebAssemblyRenderMode(prerender: false)`
- Cleaned up index-based text selection methods (no longer needed)

## [1.1.0] - 2025-07-15

### 🎉 New Features

#### Typing Indicators
- ✅ Real-time typing status display
- ✅ Shows "X is typing..." with animated dots
- ✅ Supports multiple users typing simultaneously
- ✅ Auto-clears after 3 seconds of inactivity
- ✅ Smooth animations and transitions

### Technical Implementation
- Added `StartTyping` and `StopTyping` methods to SignalR ChatHub
- Created reusable `TypingIndicator` component
- Implemented debounced typing detection on message input
- Added server-side cleanup for stale typing indicators

## [1.0.0] - 2025-07-15

### 🎉 Initial Release

#### Core Features Implemented

**Server (BlazorChat.Server)**
- ✅ SignalR ChatHub with real-time messaging
- ✅ Image upload API endpoint (`/api/images/upload`)
- ✅ File validation (image types only, 100MB limit)
- ✅ Chat history service (stores last 100 messages)
- ✅ CORS configuration for Blazor client
- ✅ Static file serving for uploaded images
- ✅ Automatic user disconnection handling

**Client (BlazorChat.Client)**
- ✅ Username entry screen
- ✅ Real-time chat interface
- ✅ Message input with Enter key support
- ✅ Image upload with file picker
- ✅ Online users sidebar (collapsible on mobile)
- ✅ Connection status indicator
- ✅ Auto-reconnection on connection loss
- ✅ Chat history retrieval on join
- ✅ Full-size image viewer modal
- ✅ Message grouping for consecutive messages
- ✅ Timestamp display on messages

**UI/UX Enhancements**
- ✅ Discord-inspired dark theme
- ✅ Fully responsive design
- ✅ Mobile-optimized with backdrop effects
- ✅ Smooth animations and transitions
- ✅ Auto-scroll to latest messages
- ✅ Professional chat interface layout

#### Beyond Original Plan

**Progressive Web App (PWA)**
- ✅ Web app manifest for installability
- ✅ Theme colors and app icons
- ✅ Standalone app mode support

**Docker Support**
- ✅ Dockerfiles for Server and Client projects
- ✅ Docker Compose configuration
- ✅ Volume mapping for persistent uploads
- ✅ Multi-stage builds for optimization

**Architecture Improvements**
- ✅ Removed YARP reverse proxy for simplicity
- ✅ Direct API calls from Blazor client
- ✅ Dynamic API URL configuration
- ✅ Improved error handling and logging

### 📊 Completion Status

**From Original Plan (CLAUDE.md):**
- Phase 1 (Server Setup): 100% Complete
- Phase 2 (Client Implementation): 100% Complete
- Phase 3 (Features & Polish): 90% Complete
  - Not implemented: Typing indicators, User avatars

**Additional Features Added:**
- Docker containerization
- PWA capabilities
- Enhanced UI/UX beyond original scope
- Better architecture decisions

### 🔧 Technical Stack

- **Frontend**: Blazor WebAssembly (.NET 8)
- **Backend**: ASP.NET Core with SignalR
- **Real-time**: SignalR WebSockets
- **Styling**: Tailwind CSS
- **Icons**: Heroicons
- **Deployment**: Docker & Docker Compose
- **Orchestration**: .NET Aspire

### 📝 Notes

This release represents a fully functional real-time chat application that exceeds the original project plan. The application is production-ready with proper error handling, responsive design, and easy deployment options.