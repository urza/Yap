window.scrollToBottom = (element) => {
    element.scrollTop = element.scrollHeight;
};

// Tab title and notification management
window.tabNotifications = {
    originalTitle: null,
    unreadCount: 0,
    
    // Initialize with current page title
    initialize: function() {
        if (this.originalTitle === null) {
            this.originalTitle = document.title || "BlazorChat";
        }
    },
    
    // Update the browser tab title with unread count
    updateTitle: function(count) {
        this.initialize(); // Ensure we have the original title
        if (count > 0) {
            document.title = `(${count}) ${this.originalTitle}`;
        } else {
            document.title = this.originalTitle;
        }
        this.unreadCount = count;
    },
    
    // Check if the page is currently visible/active
    isPageVisible: function() {
        return document.visibilityState === 'visible';
    },
    
    // Setup page visibility change listener
    setupVisibilityListener: function(dotNetRef) {
        // Capture the title at setup time (after Blazor has set it)
        this.originalTitle = document.title;
        
        document.addEventListener('visibilitychange', () => {
            if (this.isPageVisible()) {
                // Page became visible - reset unread count
                dotNetRef.invokeMethodAsync('OnPageBecameVisible');
            }
        });
    },
    
    // Sound notification functionality
    playNotificationSound: function() {
        try {
            console.log('Attempting to play notification sound...');
            const audio = new Audio('/notif.mp3');
            audio.volume = 0.5; // 50% volume
            
            // Handle autoplay policy restrictions
            const playPromise = audio.play();
            if (playPromise !== undefined) {
                playPromise
                    .then(() => {
                        console.log('Notification sound played successfully');
                    })
                    .catch(error => {
                        // Auto-play was prevented
                        console.log('Notification sound blocked by browser:', error.name, error.message);
                    });
            }
        } catch (error) {
            console.log('Error creating audio element:', error);
        }
    },
    
    // Reset to original title
    reset: function() {
        this.updateTitle(0);
        this.unreadCount = 0;
    }
};