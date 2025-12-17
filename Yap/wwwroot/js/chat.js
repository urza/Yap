// Tab notification helpers
let dotNetRef = null;

window.setupVisibilityListener = (ref) => {
    dotNetRef = ref;
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible' && dotNetRef) {
            dotNetRef.invokeMethodAsync('OnPageBecameVisible');
        }
    });
};

window.isPageVisible = () => document.visibilityState === 'visible';

window.playNotificationSound = () => {
    const audio = new Audio('/notif.mp3');
    audio.volume = 0.5;
    audio.play().catch(() => {});
};

window.scrollToBottom = (element) => {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
};
