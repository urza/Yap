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
    
    // Future: Sound notification functionality
    playNotificationSound: function() {
        // TODO: Implement sound notification
        // Will be added in future iteration
    },
    
    // Reset to original title
    reset: function() {
        this.updateTitle(0);
        this.unreadCount = 0;
    }
};