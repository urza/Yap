// Client locale detection (timezone + language)
window.getClientLocaleInfo = () => ({
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    locale: navigator.language
});

// Welcome page: attach click handler to #yap-enter element
// Supports optional .welcome-bg transition (grayscale → color) before navigating
window.setupWelcomePage = (dotNetRef) => {
    const el = document.getElementById('yap-enter');
    if (!el) return;

    el.style.cursor = 'pointer';
    const handleClick = () => {
        el.removeEventListener('click', handleClick);

        const bg = document.querySelector('.welcome-bg');
        if (bg) {
            el.classList.add('lit');
            bg.classList.add('awaken');
            setTimeout(() => dotNetRef.invokeMethodAsync('OnEnterClicked'), 2200);
        } else {
            dotNetRef.invokeMethodAsync('OnEnterClicked');
        }
    };
    el.addEventListener('click', handleClick);
};

// Tab notification helpers
let dotNetRef = null;
let notificationAudio = null;

// Pre-load audio on first user interaction
function ensureAudioLoaded() {
    if (!notificationAudio) {
        notificationAudio = new Audio('/notif.mp3');
        notificationAudio.volume = 0.5;
        notificationAudio.load();
    }
}

// Initialize audio on first user interaction (required by browsers)
document.addEventListener('click', ensureAudioLoaded, { once: true });
document.addEventListener('keydown', ensureAudioLoaded, { once: true });

window.setupVisibilityListener = (ref) => {
    dotNetRef = ref;
    ensureAudioLoaded();
    document.addEventListener('visibilitychange', () => {
        if (!dotNetRef) return;
        const visible = document.visibilityState === 'visible';
        dotNetRef.invokeMethodAsync('OnPageVisibilityChanged', visible);
    });
};

window.isPageVisible = () => document.visibilityState === 'visible';

window.setDocumentTitle = (title) => {
    document.title = title;
};

window.notifyNewMessage = (title) => {
    document.title = title;
    ensureAudioLoaded();
    if (notificationAudio) {
        notificationAudio.currentTime = 0;
        notificationAudio.play().catch(() => {});
    }
};

window.scrollToBottom = () => {
    const element = document.querySelector('.messages');
    if (!element) return;

    const doScroll = () => {
        element.scrollTop = element.scrollHeight;
    };

    // Two passes: now and after Blazor's render flushes. The freshly-added message
    // (e.g. a just-sent GIF) often hasn't hit the DOM yet on the first pass, so its
    // img wouldn't be in our pending-load list. Re-running querySelectorAll inside
    // a deferred callback catches it.
    const wireUpAndScroll = () => {
        doScroll();

        const pendingImgs = Array.from(element.querySelectorAll('img')).filter(img => !img.complete);
        const pendingMedia = Array.from(element.querySelectorAll('video, audio'))
            .filter(m => m.readyState < 1 /* HAVE_METADATA */);

        const total = pendingImgs.length + pendingMedia.length;
        if (total === 0) return;

        let remaining = total;
        const onSettled = () => {
            if (--remaining === 0) {
                requestAnimationFrame(doScroll);
            }
        };

        pendingImgs.forEach(img => {
            img.addEventListener('load', onSettled, { once: true });
            img.addEventListener('error', onSettled, { once: true });
        });
        pendingMedia.forEach(m => {
            let done = false;
            const fire = () => { if (!done) { done = true; onSettled(); } };
            m.addEventListener('loadedmetadata', fire, { once: true });
            m.addEventListener('error', fire, { once: true });
            // Safety: some browsers/elements never fire loadedmetadata
            // (e.g. cross-origin or stalled fetches). Don't wait forever.
            setTimeout(fire, 1500);
        });
    };

    // Pass 1: immediate (catches existing pending media).
    requestAnimationFrame(wireUpAndScroll);

    // Pass 2: short delay so Blazor's render queue has flushed and any just-added
    // message (with its img) is now in the DOM and gets a load-listener wired up.
    // 80ms is enough for one or two render passes without feeling laggy.
    setTimeout(wireUpAndScroll, 80);
};

// Check if user is scrolled near the bottom of messages
// Used to decide whether to auto-scroll after adding reactions
window.isNearBottom = (threshold = 100) => {
    const element = document.querySelector('.messages');
    if (!element) return true; // Default to true if no element

    const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
    return distanceFromBottom <= threshold;
};

// Image modal keyboard navigation
let modalKeyHandler = null;

window.setupModalKeyboard = (dotNetRef) => {
    // Remove any existing handler
    if (modalKeyHandler) {
        document.removeEventListener('keydown', modalKeyHandler);
    }

    modalKeyHandler = (e) => {
        if (e.key === 'Escape') {
            dotNetRef.invokeMethodAsync('CloseModalFromJs');
        } else if (e.key === 'ArrowRight') {
            dotNetRef.invokeMethodAsync('NextImageFromJs');
        } else if (e.key === 'ArrowLeft') {
            dotNetRef.invokeMethodAsync('PrevImageFromJs');
        }
    };

    document.addEventListener('keydown', modalKeyHandler);
};

