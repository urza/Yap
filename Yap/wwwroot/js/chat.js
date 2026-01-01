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
