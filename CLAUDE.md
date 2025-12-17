# Yap - Blazor Server Chat Application

## Overview
A real-time chat application built with Blazor Server (.NET 10), featuring instant messaging and image sharing capabilities.

## Architecture

### Single Project Structure
```
Yap/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor
│   │   ├── ReconnectModal.razor      # .NET 10 built-in reconnection UI
│   │   └── ReconnectModal.razor.js
│   ├── Pages/
│   │   ├── Chat.razor                # Main chat page (home route)
│   │   ├── Chat.razor.css            # Discord-style dark theme
│   │   ├── Error.razor
│   │   └── NotFound.razor
│   ├── App.razor
│   ├── Routes.razor
│   └── _Imports.razor
├── Services/
│   ├── ChatService.cs                # Core real-time functionality
│   ├── ChatConfigService.cs          # UI text configuration
│   └── EmojiService.cs               # Twemoji rendering
├── Models/
│   └── ChatMessage.cs                # Message record type
├── wwwroot/
│   ├── js/chat.js                    # Tab notification helpers
│   ├── uploads/                      # Image storage
│   ├── app.css                       # Base styles
│   └── notif.mp3                     # Notification sound
├── appsettings.json                  # Chat config + funny texts
├── Program.cs
└── Yap.csproj
```

## How It Works

### Real-time Communication
Blazor Server uses a persistent SignalR connection (circuit) for all UI updates. We leverage this existing connection for chat functionality:

1. **ChatService** (singleton) - Holds all chat state and raises events
2. **Chat.razor** components subscribe to ChatService events
3. When a message is sent, ChatService notifies all subscribers
4. Each component calls `StateHasChanged()` to update its UI

No custom SignalR hub needed - Blazor's built-in circuit handles everything.

### Key Components

**ChatService.cs**
- Manages online users, messages, typing indicators
- Uses `ConcurrentDictionary` for thread-safe state
- Exposes events: `OnMessageReceived`, `OnUserChanged`, `OnUsersListChanged`, `OnTypingUsersChanged`

**Chat.razor**
- Main chat UI component
- Subscribes to ChatService events on join
- Unsubscribes and removes user on dispose
- Handles file uploads directly (no HTTP API needed)

**ChatConfigService.cs**
- Provides randomized UI text from configuration
- Fun Gen Z/Alpha slang for all UI elements

**EmojiService.cs**
- Converts Unicode emojis to Twemoji SVGs
- Supports large emoji rendering for emoji-only messages

## Configuration

All settings in `appsettings.json`:

```json
{
  "ChatSettings": {
    "ProjectName": "Yap",
    "RoomName": "lobby",
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
- **Image sharing** - Direct file upload, up to 100MB
- **Emoji support** - Twemoji rendering
- **Tab notifications** - Unread count in title + audio
- **Online users** - Live user list
- **Chat history** - Last 100 messages preserved
- **Typing indicators** - See who's typing
- **Mobile responsive** - Collapsible sidebar
- **Auto-reconnection** - .NET 10 ReconnectModal handles disconnects
- **Dark theme** - Discord-inspired UI

## Technical Details

### .NET 10 Features Used
- `ReconnectModal` component for reconnection UI
- `BlazorDisableThrowNavigationException` for cleaner navigation
- `ResourcePreloader` for optimized asset loading
- `MapStaticAssets()` for fingerprinted static files

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

## Previous Architecture (Migrated From)

The app was migrated from a 4-project Blazor WebAssembly + SignalR architecture:
- BlazorChat.Server (SignalR hub + API)
- BlazorChat.Client (WebAssembly)
- BlazorChat.Client.Serve (WASM host)
- BlazorChat.AppHost (Aspire)

See `MIGRATION_TO_BLAZOR_SERVER.md` for migration details.