window.removeModalKeyboard = () => {
    if (modalKeyHandler) {
        document.removeEventListener('keydown', modalKeyHandler);
        modalKeyHandler = null;
    }
};

// Drag-drop file handling
window.setupDropZone = (dropZoneElement, fileInputId) => {
    const fileInput = document.getElementById(fileInputId);
    if (!fileInput || !dropZoneElement) return;

    // Handle dragover on the whole document to detect when user is dragging files
    document.addEventListener('dragover', (e) => e.preventDefault());
    document.addEventListener('drop', (e) => e.preventDefault());

    // Handle file drop on drop zone
    dropZoneElement.addEventListener('drop', (e) => {
        e.preventDefault();
        // Don't stopPropagation - let Blazor's handler also fire to reset drag state

        const files = e.dataTransfer?.files;
        if (files && files.length > 0) {
            // Filter for image and video files
            const mediaFiles = Array.from(files).filter(f =>
                f.type.startsWith('image/') || f.type.startsWith('video/'));
            if (mediaFiles.length > 0) {
                // Create a DataTransfer object and add the files
                const dt = new DataTransfer();
                mediaFiles.forEach(f => dt.items.add(f));

                // Set the files on the input and trigger change
                fileInput.files = dt.files;
                fileInput.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }
    });
};

// Auto-resize textarea (Discord-style)
window.autoResizeTextarea = (id) => {
    requestAnimationFrame(() => {
        const textarea = document.getElementById(id);
        if (textarea) {
            textarea.style.height = 'auto';
            textarea.style.height = Math.min(textarea.scrollHeight, 200) + 'px';
        }
    });
};

window.resetTextareaHeight = (id) => {
    const textarea = document.getElementById(id);
    if (textarea) {
        textarea.style.height = '44px'; // Reset to min-height, avoids scrollbar flash
    }
};

// Insert text at cursor position in textarea (for emoji picker)
window.insertTextAtCursor = (textareaId, text) => {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;

    textarea.focus();

    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const value = textarea.value;

    // Splice text in at cursor position
    textarea.value = value.substring(0, start) + text + value.substring(end);

    // Move cursor to after inserted text
    const newPos = start + text.length;
    textarea.selectionStart = newPos;
    textarea.selectionEnd = newPos;

    // Dispatch input event so Blazor's @bind picks up the change
    textarea.dispatchEvent(new Event('input', { bubbles: true }));

    // Auto-resize textarea
    window.autoResizeTextarea(textareaId);
};

// Emoji toggle button: randomize which color emoji appears on each hover
// Listens on the button (parent) to avoid re-triggering when moving within it
window.setupEmojiToggle = (iconId, cols, totalEmojis) => {
    const icon = document.getElementById(iconId);
    if (!icon) return;
    const button = icon.parentElement;
    if (!button) return;

    button.addEventListener('mouseenter', () => {
        // Pick one random emoji — set on both color and grey
        // Color shows immediately (hover), grey shows when you leave (same emoji, greyed out)
        const index = Math.floor(Math.random() * totalEmojis);
        const col = index % cols;
        const row = Math.floor(index / cols);
        icon.style.setProperty('--color-col', col);
        icon.style.setProperty('--color-row', row);
        icon.style.setProperty('--grey-col', col);
        icon.style.setProperty('--grey-row', row);
    });
};

// Detect touch/mobile device - Enter should not send on these
window.isTouchDevice = () => {
    return 'ontouchstart' in window || navigator.maxTouchPoints > 0;
};

// Watch viewport for the mobile breakpoint and notify Blazor when it changes.
// Returns the initial match value so the caller can set state on first render.
const _mobileLayoutWatchers = new Map(); // id -> { mq, handler }

window.setupMobileLayoutWatcher = (dotnetRef, id) => {
    const mq = window.matchMedia('(max-width: 600px)');
    const handler = (e) => {
        try { dotnetRef.invokeMethodAsync('OnMobileLayoutChanged', e.matches); }
        catch { /* circuit may be gone */ }
    };
    if (mq.addEventListener) mq.addEventListener('change', handler);
    else mq.addListener(handler);
    // Track per MessageInput instance so DisposeAsync can detach it — otherwise every page
    // navigation leaks a matchMedia handler that later fires on a disposed DotNetObjectReference.
    _mobileLayoutWatchers.set(id, { mq, handler });
    return mq.matches;
};

window.teardownMobileLayoutWatcher = (id) => {
    const w = _mobileLayoutWatchers.get(id);
    if (!w) return;
    if (w.mq.removeEventListener) w.mq.removeEventListener('change', w.handler);
    else w.mq.removeListener(w.handler);
    _mobileLayoutWatchers.delete(id);
};

// Prevent textarea from losing focus when send button is tapped (keeps mobile keyboard open)
window.setupSendButtonFocus = (textareaId) => {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;
    const container = textarea.closest('.message-input-container');
    if (!container) return;
    const sendBtn = container.querySelector('.send-button');
    if (!sendBtn) return;

    sendBtn.addEventListener('mousedown', (e) => {
        e.preventDefault();
    });
};

// Prevent Enter from inserting newline (handled server-side for sending)
// This runs client-side to avoid race conditions with server-side preventDefault
window.setupEnterKeyHandler = (textareaId) => {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;

    // Remove any existing handler to avoid duplicates
    if (textarea._enterKeyHandler) {
        textarea.removeEventListener('keydown', textarea._enterKeyHandler);
    }

    const isTouch = window.isTouchDevice();

    textarea._enterKeyHandler = (e) => {
        // On touch devices, Enter creates newline (use send button)
        // On desktop, Enter sends message (prevent newline), Shift+Enter for newline
        if (e.key === 'Enter' && !e.shiftKey && !isTouch) {
            e.preventDefault();
        }
    };

    textarea.addEventListener('keydown', textarea._enterKeyHandler);
};

// PWA Badge API for unread notifications
window.setAppBadge = async (count) => {
    if ('setAppBadge' in navigator) {
        try {
            // iOS requires notification permission for badges (only prompt inside PWA)
            if (window.isPwaInstalled() && 'Notification' in window && Notification.permission === 'default') {
                await Notification.requestPermission();
            }

            if (count > 0) {
                await navigator.setAppBadge(count);
            } else {
                await navigator.clearAppBadge();
            }
            return true;
        } catch (e) {
            console.warn('[PWA] Badge update failed:', e);
            return false;
        }
    }
    return false;
};

window.clearAppBadge = async () => {
    if ('clearAppBadge' in navigator) {
        try {
            await navigator.clearAppBadge();
            return true;
        } catch (e) {
            console.warn('[PWA] Badge clear failed:', e);
            return false;
        }
    }
    return false;
};

// Check if Badge API is supported
window.isBadgeSupported = () => {
    return 'setAppBadge' in navigator;
};

// ==========================================
// Push Notification Subscription
// ==========================================

// Check if push is supported
window.isPushSupported = () => {
    return 'PushManager' in window && 'serviceWorker' in navigator;
};

// Get current notification permission
window.getNotificationPermission = () => {
    if (!('Notification' in window)) return 'unsupported';
    return Notification.permission;
};

// Request notification permission
window.requestNotificationPermission = async () => {
    if (!('Notification' in window)) return 'unsupported';
    try {
        const result = await Notification.requestPermission();
        console.log('[Push] Permission result:', result);
        return result;
    } catch (e) {
        console.error('[Push] Permission request failed:', e);
        return 'error';
    }
};

// Check if app is installed as PWA
window.isPwaInstalled = () => {
    return window.matchMedia('(display-mode: standalone)').matches ||
           window.navigator.standalone === true;
};

// PWA last-route persistence (resume where you left off)
window.saveLastRoute = (route) => {
    localStorage.setItem('yap-last-route', route);
};

window.getLastPwaRoute = () => {
    if (!window.isPwaInstalled()) return null;
    return localStorage.getItem('yap-last-route');
};

// PWA Install Banner helpers
window.isMessageInputFocused = () => {
    return document.activeElement?.classList.contains('message-input') === true;
};

window.shouldShowPwaInstallBanner = () => {
    if (window.isPwaInstalled()) return false;
    if (sessionStorage.getItem('pwa-banner-dismissed')) return false;
    return true;
};

window.dismissPwaInstallBanner = () => {
    sessionStorage.setItem('pwa-banner-dismissed', 'true');
};

// Push permission prompt (full-page overlay for PWA users)
window.shouldShowPushPermissionPrompt = () => {
    if (!window.isPwaInstalled()) return false;
    if (!window.isPushSupported()) return false;
    // Already granted or denied — no point showing
    if ('Notification' in window && Notification.permission !== 'default') return false;
    // Dismissed 3+ times — stop asking
    const dismissCount = parseInt(localStorage.getItem('push-prompt-dismiss-count') || '0');
    if (dismissCount >= 3) return false;
    return true;
};

window.dismissPushPermissionPrompt = () => {
    const count = parseInt(localStorage.getItem('push-prompt-dismiss-count') || '0');
    localStorage.setItem('push-prompt-dismiss-count', String(count + 1));
};

// Submit signin via hidden POST form (avoids password in URL)
window.submitSigninForm = (username, password, returnUrl) => {
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = '/auth/signin';
    form.style.display = 'none';

    const addField = (name, value) => {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = name;
        input.value = value;
        form.appendChild(input);
    };

    addField('username', username);
    addField('password', password);
    addField('returnUrl', returnUrl);

    document.body.appendChild(form);
    form.submit();
};

// Capture native install prompt (Chrome/Edge on desktop & Android)
let _deferredInstallPrompt = null;
window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    _deferredInstallPrompt = e;
});

