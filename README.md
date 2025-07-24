# BlazorChat

A real-time chat application built with Blazor WebAssembly and SignalR, featuring instant messaging and image sharing capabilities.

## 🚀 Features

- **Real-time messaging** - Instant message delivery using SignalR WebSockets
- **Image sharing** - Upload and share images up to 100MB
- **Emoji support** - Consistent Twemoji rendering using v16 from [jdecked/twemoji](https://github.com/jdecked/twemoji) (official repository)
- **Online users** - See who's currently in the chat
- **Chat history** - Last 100 messages are preserved
- **Mobile responsive** - Works great on all devices with collapsible sidebar
- **PWA support** - Install as a standalone app on mobile devices
- **Dark theme** - Modern Discord-inspired UI
- **Auto-reconnection** - Automatically reconnects if connection is lost
- **Docker ready** - Easy deployment with Docker Compose

## 🏗️ Architecture

```
Browser → BlazorChat.Client.Serve (Port 5221) → Serves WebAssembly
   ↓
WebAssembly runs in browser
   ↓
WebAssembly → BlazorChat.Server (Port 5224) → SignalR + API
```

### Projects

- **BlazorChat.Client** - Blazor WebAssembly frontend that runs in the browser
- **BlazorChat.Client.Serve** - Host server that serves the WebAssembly app and provides configuration
- **BlazorChat.Server** - Pure API server with SignalR hub and file uploads
- **BlazorChat.AppHost** - .NET Aspire orchestration for development

### Key Architecture Points

- **WebAssembly runs in browser**: The client code executes in the user's browser, not in a container
- **Two-server setup**: One serves the WebAssembly files, another provides the API
- **External URL requirement**: Since WebAssembly runs in browser, it needs external URLs (localhost:5224) not Docker internal URLs
- **Configuration discovery**: Client fetches API server location from `/api/config` endpoint

### Architecture Decisions

- **No Pre-rendering**: The app runs as pure Blazor WebAssembly without server-side pre-rendering to avoid configuration duplication and UI flashing issues
- **Client-side Configuration**: All UI text variations and settings are managed client-side in `wwwroot/appsettings.json`

## 🛠️ Getting Started

### Prerequisites

- .NET 9.0 SDK or later
- Visual Studio 2022 or VS Code (optional)
- Docker (for containerized deployment)

### Running Locally

1. Clone the repository
2. Navigate to the solution directory
3. Run the Aspire AppHost:
   ```bash
   cd BlazorChat.AppHost
   dotnet run
   ```
4. Open the Aspire dashboard link shown in the console
5. Click on the BlazorChat endpoint to access the chat

### Running with Docker

```bash
docker-compose up --build
```

Access the application at:
- **Chat UI**: http://localhost:5221 (what users visit)
- **API Server**: http://localhost:5224 (WebAssembly connects to this)

## 📱 Using the Chat

1. Enter your username on the welcome screen
2. Start chatting! Press Enter or click Send to send messages
3. Use emojis naturally - they'll render as consistent Twemoji SVGs
4. Click the image button to upload and share pictures
5. Toggle the sidebar on mobile to see online users

## 🔧 Configuration

### API URL Configuration

The client automatically discovers the API URL from the server. For custom deployments, you can override this by setting the `ApiUrl` environment variable.

### File Upload Limits

- Maximum file size: 100MB
- Supported formats: JPEG, PNG, GIF, WebP

## 📁 Project Structure

```
BlazorChat/
├── BlazorChat.Server/          # Pure API server (Port 5224)
│   ├── Hubs/                   # SignalR hub implementation
│   ├── Services/               # Business logic
│   └── wwwroot/uploads/        # Uploaded images storage
├── BlazorChat.Client/          # WebAssembly app code
│   ├── Layout/                 # UI layout components
│   ├── Pages/                  # Page components (Chat.razor)
│   ├── Services/               # Client services
│   └── wwwroot/                # Static assets & config
├── BlazorChat.Client.Serve/    # WebAssembly host server (Port 5221)
│   ├── Components/             # App.razor host template
│   └── wwwroot/                # Host static files
├── BlazorChat.AppHost/         # Aspire orchestration
└── docker-compose.yml          # Container orchestration
```

## 🚢 Deployment

See [README.Docker.md](README.Docker.md) for detailed Docker deployment instructions.

For production deployments, consider:
- Using HTTPS with proper SSL certificates
- Implementing authentication if needed
- Setting up persistent storage for uploads
- Configuring appropriate CORS policies
- Adding rate limiting for uploads

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is open source and available under the MIT License.