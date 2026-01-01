// =============================================================================
// BLAZOR RECONNECTION HANDLER (.NET 10)
// =============================================================================
// This script handles WebSocket reconnection when the connection to the server
// is lost. It's designed to be aggressive about reconnecting and resuming
// the user's session automatically.
//
// Key Features:
// - Infinite retries every 4 seconds (never gives up)
// - Auto-resume when circuit is evicted (no user action needed)
// - Retry immediately when tab becomes visible
// - Non-blocking UI (top banner instead of modal)
//
// Connection State Flow:
// 1. Connection lost → "show" state → banner appears, first retry
// 2. Retry fails → "retrying" state → countdown to next retry
// 3. Keep retrying every 4 seconds indefinitely
// 4. If circuit evicted → "rejected" state → auto-resume with persisted state
// 5. Connection restored → "hide" state → banner disappears
// =============================================================================

// Get reference to the reconnection banner element.
// Blazor looks for this specific ID to dispatch reconnection events.
const reconnectModal = document.getElementById("components-reconnect-modal");

// =============================================================================
// EVENT HANDLERS SETUP
// =============================================================================

// Listen for Blazor's reconnection state changes.
// This event is dispatched by the Blazor framework whenever the connection state changes.
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

// Manual retry button (shown only if automatic retries somehow fail)
const retryButton = document.getElementById("components-reconnect-button");
if (retryButton) {
    retryButton.addEventListener("click", manualRetry);
}

// Reload button (last resort if resume fails)
const reloadButton = document.getElementById("components-reload-button");
if (reloadButton) {
    reloadButton.addEventListener("click", () => location.reload());
}

// =============================================================================
// STATE CHANGE HANDLER
// =============================================================================

/**
 * Handles Blazor's reconnection state change events.
 *
 * States:
 * - "show": Connection lost, first reconnection attempt starting
 * - "retrying": Subsequent reconnection attempts (with countdown)
 * - "failed": All configured retries exhausted (shouldn't happen with infinite retry)
 * - "rejected": Server rejected the connection - circuit was evicted
 *               This triggers auto-resume to restore persisted state
 * - "hide": Successfully reconnected
 *
 * @param {CustomEvent} event - The state change event with detail.state
 */
function handleReconnectStateChanged(event) {
    const state = event.detail.state;

    console.log(`[Reconnect] State changed to: ${state}`);

    switch (state) {
        case "show":
            // Connection lost - show banner IMMEDIATELY
            // The original dialog uses showModal() which is instant.
            // We manually set visibility to ensure instant display.
            reconnectModal.style.visibility = "visible";
            reconnectModal.style.transform = "translateY(0)";
            break;

        case "retrying":
            // Subsequent retry - ensure still visible
            reconnectModal.style.visibility = "visible";
            reconnectModal.style.transform = "translateY(0)";
            break;

        case "hide":
            // Successfully reconnected! Hide banner immediately
            console.log("[Reconnect] Connection restored successfully");
            reconnectModal.style.visibility = "hidden";
            reconnectModal.style.transform = "translateY(calc(-100% - 10px))";
            break;

        case "failed":
            // Automatic retries exhausted (shouldn't happen with our infinite retry config)
            // Keep banner visible, set up listener to retry when user returns to tab
            console.log("[Reconnect] Automatic retries exhausted, waiting for tab focus");
            reconnectModal.style.visibility = "visible";
            reconnectModal.style.transform = "translateY(0)";
            document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            break;

        case "rejected":
            // Server rejected the connection - the circuit was evicted.
            // This happens when DisconnectedCircuitRetentionPeriod expires.
            // We AUTO-RESUME here instead of showing a "Resume" button.
            console.log("[Reconnect] Circuit evicted, attempting auto-resume with persisted state...");
            autoResume();
            break;
    }
}

// =============================================================================
// RECONNECTION FUNCTIONS
// =============================================================================

/**
 * Manually trigger a reconnection attempt.
 * Called when user clicks "Retry Now" button.
 */
async function manualRetry() {
    // Remove visibility listener if it was set
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        console.log("[Reconnect] Manual retry initiated...");

        // Blazor.reconnect() attempts to reconnect to the existing circuit.
        // Returns:
        // - true: Successfully reconnected to existing circuit
        // - false: Reached server but circuit was evicted (need to resume)
        // - throws: Couldn't reach server at all
        const successful = await Blazor.reconnect();

        if (!successful) {
            // Circuit was evicted, try to resume with persisted state
            console.log("[Reconnect] Circuit evicted during retry, attempting resume...");
            await autoResume();
        }
    } catch (err) {
        // Couldn't reach server, wait for tab to become visible and try again
        console.log("[Reconnect] Server unreachable, will retry on tab focus");
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

/**
 * Automatically resume the circuit using persisted state.
 *
 * .NET 10 PERSISTED STATE:
 * When a circuit is evicted, properties marked with [PersistentState] are
 * serialized and stored. Blazor.resumeCircuit() creates a NEW circuit and
 * restores those properties, allowing the user to continue where they left off.
 *
 * This means:
 * - UserStateService.Username is restored (user stays "logged in")
 * - ChatNavigationState.CurrentRoomId is restored (user stays in same room)
 * - No need for user to click anything - it's automatic
 */
async function autoResume() {
    try {
        console.log("[Reconnect] Calling Blazor.resumeCircuit()...");

        // Blazor.resumeCircuit() creates a new circuit and restores persisted state.
        // Returns:
        // - true: Successfully created new circuit with restored state
        // - false: Resume failed (e.g., persisted state expired or corrupted)
        const successful = await Blazor.resumeCircuit();

        if (successful) {
            console.log("[Reconnect] Circuit resumed successfully with persisted state!");
            // Banner will hide automatically via the "hide" state event
        } else {
            // Resume failed - persisted state might have expired or something went wrong.
            // For now, reload the page as a last resort.
            // TODO: Could show login screen instead if you want to avoid full reload.
            console.log("[Reconnect] Resume failed, reloading page...");
            location.reload();
        }
    } catch (err) {
        // Something went wrong during resume
        console.error("[Reconnect] Resume error:", err);
        location.reload();
    }
}

/**
 * Retry connection when the document becomes visible.
 * This catches cases where the user switched away from the tab
 * while disconnected and comes back.
 */
async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        console.log("[Reconnect] Tab became visible, retrying connection...");
        await manualRetry();
    }
}

