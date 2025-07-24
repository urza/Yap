# Changelog

All notable changes to the BlazorChat project are documented in this file.

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