window.showPwaInstallGuide = async () => {
    sessionStorage.setItem('pwa-banner-dismissed', 'true');

    // Use native prompt if available (desktop Chrome/Edge, Android Chrome)
    if (_deferredInstallPrompt) {
        _deferredInstallPrompt.prompt();
        const result = await _deferredInstallPrompt.userChoice;
        console.log('[PWA] Install prompt result:', result.outcome);
        _deferredInstallPrompt = null;
        return;
    }

    // Fallback: show add-to-homescreen guide (iOS Safari, etc.)
    const cdnBase = 'https://cdn.jsdelivr.net/gh/philfung/add-to-homescreen@3.5/dist';

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = cdnBase + '/add-to-homescreen.min.css';
    document.head.appendChild(link);

    const script = document.createElement('script');
    script.src = cdnBase + '/add-to-homescreen.min.js';
    script.onload = () => {
        if (window.AddToHomeScreen) {
            const instance = window.AddToHomeScreen({
                appName: 'Yap',
                appIconUrl: 'icon-192.png',
                assetUrl: cdnBase + '/assets/img/',
                allowClose: false,
                showArrow: true
            });
            instance.show('en');
        }
    };
    document.body.appendChild(script);
};

// Subscribe to push notifications
window.subscribeToPush = async (vapidPublicKey) => {
    if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
        console.warn('[Push] Push not supported');
        return null;
    }

    try {
        const registration = await navigator.serviceWorker.ready;
        const convertedKey = urlBase64ToUint8Array(vapidPublicKey);

        // Check for existing subscription
        let subscription = await registration.pushManager.getSubscription();

        // If an existing subscription was created with a DIFFERENT applicationServerKey (e.g. the
        // server's VAPID key was rotated/fixed), it can never receive pushes signed by the new key.
        // Drop it and re-subscribe so users migrate automatically with no action on their part.
        if (subscription && !applicationServerKeyMatches(subscription, convertedKey)) {
            console.log('[Push] VAPID key changed — re-subscribing with the new key');
            try { await subscription.unsubscribe(); } catch (e) { console.warn('[Push] old unsubscribe failed', e); }
            subscription = null;
        }

        if (!subscription) {
            subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: convertedKey
            });
            console.log('[Push] New subscription created');
        } else {
            console.log('[Push] Using existing subscription');
        }

        // Return subscription as JSON string
        const subJson = subscription.toJSON();
        return JSON.stringify({
            endpoint: subJson.endpoint,
            p256dh: subJson.keys.p256dh,
            auth: subJson.keys.auth
        });
    } catch (e) {
        console.error('[Push] Subscription failed:', e);
        return null;
    }
};

