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

    // Scroll immediately
    requestAnimationFrame(doScroll);

    // Re-scroll after short delays to catch lazy-loaded images
    // Images with loading="lazy" load asynchronously after initial render
    setTimeout(doScroll, 100);
    setTimeout(doScroll, 300);
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
            // Filter for image files only
            const imageFiles = Array.from(files).filter(f => f.type.startsWith('image/'));
            if (imageFiles.length > 0) {
                // Create a DataTransfer object and add the files
                const dt = new DataTransfer();
                imageFiles.forEach(f => dt.items.add(f));

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

        // Check for existing subscription
        let subscription = await registration.pushManager.getSubscription();

        if (!subscription) {
            // Convert VAPID key to Uint8Array
            const convertedKey = urlBase64ToUint8Array(vapidPublicKey);

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

        const imageFiles = [];
        for (const item of items) {
            if (item.type.startsWith('image/')) {
                const file = item.getAsFile();
                if (file) imageFiles.push(file);
            }
        }

        if (imageFiles.length === 0) return;

        // Prevent the default paste (don't paste image as text)
        e.preventDefault();

        const dt = new DataTransfer();
        imageFiles.forEach(f => dt.items.add(f));
        fileInput.files = dt.files;
        fileInput.dispatchEvent(new Event('change', { bubbles: true }));
    });
};

// ==========================================
// Parallel File Upload via HTTP
// ==========================================

// Upload multiple files in parallel via HTTP POST
// Returns array of { url, path } for successful uploads
window.uploadFilesParallel = async (fileInputId) => {
    const fileInput = document.getElementById(fileInputId);
    if (!fileInput || !fileInput.files || fileInput.files.length === 0) {
        return { success: false, error: 'No files selected' };
    }

    const files = Array.from(fileInput.files);
    const allowedExtensions = ['.jpg', '.jpeg', '.png', '.gif', '.webp'];
    const maxSize = 100 * 1024 * 1024; // 100MB

    // Filter valid files
    const validFiles = files.filter(f => {
        const ext = '.' + f.name.split('.').pop().toLowerCase();
        return allowedExtensions.includes(ext) && f.size <= maxSize;
    });

    if (validFiles.length === 0) {
        return { success: false, error: 'No valid image files' };
    }

    // Upload all files in parallel
    const uploadPromises = validFiles.map(async (file) => {
        const formData = new FormData();
        formData.append('file', file);

        try {
            const response = await fetch('/api/upload', {
                method: 'POST',
                body: formData
            });

            if (!response.ok) {
                const err = await response.json();
                console.error('[Upload] Failed:', file.name, err);
                return null;
            }

            return await response.json();
        } catch (e) {
            console.error('[Upload] Error:', file.name, e);
            return null;
        }
    });

    const results = await Promise.all(uploadPromises);
    const successful = results.filter(r => r !== null);

    // Clear the input for next upload
    fileInput.value = '';

    return {
        success: successful.length > 0,
        files: successful,
        totalCount: validFiles.length,
        successCount: successful.length
    };
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
