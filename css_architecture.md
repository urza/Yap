# CSS Architecture Overview

This project uses **Blazor CSS Isolation** - each component has its own `.razor.css` file that gets scoped automatically.

## How It Works

**Build Process:**
1. Each `.razor.css` file gets processed at build time
2. Blazor adds unique attribute selectors like `[b-36jla8vlnc]` to each rule
3. The corresponding component's HTML gets the same attribute
4. Result: CSS is scoped to just that component

**What You See in DevTools:**
```css
/* Your source code */
.message-input { ... }

/* What browser sees */
.message-input[b-36jla8vlnc] { ... }
```

---

## File Map (What's Where)

| File | Purpose | Key Classes |
|------|---------|-------------|
| `wwwroot/app.css` | **Global base styles** - resets, body, error boundaries | `html, body`, `.blazor-error-boundary` |
| `Components/Layout/ChatLayout.razor.css` | **Main container layout** - the flex column that holds everything | `.chat-container`, `.chat-main`, `.messages-container`, `::deep .messages` |
| `Components/ChatHeader.razor.css` | **Top header bar** | `.chat-header`, `.status-dropdown`, `.mailbox-button` |
| `Components/ChatSidebar.razor.css` | **Right sidebar** - rooms & users lists | `.users-sidebar`, `.room-item`, `.user-item` |
| `Components/MessageInput.razor.css` | **Input area at bottom** | `.message-input-container`, `.message-input`, `.send-button`, `.typing-indicator` |
| `Components/MessageItem.razor.css` | **Individual messages** | `.message-group`, `.message-content`, `.image-gallery`, `.message-actions`, `.reaction-pill` |
| `Components/Pages/Login.razor.css` | **Login page** | `.username-container`, `.username-form` |
| `Components/EmojiPicker.razor.css` | **Emoji picker popup** | `.emoji-picker`, `.emoji-grid` |
| `Components/ImageGalleryModal.razor.css` | **Fullscreen image viewer** | `.image-modal`, `.modal-nav` |
| `Components/Layout/ReconnectModal.razor.css` | **Connection banner** | `#components-reconnect-modal`, `.reconnect-banner-content` |
| `Components/Layout/MainLayout.razor.css` | **Blazor error UI only** | `#blazor-error-ui` |

---

## Layout Hierarchy

```
┌─────────────────────────────────────────────────┐
│ .chat-container (ChatLayout)                    │
│ flex-direction: column, height: 100svh          │
│ ┌─────────────────────────────────────────────┐ │
│ │ .chat-header (ChatHeader)                   │ │
│ └─────────────────────────────────────────────┘ │
│ ┌─────────────────────────────────────────────┐ │
│ │ .chat-main (ChatLayout)                     │ │
│ │ flex: 1, display: flex                      │ │
│ │ ┌───────────────────────────┬─────────────┐ │ │
│ │ │ .messages-container       │ .users-     │ │ │
│ │ │ flex: 1                   │ sidebar     │ │ │
│ │ │ ┌───────────────────────┐ │ width:240px │ │ │
│ │ │ │ .messages (::deep)    │ │             │ │ │
│ │ │ │ flex: 1, overflow-y   │ │             │ │ │
│ │ │ │ ┌───────────────────┐ │ │             │ │ │
│ │ │ │ │ .message-group    │ │ │             │ │ │
│ │ │ │ │ (MessageItem)     │ │ │             │ │ │
│ │ │ │ └───────────────────┘ │ │             │ │ │
│ │ │ └───────────────────────┘ │             │ │ │
│ │ │ ┌───────────────────────┐ │             │ │ │
│ │ │ │ .typing-indicator-    │ │             │ │ │
│ │ │ │ container             │ │             │ │ │
│ │ │ │ .message-input-       │ │             │ │ │
│ │ │ │ container             │ │             │ │ │
│ │ │ │ (MessageInput)        │ │             │ │ │
│ │ │ └───────────────────────┘ │             │ │ │
│ │ └───────────────────────────┴─────────────┘ │ │
│ └─────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────┘
```

---

## Key CSS Patterns Used

### 1. Flexbox Everywhere
Almost all layouts use flexbox. The critical thing to remember:
```css
/* Parent must allow shrinking below content size */
.flex-parent {
    min-width: 0;  /* THIS IS CRUCIAL for preventing overflow */
}
```

### 2. `::deep` Selector
Used in `ChatLayout.razor.css` to style content that's rendered inside the component (like `.messages` which contains child components):
```css
::deep .messages {
    flex: 1;
    overflow-y: auto;
    padding: 1rem 0 0.25rem 0;
}
```

### 3. Mobile Breakpoints
Two breakpoints used:
- `@media (max-width: 768px)` - Tablet/sidebar behavior
- `@media (max-width: 600px)` - Phone adjustments

**Critical gotcha:** Media queries can override base styles unexpectedly:
```css
/* Base */
.message-input-container {
    padding: 0.5rem;
    padding-bottom: 1rem;  /* Gets overridden! */
}

/* Mobile - this replaces ALL padding */
@media (max-width: 600px) {
    .message-input-container {
        padding: 0.5rem;  /* Must re-add padding-bottom here too */
        padding-bottom: 1rem;
    }
}
```

---

## DevTools Tips

### Finding Which CSS File
1. Inspect element
2. Look at the scoped attribute: `[b-36jla8vlnc]`
3. In Sources panel, search for that hash to find the component

### Common Elements to Adjust

**Space below messages:**
```css
/* ChatLayout.razor.css line ~31 */
::deep .messages {
    padding: 1rem 0 0.25rem 0;  /* last value = bottom padding */
}
```

**Input box spacing from bottom:**
```css
/* MessageInput.razor.css line ~8 */
.message-input-container {
    padding-bottom: 1rem;  /* adjust this */
}
```

**Message spacing:**
```css
/* MessageItem.razor.css line ~3 */
.message-group {
    padding: 0.125rem 1rem;
}
```

**Gallery image size:**
```css
/* MessageItem.razor.css line ~106 */
.gallery-single .gallery-image {
    max-height: 300px;
    max-width: min(400px, 100%);
}
```

---

## Assessment

**What's Good:**
- Component isolation prevents style conflicts
- Consistent Discord-like color scheme (#36393f, #2f3136, etc.)
- Mobile responsive with clear breakpoints

**What Could Be Better:**
- No CSS variables for colors (hardcoded everywhere)
- Some duplication (typing dots defined in both MessageInput and ReconnectModal)
- The `min-width: 0` hack scattered around to fix flexbox overflow issues
- Gallery CSS is fragile (the multi-image layout issue)

**Recommendation:**
The architecture is fine and maintainable. The main issue is **content overflow breaking layout** - always ensure flex containers have `min-width: 0` and images have `max-width: 100%`. Consider adding CSS variables for the color palette if you want to clean things up.
