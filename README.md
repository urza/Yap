# Yap

A real-time chat application built with Blazor Server (.NET 10), featuring instant messaging and image sharing capabilities.

## Features

- **Real-time messaging** - Instant message delivery via Blazor Server circuit
- **Message actions** - Discord-style hover popup on messages
- **Reactions** - ❤️ 😂 🥹 emoji reactions with counts
- **Edit/Delete** - Edit or delete your own messages
- **Image sharing** - Upload multiple images at once (up to 10 files, 100MB each)
- **Drag & drop** - Drop images onto the input area to upload
- **Image gallery** - Compact thumbnail row with "+N" overlay, full-size modal with navigation
- **Emoji support** - Consistent Twemoji rendering
- **Tab notifications** - Unread count in browser tab + audio notifications
- **Online users** - See who's currently in the chat
- **Chat history** - Last 100 messages preserved
- **Typing indicators** - See who's typing in real-time
- **Mobile responsive** - Works great on all devices with collapsible sidebar
- **Dark theme** - Discord-inspired UI
- **Auto-reconnection** - .NET 10's built-in ReconnectModal handles disconnects gracefully

## Architecture

Single-project Blazor Server application:

```
Yap/
├── Components/
│   ├── Pages/Chat.razor          # Main chat UI
│   └── Layout/ReconnectModal.razor  # .NET 10 reconnection UI
├── Services/
│   ├── ChatService.cs            # Real-time chat logic
│   ├── ChatConfigService.cs      # UI text configuration
│   └── EmojiService.cs           # Emoji rendering
├── Models/ChatMessage.cs
├── wwwroot/
│   ├── js/chat.js                # Tab notifications
│   ├── uploads/                  # Image storage
│   └── notif.mp3                 # Notification sound
└── appsettings.json              # Configuration
```

### How Real-time Works

Blazor Server maintains a persistent SignalR connection (circuit) for UI updates. We use this same connection for chat:

1. `ChatService` (singleton) holds chat state and raises events
2. Each user's `Chat.razor` component subscribes to these events
3. When someone sends a message, all subscribers get notified
4. Components call `StateHasChanged()` to push updates to browsers

No custom SignalR hub needed - Blazor's built-in circuit handles everything.

## Getting Started

### Prerequisites

- .NET 10 SDK
- Visual Studio 2022 or VS Code (optional)

### Running Locally

```bash
cd Yap
dotnet run
```

Open the URL shown in the console (typically `https://localhost:5001`).

### Running with Docker

```bash
# Build
docker build -t yap ./Yap

# Run
docker run -p 5221:8080 -v ./uploads:/app/wwwroot/uploads yap
```

Access at `http://localhost:5221`

## Using the Chat

1. Enter your username on the welcome screen
2. Start chatting - press Enter to send messages
3. Use emojis naturally - they render as Twemoji SVGs
4. Click the image button to upload pictures (supports multiple selection)
5. Drag and drop images directly onto the message input area
6. Hover over messages to react, edit, or delete
7. Toggle the sidebar on mobile to see online users

## Configuration

Edit `appsettings.json` to customize:

```json
{
  "ChatSettings": {
    "ProjectName": "Yap",
    "RoomName": "lobby",
    "FunnyTexts": {
      "WelcomeMessages": ["welcome to {0}", "you ready?"],
      "JoinButtonTexts": ["lessgo", "slide in", "hop on"],
      "SystemMessages": {
        "UserJoined": ["{0} just dropped", "{0} pulled up"],
        "UserLeft": ["{0} dipped", "{0} ghosted us"]
      }
    }
  }
}
```

## File Upload Limits

- Maximum file size: 100MB per file
- Maximum files per upload: 10
- Supported formats: JPEG, PNG, GIF, WebP
- Drag & drop: Supported

## Tech Stack

- **Framework**: Blazor Server (.NET 10)
- **Real-time**: Blazor circuit (built-in SignalR)
- **Styling**: Scoped CSS with Discord-inspired dark theme
- **Emojis**: Twemoji v16

## Migration History

This project was migrated from a 4-project Blazor WebAssembly + SignalR architecture to a single Blazor Server project. See `MIGRATION_TO_BLAZOR_SERVER.md` for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is open source and available under the MIT License.
