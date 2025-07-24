# BlazorChat Project Plan

## Overview
A real-time chat application using Blazor WebAssembly client with SignalR for real-time messaging and image sharing capabilities.

## Architecture

### Projects Structure
1. **BlazorChat.Server** (Port 5224)
   - Pure API server with SignalR Hub for real-time chat
   - RESTful API for image upload and chat history
   - File storage service for uploaded images
   - CORS configuration for browser access
   - **Important**: Browser connects directly to this server's external URL

2. **BlazorChat.Client** 
   - Blazor WebAssembly code that runs **in the browser**
   - Chat UI components with Discord-style dark theme
   - Username entry screen with randomized UI text
   - SignalR client connection to BlazorChat.Server
   - Image upload/preview functionality
   - Real-time typing indicators and emoji support

3. **BlazorChat.Client.Serve** (Port 5221)
   - Host server that serves the WebAssembly client to browsers
   - Provides `/api/config` endpoint so client knows API server location
   - Acts as static file server + configuration provider
   - **Key**: This is what users visit, but client connects to Server

4. **BlazorChat.AppHost**
   - .NET Aspire orchestration project
   - Manages all services during development

## Implementation Plan

### Phase 1: Server Setup
1. Create BlazorChat.Server project
   - ASP.NET Core Web API project
   - Add SignalR and file handling packages

2. Implement ChatHub
   ```csharp
   public class ChatHub : Hub
   {
       // Handle user connections
       // Broadcast messages
       // Handle user disconnections
   }
   ```

3. Image Upload API
   - POST endpoint: `/api/images/upload`
   - Store in `wwwroot/uploads/`
   - Return image URL

4. Configure Services
   - CORS for Blazor client
   - SignalR with WebSockets
   - Static file serving

### Phase 2: Client Implementation
1. Username Entry Component
   - Simple form before entering chat
   - Store username in session/memory

2. Chat Interface
   - Message list component
   - Message input with send button
   - Image upload button
   - Online users sidebar

3. SignalR Integration
   - HubConnection setup
   - Message send/receive handlers
   - Connection state management

4. Image Features
   - File picker for images
   - Upload progress indicator
   - Image preview in messages
   - Click to view full size

### Phase 3: Features & Polish
1. Message Types
   - Text messages
   - Image messages
   - System notifications (user joined/left)

2. UI Enhancements
   - Responsive design
   - Auto-scroll to latest message
   - Timestamps
   - User avatars (generated from username)

3. Additional Features
   - Typing indicators
   - Message timestamps
   - Online user count
   - Connection retry logic
   - Emoji support with Twemoji rendering

## Technical Details

### SignalR Hub Methods
- `SendMessage(string user, string message)`
- `SendImage(string user, string imageUrl)`
- `UserJoined(string user)`
- `UserLeft(string user)`

### API Endpoints (BlazorChat.Server)
- `POST /api/images/upload` - Upload image file
- `GET /uploads/{filename}` - Serve uploaded images
- `GET /api/chat/history` - Get recent chat messages
- `/api/chathub` - SignalR hub endpoint

### Configuration Endpoints (BlazorChat.Client.Serve)
- `GET /api/config` - Configuration discovery endpoint, returns ApiUrl for WebAssembly client

### Client State
- Current username
- Connection status
- Message history
- Online users list

## Networking Architecture

### Development (Aspire)
- All services run locally with Aspire orchestration
- Client discovers server URL automatically

### Docker Deployment
- **Browser** → **BlazorChat.Client.Serve** (localhost:5221) - Gets WebAssembly app
- **WebAssembly in Browser** → **BlazorChat.Client.Serve** `/api/config` - Gets API server URL
- **WebAssembly in Browser** → **BlazorChat.Server** (localhost:5224) - API calls & SignalR
- **Important**: WebAssembly needs external URLs, not Docker internal networking

## Configuration Discovery Pattern

### The Problem
WebAssembly code runs in the browser and needs to connect to the API server, but:
- WebAssembly is compiled at build time with fixed URLs
- Different deployment environments need different API URLs
- Can't hardcode URLs because they change per environment

### The Solution: Runtime Configuration
1. **WebAssembly asks BlazorChat.Client.Serve** for configuration via `/api/config`
2. **BlazorChat.Client.Serve responds** with the correct API URL for the current environment  
3. **WebAssembly uses discovered URL** to connect to **BlazorChat.Server**

### Implementation Flow
```
Browser loads WebAssembly from BlazorChat.Client.Serve (localhost:5221)
       ↓
WebAssembly calls GET /api/config on BlazorChat.Client.Serve (same-origin)
       ↓
BlazorChat.Client.Serve responds: {"ApiUrl": "http://localhost:5224"}
       ↓
WebAssembly connects to SignalR on BlazorChat.Server at discovered URL
```

### Benefits
- **Environment agnostic**: Same WebAssembly works in dev, staging, production
- **Same-origin safety**: Config fetch always works (no CORS issues)
- **Runtime flexibility**: Can change API URL without rebuilding WebAssembly

## Security Considerations
- File upload size limits
- Image type validation
- Sanitize user inputs
- No authentication (as requested)