// Unsubscribe from push notifications
window.unsubscribeFromPush = async () => {
    try {
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();

        if (subscription) {
            await subscription.unsubscribe();
            console.log('[Push] Unsubscribed');
            return true;
        }
        return false;
    } catch (e) {
        console.error('[Push] Unsubscribe failed:', e);
        return false;
    }
};

// Get current push subscription
window.getPushSubscription = async () => {
    try {
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();

        if (subscription) {
            const subJson = subscription.toJSON();
            return JSON.stringify({
                endpoint: subJson.endpoint,
                p256dh: subJson.keys.p256dh,
                auth: subJson.keys.auth
            });
        }
        return null;
    } catch (e) {
        console.error('[Push] Get subscription failed:', e);
        return null;
    }
};

// Helper: true if the subscription was created with the given applicationServerKey.
// If the browser doesn't expose options.applicationServerKey, assume a match so we never
// churn a working subscription on a browser that simply can't tell us what key it used.
function applicationServerKeyMatches(subscription, expectedKeyBytes) {
    try {
        const current = subscription.options && subscription.options.applicationServerKey;
        if (!current) return true;
        const actual = new Uint8Array(current);
        if (actual.length !== expectedKeyBytes.length) return false;
        for (let i = 0; i < actual.length; i++) {
            if (actual[i] !== expectedKeyBytes[i]) return false;
        }
        return true;
    } catch (e) {
        return true;
    }
}

// Helper: Convert VAPID key to Uint8Array
function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding)
        .replace(/-/g, '+')
        .replace(/_/g, '/');

    const rawData = window.atob(base64);
    const outputArray = new Uint8Array(rawData.length);

    for (let i = 0; i < rawData.length; ++i) {
        outputArray[i] = rawData.charCodeAt(i);
    }
    return outputArray;
}

// Listen for notification clicks from service worker
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.addEventListener('message', (event) => {
        if (event.data?.type === 'NOTIFICATION_CLICK' && event.data?.url) {
            // Navigate to the URL from notification
            window.location.href = event.data.url;
        }
    });
}

// ==========================================
// Emoji Picker Positioning
// ==========================================

// Position emoji picker as a fixed overlay near the anchor button, clamped to viewport.
// On mobile (<=600px), CSS handles bottom-sheet positioning so we skip JS.
window.positionEmojiPickerFixed = (wrapper, anchor) => {
    if (!wrapper || !anchor) return;

    // On mobile, CSS bottom-sheet handles positioning
    if (window.innerWidth <= 600) return;

    const anchorRect = anchor.getBoundingClientRect();
    const pickerWidth = 340;
    const pickerHeight = 384;
    const margin = 4;

    // Default: below anchor, right-aligned with anchor's right edge
    let top = anchorRect.bottom + margin;
    let left = anchorRect.right - pickerWidth;

    // If not enough space below, position above the anchor
    if (top + pickerHeight > window.innerHeight - margin) {
        top = anchorRect.top - pickerHeight - margin;
    }

    // Clamp to viewport edges
    if (top < margin) top = margin;
    if (left < margin) left = margin;
    if (left + pickerWidth > window.innerWidth - margin) {
        left = window.innerWidth - pickerWidth - margin;
    }
    if (top + pickerHeight > window.innerHeight - margin) {
        top = window.innerHeight - pickerHeight - margin;
    }

    wrapper.style.top = top + 'px';
    wrapper.style.left = left + 'px';
};

