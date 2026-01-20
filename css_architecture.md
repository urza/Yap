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
| `wwwroot/app.css` | **Global base styles** - CSS variables, resets, body, error boundaries | `:root`, `html, body`, `.blazor-error-boundary` |
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

## CSS Variables (Custom Properties)

All colors are defined as CSS variables in `wwwroot/app.css` on the `:root` selector. This enables future theming support.

### Color Palette

```css
:root {
    /* Backgrounds (darkest to lightest) */
    --bg-darkest: #18191c;
    --bg-header: #202225;
    --bg-sidebar: #2f3136;
    --bg-hover: #32353b;
    --bg-primary: #36393f;
    --bg-secondary: #393c43;
    --bg-input: #40444b;
    --bg-muted: #4f545c;

    /* Text colors (brightest to dimmest) */
    --text-white: #fff;
    --text-primary: #dcddde;
    --text-secondary: #b9bbbe;
    --text-muted: #a3a6aa;
    --text-tertiary: #96989d;
    --text-placeholder: #72767d;

    /* Accent colors (Discord blurple) */
    --accent-primary: #5865f2;
    --accent-hover: #4752c4;
    --accent-light: #dee0fc;
    --accent-focus: #00aff4;

    /* Status colors */
    --status-online: #3ba55c;
    --status-online-hover: #2d7d46;
    --status-away: #faa61a;
    --status-away-hover: #fcc24f;
    --status-invisible: #747f8d;

    /* Semantic colors */
    --color-success: #3ba55c;
    --color-warning: #faa61a;
    --color-danger: #ed4245;

    /* Borders */
    --border-dark: #202225;
    --border-subtle: #040405;
}
```

### Usage in Components

CSS variables cascade through the DOM regardless of Blazor's CSS isolation:

```css
/* In any .razor.css file */
.my-element {
    background: var(--bg-primary);
    color: var(--text-primary);
    border: 1px solid var(--border-dark);
}

.my-button {
    background: var(--accent-primary);
}

.my-button:hover {
    background: var(--accent-hover);
}
```

### Future Theming

To add a light theme, override variables with a data attribute selector:

```css
[data-theme="light"] {
    --bg-primary: #ffffff;
    --bg-sidebar: #f2f3f5;
    --text-primary: #2e3338;
    /* ... override other variables ... */
}
```

Then toggle themes by setting `data-theme` on the `<html>` element.

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
- CSS variables for all colors - easy theming support
- Consistent Discord-like color scheme
- Mobile responsive with clear breakpoints

**What Could Be Better:**
- Some duplication (typing dots defined in both MessageInput and ReconnectModal)
- The `min-width: 0` hack scattered around to fix flexbox overflow issue

**Recommendation:**
The architecture is solid and maintainable. The main issue is **content overflow breaking layout** - always ensure flex containers have `min-width: 0` and images have `max-width: 100%`. When adding new colors, use existing CSS variables or add new ones to `app.css` - avoid hardcoding hex values in component CSS.
