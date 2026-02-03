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
        if (document.visibilityState === 'visible' && dotNetRef) {
            dotNetRef.invokeMethodAsync('OnPageBecameVisible');
        }
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

// Detect touch/mobile device - Enter should not send on these
window.isTouchDevice = () => {
    return 'ontouchstart' in window || navigator.maxTouchPoints > 0;
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
            // iOS requires notification permission for badges
            if ('Notification' in window && Notification.permission === 'default') {
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

// Check if emoji picker would overflow viewport bottom and should flip upward
window.checkEmojiPickerPosition = (element) => {
    if (!element) return false;

    const rect = element.getBoundingClientRect();
    const pickerHeight = 350; // Approximate height of emoji picker
    const viewportHeight = window.innerHeight;

    const spaceBelow = viewportHeight - rect.bottom;
    const spaceAbove = rect.top;

    // Only flip if: not enough space below AND more space above than below
    // This prevents always-flipped on mobile where neither direction has full space
    return spaceBelow < pickerHeight && spaceAbove > spaceBelow;
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