// Initialize scroll-tracking for emoji picker sidebar highlights.
// Returns an object with dispose() to clean up the scroll listener.
window.initEmojiPickerScroll = (contentElement) => {
    if (!contentElement) return { dispose: () => {} };

    const picker = contentElement.closest('.emoji-picker');
    if (!picker) return { dispose: () => {} };

    const sidebar = picker.querySelector('.emoji-sidebar');
    if (!sidebar) return { dispose: () => {} };

    const highlightActive = () => {
        const sections = contentElement.querySelectorAll('.emoji-section');
        let activeKey = null;

        // Find the section whose top is at or above the container's scroll position
        for (const section of sections) {
            if (section.offsetTop <= contentElement.scrollTop + 8) {
                activeKey = section.getAttribute('data-section');
            }
        }

        // Fallback to first section if nothing matched
        if (!activeKey && sections.length > 0) {
            activeKey = sections[0].getAttribute('data-section');
        }

        // Toggle .active on matching sidebar button
        for (const btn of sidebar.querySelectorAll('.category-btn')) {
            btn.classList.toggle('active', btn.getAttribute('data-category') === activeKey);
        }
    };

    // Run initial highlight
    highlightActive();

    const onScroll = () => highlightActive();
    contentElement.addEventListener('scroll', onScroll, { passive: true });

    return {
        dispose: () => {
            contentElement.removeEventListener('scroll', onScroll);
        }
    };
};

// Client-side click handler for emoji picker — inserts emoji into textarea instantly
// without waiting for Blazor server round-trip. Blazor @onclick still fires for bookkeeping.
// Note: intentionally does NOT focus the textarea — on mobile, focus() within a user gesture
// triggers the keyboard, which we don't want while the emoji picker is open.
// Document-level event delegation for emoji picker clicks.
// Survives Blazor DOM replacements — the handler is on document, not the picker element.
let _emojiTextareaId = null;
let _emojiCursorPos = null;
let _emojiClickSetup = false;

let _emojiDotNetRef = null;

window.setupEmojiPickerClick = (pickerElement, textareaId, dotNetRef) => {
    _emojiTextareaId = textareaId;
    _emojiCursorPos = null;
    _emojiDotNetRef = dotNetRef || null;

    if (_emojiClickSetup) return;
    _emojiClickSetup = true;

    // Blur search input on pointerdown to dismiss mobile keyboard early,
    // so the click event fires without keyboard dismissal delay.
    document.addEventListener('pointerdown', (e) => {
        const btn = e.target.closest('.emoji-picker .emoji-btn[data-emoji]');
        if (btn) {
            const searchInput = btn.closest('.emoji-picker')?.querySelector('.emoji-search input');
            if (searchInput && document.activeElement === searchInput) {
                searchInput.blur();
            }
        }
    });

    // Track cursor position ourselves — Blazor re-renders reset selectionStart to 0
    // when the textarea doesn't have focus.
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.emoji-picker .emoji-btn[data-emoji]');
        if (!btn) return;

        const emoji = btn.getAttribute('data-emoji');
        if (!emoji || !_emojiTextareaId) return;

        const textarea = document.getElementById(_emojiTextareaId);
        if (!textarea) return;

        const pos = _emojiCursorPos !== null ? _emojiCursorPos : textarea.value.length;
        const value = textarea.value;

        textarea.value = value.substring(0, pos) + emoji + value.substring(pos);

        const newPos = pos + emoji.length;
        textarea.selectionStart = newPos;
        textarea.selectionEnd = newPos;
        _emojiCursorPos = newPos;

        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        window.autoResizeTextarea(_emojiTextareaId);

        // Fire-and-forget bookkeeping (recents + counts). Avoids a second
        // server round-trip via Blazor @onclick that would also force a
        // parent re-render of the open picker.
        if (_emojiDotNetRef) {
            _emojiDotNetRef.invokeMethodAsync('RecordEmojiUsed', emoji).catch(() => { });
        }
    });
};

// Client-side textarea auto-resize on input. Replaces the server-side
// JS.InvokeVoidAsync("autoResizeTextarea") round-trip that used to fire on
// every keystroke.
window.setupTextareaAutoResize = (textareaId) => {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;
    if (textarea._autoResizeHandler) {
        textarea.removeEventListener('input', textarea._autoResizeHandler);
    }
    textarea._autoResizeHandler = () => window.autoResizeTextarea(textareaId);
    textarea.addEventListener('input', textarea._autoResizeHandler);
};

