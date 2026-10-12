// Follow-the-bottom state and the "New messages" indicator.
// Follow is released only by an upward user scroll, a real scroll event, never by a wheel,
// touch or key gesture that does not move the list. The gesture listeners that did that turned
// follow off on a tap with a small finger movement, or on a swipe over a short conversation,
// and new messages then landed below the fold with no sign (audit C-01). The pre-rewrite page
// never let a tap change scrolling.
// `label` returns the pill's wording; it is read each time the pill appears.
export function createScroll(scroller, pill, label) {
    let following = true,
        previousTop = scroller.scrollTop,
        frame;
    const nearBottom = () =>
        scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop < 80;
    const update = () => {
        cancelAnimationFrame(frame);
        if (!following) return;
        frame = requestAnimationFrame(() => {
            if (!following) return;
            scroller.scrollTop = scroller.scrollHeight;
            previousTop = scroller.scrollTop;
        });
    };
    const bottom = () => {
        following = true;
        pill.hidden = true;
        update();
    };
    scroller.addEventListener(
        'scroll',
        () => {
            // Growing media can move the bottom without a user scroll. Only upward movement
            // releases the bottom lock; otherwise late images would strand us above new content.
            if (nearBottom()) {
                following = true;
                pill.hidden = true;
            } else if (scroller.scrollTop < previousTop - 1) following = false;
            previousTop = scroller.scrollTop;
        },
        { passive: true },
    );
    pill.onclick = bottom;
    // Observe actual layout instead of fixed delays: uploads, lazy images, video metadata
    // and composer resizing can all finish after the conversation's first render.
    const resize = new ResizeObserver(update);
    resize.observe(scroller);
    resize.observe(scroller.querySelector('.messages-flow'));
    return {
        update,
        preserve() {
            if (following) return () => {};
            const bounds = scroller.getBoundingClientRect();
            const anchors = [];
            for (const node of scroller.querySelectorAll('#timeline > .message-group')) {
                const rect = node.getBoundingClientRect();
                if (rect.top > bounds.bottom) break;
                if (rect.bottom > bounds.top) anchors.push({ id: node.id, top: rect.top });
            }
            return () => {
                // A reaction/edit above the viewport can change its height; a deletion can
                // remove the first visible row. Keep the next surviving visible row in place.
                for (const anchor of anchors) {
                    const node = document.getElementById(anchor.id);
                    if (!node) continue;
                    scroller.scrollTop += node.getBoundingClientRect().top - anchor.top;
                    previousTop = scroller.scrollTop;
                    break;
                }
            };
        },
        // Someone else posted while the user reads older messages: the view keeps its place
        // (decided 2026-10-11, audit C-03) and the pill offers the way down.
        announce() {
            if (following || !pill.hidden) return;
            // Fresh wording for each appearance; a second arrival while the pill shows
            // keeps the text still, so it never flickers under the reader's eye.
            pill.querySelector('#new-messages-text').textContent = label();
            pill.hidden = false;
        },
        bottom,
        reset() {
            following = false;
            pill.hidden = true;
            cancelAnimationFrame(frame);
            previousTop = 0;
        },
    };
}
