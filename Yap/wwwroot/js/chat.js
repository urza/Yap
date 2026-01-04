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
    // Use requestAnimationFrame to ensure DOM has updated
    requestAnimationFrame(() => {
        const element = document.querySelector('.messages');
        if (element) {
            element.scrollTop = element.scrollHeight;
        }
    });
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

// Detect touch/mobile device - Enter should not send on these
window.isTouchDevice = () => {
    return 'ontouchstart' in window || navigator.maxTouchPoints > 0;
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