// Smooth-scroll emoji picker content to a specific category section.
window.scrollEmojiPickerToSection = (contentElement, categoryKey) => {
    if (!contentElement) return;

    const section = contentElement.querySelector(`.emoji-section[data-section="${categoryKey}"]`);
    if (section) {
        section.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
};

// ==========================================
// Scroll-to-Dismiss (Mobile message actions)
// Uses pure CSS class toggling - no Blazor callbacks needed
// ==========================================

let scrollWatchActive = false;
let scrollStartTop = 0;
let scrollDismissHandler = null;
const SCROLL_DISMISS_THRESHOLD = 50; // pixels

window.setupScrollDismiss = () => {
    const messagesEl = document.querySelector('.messages');
    if (!messagesEl) return;

    // Remove existing handler if any
    if (scrollDismissHandler) {
        messagesEl.removeEventListener('scroll', scrollDismissHandler);
    }

    scrollDismissHandler = () => {
        if (!scrollWatchActive) return;

        const delta = Math.abs(messagesEl.scrollTop - scrollStartTop);
        if (delta > SCROLL_DISMISS_THRESHOLD) {
            scrollWatchActive = false;
            // Add class to hide all message action popups via CSS
            messagesEl.classList.add('scroll-dismissing');
        }
    };

    messagesEl.addEventListener('scroll', scrollDismissHandler, { passive: true });
};

window.activateScrollWatch = () => {
    const messagesEl = document.querySelector('.messages');
    if (messagesEl) {
        // Remove dismiss class when user taps a message
        messagesEl.classList.remove('scroll-dismissing');
        scrollStartTop = messagesEl.scrollTop;
        scrollWatchActive = true;
    }
};

window.cleanupScrollDismiss = () => {
    const messagesEl = document.querySelector('.messages');
    if (messagesEl && scrollDismissHandler) {
        messagesEl.removeEventListener('scroll', scrollDismissHandler);
        messagesEl.classList.remove('scroll-dismissing');
    }
    scrollDismissHandler = null;
    scrollWatchActive = false;
};

// Focus message input (used after clicking Reply)
window.focusMessageInput = () => {
    const el = document.querySelector('.message-input');
    if (el) el.focus();
};

// Focus input on reply button click, in the user gesture context.
// Uses capture phase so it fires before Blazor's bubbling-phase handler,
// keeping focus in the same gesture without interfering with Blazor's click processing.
document.addEventListener('click', (e) => {
    if (e.target.closest('.action-reply')) {
        const el = document.querySelector('.message-input');
        if (el) el.focus();
    }
}, true);


// ==========================================
// Scroll to Message (Reply click)
// ==========================================

window.scrollToMessage = (messageId) => {
    const el = document.getElementById('msg-' + messageId);
    if (!el) return;

    const container = document.querySelector('.messages');
    if (!container) return;

    // Target: center the element in the container
    const targetTop = el.offsetTop - container.offsetTop - (container.clientHeight / 2) + (el.offsetHeight / 2);
    const startTop = container.scrollTop;
    const distance = targetTop - startTop;
    const duration = 600; // ms — fixed duration regardless of distance
    const startTime = performance.now();

    // Ease-out cubic for a natural deceleration feel
    const ease = (t) => 1 - Math.pow(1 - t, 3);

    const step = (now) => {
        const elapsed = Math.min((now - startTime) / duration, 1);
        container.scrollTop = startTop + distance * ease(elapsed);
        if (elapsed < 1) {
            requestAnimationFrame(step);
        } else {
            // Highlight with blurple tint after scroll completes
            el.classList.add('highlight-message');
            setTimeout(() => el.classList.remove('highlight-message'), 2000);
        }
    };

    requestAnimationFrame(step);
};

// ==========================================
// Clipboard Paste Upload
// ==========================================

window.setupPasteUpload = (textareaId, fileInputId) => {
    const textarea = document.getElementById(textareaId);
    const fileInput = document.getElementById(fileInputId);
    if (!textarea || !fileInput) return;

    textarea.addEventListener('paste', (e) => {
        const items = e.clipboardData?.items;
        if (!items) return;

        const mediaFiles = [];
        for (const item of items) {
            if (item.type.startsWith('image/') || item.type.startsWith('video/')) {
                const file = item.getAsFile();
                if (file) mediaFiles.push(file);
            }
        }

        if (mediaFiles.length === 0) return;

        // Prevent the default paste (don't paste media as text)
        e.preventDefault();

        const dt = new DataTransfer();
        mediaFiles.forEach(f => dt.items.add(f));
        fileInput.files = dt.files;
        fileInput.dispatchEvent(new Event('change', { bubbles: true }));
    });
};

// ==========================================
// Parallel File Upload via HTTP
// ==========================================

// Resumable file upload via tus.io protocol
// Returns same shape as old uploadFilesParallel: { success, error, files, totalCount, successCount }
window.uploadFilesWithTus = async (fileInputId, maxSizeMB, tusEndpoint, dotNetRef) => {
    const fileInput = document.getElementById(fileInputId);
    if (!fileInput || !fileInput.files || fileInput.files.length === 0) {
        return { success: false, error: 'No files selected' };
    }

    maxSizeMB = maxSizeMB || 100;
    tusEndpoint = tusEndpoint || '/api/tus';
    const files = Array.from(fileInput.files);
    const allowedExtensions = ['.jpg', '.jpeg', '.png', '.gif', '.webp', '.mp4', '.webm', '.mov', '.avi', '.mkv'];
    const maxSize = maxSizeMB * 1024 * 1024;

    // Check each file and collect rejection reasons
    const validFiles = [];
    const rejections = [];
    for (const f of files) {
        const ext = '.' + f.name.split('.').pop().toLowerCase();
        const isAllowedExt = allowedExtensions.includes(ext);
        const isAllowedMime = f.type.startsWith('image/') || f.type.startsWith('video/');

        if (!isAllowedExt && !isAllowedMime) {
            rejections.push(`"${f.name}" — unsupported file type`);
        } else if (f.size > maxSize) {
            const sizeMB = (f.size / 1024 / 1024).toFixed(0);
            rejections.push(`"${f.name}" — too large (${sizeMB} MB, max ${maxSizeMB} MB)`);
        } else {
            validFiles.push(f);
        }
    }

    if (validFiles.length === 0) {
        return { success: false, error: rejections.join('\n') || 'No valid files selected' };
    }

    // Track aggregate progress across all files
    const totalBytes = validFiles.reduce((sum, f) => sum + f.size, 0);
    const fileProgress = new Array(validFiles.length).fill(0);
    let completedFiles = 0;

    const updateProgress = () => {
        const uploadedBytes = fileProgress.reduce((sum, b) => sum + b, 0);
        const percent = totalBytes > 0 ? Math.round((uploadedBytes / totalBytes) * 100) : 0;
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnUploadProgress', percent, completedFiles, validFiles.length)
                .catch(() => {}); // ignore if circuit is dead
        }
    };

    // Upload all files via tus (parallel — browser limits concurrency naturally)
    const errors = [...rejections];
    const uploadPromises = validFiles.map((file, index) => {
        return new Promise((resolve) => {
            const upload = new tus.Upload(file, {
                endpoint: tusEndpoint,
                chunkSize: 5 * 1024 * 1024, // 5 MB chunks
                retryDelays: [0, 1000, 3000, 5000],
                withCredentials: true, // send cookies cross-origin
                metadata: {
                    filename: file.name,
                    filetype: file.type
                },
                onProgress: (bytesUploaded, bytesTotal) => {
                    fileProgress[index] = bytesUploaded;
                    updateProgress();
                },
                onSuccess: async () => {
                    completedFiles++;
                    fileProgress[index] = file.size;
                    updateProgress();

                    // Extract file ID from tus upload URL
                    const fileId = upload.url.split('/').pop();
                    console.log('[Tus] Upload complete:', file.name, 'fileId=' + fileId, 'url=' + upload.url);
                    try {
                        // Fetch the processed file info from server
                        // Server may still be processing (thumbnails/posters), retry briefly
                        let info = null;
                        const infoBaseUrl = upload.url.substring(0, upload.url.lastIndexOf('/'));
                        const infoUrl = infoBaseUrl + '/info/' + fileId;
                        console.log('[Tus] Fetching info from:', infoUrl);
                        for (let attempt = 0; attempt < 30; attempt++) {
                            const resp = await fetch(infoUrl, { credentials: 'include' });
                            console.log('[Tus] Info attempt', attempt + 1, 'status:', resp.status);
                            if (resp.ok) {
                                info = await resp.json();
                                break;
                            }
                            // Not ready yet, wait and retry
                            await new Promise(r => setTimeout(r, 1000));
                        }
                        if (info) {
                            console.log('[Tus] Got file info:', info);
                            resolve(info);
                        } else {
                            errors.push(`"${file.name}" — server processing timeout`);
                            resolve(null);
                        }
                    } catch (e) {
                        errors.push(`"${file.name}" — failed to get upload result`);
                        resolve(null);
                    }
                },
                onError: (error) => {
                    const msg = error.message || 'Upload failed';
                    errors.push(`"${file.name}" — ${msg}`);
                    resolve(null);
                }
            });

            upload.start();
        });
    });

    const results = await Promise.all(uploadPromises);
    const successful = results.filter(r => r !== null);

    // Clear the input for next upload
    fileInput.value = '';

    return {
        success: successful.length > 0,
        error: errors.length > 0 ? errors.join('\n') : null,
        files: successful,
        totalCount: files.length,
        successCount: successful.length
    };
};

// ==========================================
// Video Player Controls
// ==========================================

window.playVideo = (containerElement) => {
    if (!containerElement) return;
    const video = containerElement.querySelector('video');
    const overlay = containerElement.querySelector('.video-play-overlay');
    if (!video) return;

    video.controls = true;
    if (overlay) overlay.style.display = 'none';
    video.play().catch(() => {});

    // Show overlay again when video ends or pauses
    const showOverlay = () => {
        if (overlay) overlay.style.display = '';
        video.controls = false;
    };
    video.addEventListener('ended', showOverlay, { once: true });
    video.addEventListener('pause', () => {
        // Only show overlay if video is not seeking (user might be scrubbing)
        if (video.ended || video.currentTime === 0) {
            showOverlay();
        }
    }, { once: true });
};

// ==========================================
// Infinite Scroll Support
// ==========================================

let infiniteScrollRef = null;
let scrollHandler = null;
const SCROLL_THRESHOLD = 100; // pixels from top to trigger load

// Get current scroll height for position restoration
window.getScrollHeight = () => {
    const el = document.querySelector('.messages');
    return el ? el.scrollHeight : 0;
};

// Restore scroll position after prepending content
window.restoreScrollPosition = (previousScrollHeight) => {
    requestAnimationFrame(() => {
        const el = document.querySelector('.messages');
        if (!el) return;

        // New content was added at top, so scrollHeight increased
        // Adjust scrollTop by the difference to maintain visual position
        const heightDiff = el.scrollHeight - previousScrollHeight;
        el.scrollTop += heightDiff;
    });
};

// Setup infinite scroll detection
window.setupInfiniteScroll = (dotNetRef) => {
    infiniteScrollRef = dotNetRef;
    const messagesElement = document.querySelector('.messages');

    if (!messagesElement) return;

    let isLoading = false;

    scrollHandler = () => {
        if (isLoading) return;

        // Check if scrolled near top
        if (messagesElement.scrollTop <= SCROLL_THRESHOLD) {
            isLoading = true;
            infiniteScrollRef.invokeMethodAsync('OnScrollNearTop')
                .finally(() => {
                    // Small delay before allowing next load
                    setTimeout(() => { isLoading = false; }, 200);
                });
        }

        // Debounce version (if needed):
        // clearTimeout(debounceTimer);
        // debounceTimer = setTimeout(() => { ... }, 50);
    };

    messagesElement.addEventListener('scroll', scrollHandler, { passive: true });
};

// Cleanup infinite scroll
window.cleanupInfiniteScroll = () => {
    const messagesElement = document.querySelector('.messages');
    if (messagesElement && scrollHandler) {
        messagesElement.removeEventListener('scroll', scrollHandler);
    }
    scrollHandler = null;
    infiniteScrollRef = null;
};

// ==========================================
// Theme switching
// ==========================================

window.applyTheme = (themeId) => {
    document.documentElement.dataset.theme = themeId || 'discord-dark';
};

// ==========================================
// GIF (MP4) message autoplay — canplay-based
// ==========================================
// Why this exists: Blazor Server's prerender → hydrate cycle can race the browser's autoplay
// decision (see dotnet/aspnetcore#59415). Rather than rely on the `autoplay` attribute surviving
// hydration, we listen for the native `canplay` event in the capturing phase on the document.
// That event fires once the browser has buffered enough data to start playing, regardless of
// when the <video> element was added to the DOM or whether Blazor patched it. We then call .play(),
// which works for muted+playsinline videos under every browser's autoplay policy.
//
// If a video's play() ever rejects with NotAllowedError (rare for muted videos but possible under
// strict policy), we install a one-shot pointerdown/keydown listener so the next user gesture
// anywhere unblocks every paused .gif-message-video on the page.
(function () {
    if (window.__gifAutoplayWired) return;
    window.__gifAutoplayWired = true;

    // Both chat-message gifs and picker-grid previews share the same canplay-driven autoplay.
    const GIF_VIDEO_SELECTOR = '.gif-message-video, .gif-card-video';

    const isGifVideo = (v) =>
        v && v.tagName === 'VIDEO' && v.classList &&
        (v.classList.contains('gif-message-video') || v.classList.contains('gif-card-video'));

    const tryPlay = (v) => {
        if (!isGifVideo(v) || !v.paused) return;
        v.play().catch(err => {
            if (err && err.name === 'NotAllowedError') installClickUnlock();
            else console.warn(`[gif autoplay] ${err.name}: ${err.message} — ${v.currentSrc}`);
        });
    };

    function installClickUnlock() {
        if (window.__gifKickPending) return;
        window.__gifKickPending = true;
        const unlock = () => {
            window.__gifKickPending = false;
            document.removeEventListener('pointerdown', unlock, true);
            document.removeEventListener('keydown', unlock, true);
            document.querySelectorAll(GIF_VIDEO_SELECTOR).forEach(v => {
                if (v.paused) v.play().catch(() => {});
            });
        };
        document.addEventListener('pointerdown', unlock, true);
        document.addEventListener('keydown', unlock, true);
    }

    // Drop the loading spinner once a chat GIF can paint. The CSS spins until we add
    // .gif-loaded to the .gif-message box. Covers <img> (animated webp/gif) via `load` and
    // the legacy <video> fallback via `canplay`; `error` also clears it so a broken URL never
    // spins forever. closest('.gif-message') returns null for every other image/video on the
    // page (avatars, emojis, gallery images, picker previews), so this is a cheap no-op for them.
    const markGifLoaded = (el) => {
        const box = el && el.closest && el.closest('.gif-message');
        if (box) box.classList.add('gif-loaded');
    };

    // Catches every <video> reaching the canplay state — initial-render, freshly inserted, or
    // hydrated. Capturing-phase listener ensures we see the event regardless of bubbling.
    document.addEventListener('canplay', e => { tryPlay(e.target); markGifLoaded(e.target); }, true);
    // `load`/`error` don't bubble, so capture them at the document to catch <img> regardless of
    // when Blazor inserted or patched the element.
    document.addEventListener('load', e => markGifLoaded(e.target), true);
    document.addEventListener('error', e => markGifLoaded(e.target), true);
})();
