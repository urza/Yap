# Coding Plan: GIF Select — Instant Close + Optimistic GIF Ghost

*Item 1 of the input-locality roadmap (`docs/input-locality-analysis.md`). 2026-08-06. Status: IMPLEMENTED as specified below — awaiting build + manual test (checklist at bottom).*

## Goal

Tapping a GIF card gives instant local feedback — the picker closes immediately and a dimmed GIF ghost appears at the bottom of the message list — while the real send round-trips unchanged. Server stays authoritative; the existing ghost reconciler swaps in the real message.

## Current behavior (verified)

- `.gif-card` divs (`GifPicker.razor:523-561`) have Blazor `@onclick` → `SendLocalAsync`/`SendProviderAsync` (`GifPicker.razor:588-603`) → `GifService` (fast; normalization backgrounded) → `OnGifSelected` → `MessageInput.HandleGifSelected` (`MessageInput.razor:507`) clears picker flags + `SendMessageAsync`. **Nothing changes on screen until the render batch returns** (~1s on the slow client).
- Null-attachment failures log a warning, leave the picker open, no user feedback.
- Mobile GIF cards live inside CombinedPicker in the same `.emoji-picker-container` — same DOM ancestry, same fix.

## Edits — 4 files

### 1. `Yap/wwwroot/js/chat.js` — two edits

**Edit 1a — extract a shared echo-mount helper.** Replace `showPendingEcho` (currently at ~line 1523):

```js
// BEFORE
const showPendingEcho = (text) => {
    const host = document.querySelector('.pending-echoes');
    if (!host) return;
    const ghost = document.createElement('div');
    ghost.className = 'pending-message';
    ghost.textContent = text;
    host.appendChild(ghost);
    setTimeout(() => ghost.remove(), 15000);
    watchForEchoConfirm();
};
```

```js
// AFTER
// Shared tail of every optimistic echo (text and GIF): mount in the JS-owned
// container, arm the 15s "never sent" self-remove, ensure the reconciler is watching.
const appendPendingEcho = (ghost) => {
    const host = document.querySelector('.pending-echoes');
    if (!host) return;
    host.appendChild(ghost);
    setTimeout(() => ghost.remove(), 15000);
    watchForEchoConfirm();
};

const showPendingEcho = (text) => {
    const ghost = document.createElement('div');
    ghost.className = 'pending-message';
    ghost.textContent = text;
    appendPendingEcho(ghost);
};
```

*(Both are `const` arrow functions invoked only from event handlers, so definition order within the file doesn't matter at runtime — the existing send listener already calls `showPendingEcho` from above its definition.)*

**Edit 1b — the GIF-card capture listener.** Insert after `clearPendingEchoes` (~line 1561), before `watchForOwnMessage`:

```js
// GIF-select instant feedback: tapping a picker card hides the picker at once and
// shows an optimistic ghost of the chosen GIF; the real message (and the server-side
// picker close) arrive one round trip later. Capture phase, installed at script load —
// same rationale as the send-button listener above. Blazor's @onclick still fires:
// display:none doesn't stop event propagation. Inline style (not a class) because the
// incoming render batch removes these exact nodes (showGifPicker=false), so the style
// dies with them and the next open renders fresh visible nodes — nothing to clean up.
document.addEventListener('click', (e) => {
    const card = e.target.closest('.gif-picker .gif-card');
    if (!card) return;
    // The favorite star lives inside the card — starring must not close or ghost.
    // (Its Blazor stopPropagation is bubble-phase; this capture listener runs first.)
    if (e.target.closest('.gif-fav-btn')) return;

    // Hide the picker container + its backdrop (on mobile this is the CombinedPicker sheet).
    const wrap = card.closest('.emoji-picker-container');
    if (wrap) {
        wrap.style.display = 'none';
        const backdrop = wrap.parentElement?.querySelector(':scope > .emoji-picker-backdrop');
        if (backdrop) backdrop.style.display = 'none';
    }

    // Ghost built from the preview the user actually saw — it's in the browser cache,
    // so it paints instantly. createElement + property assignment only (XSS-safe).
    const img = card.querySelector('img');
    const vid = card.querySelector('video');
    const src = img?.src || vid?.src;
    if (!src) return; // no preview to echo — close-only

    // Class must include pending-message (all echo machinery keys on it) and must
    // never be message-group (both MutationObservers match on that).
    const ghost = document.createElement('div');
    ghost.className = 'pending-message pending-gif';

    let media;
    if (img) {
        media = document.createElement('img');
    } else {
        media = document.createElement('video');
        media.muted = true;
        media.autoplay = true;
        media.loop = true;
        media.playsInline = true;
        media.className = 'gif-card-video'; // rides the document-level canplay autoplay wiring
    }
    media.src = src;
    // The card carries the true aspect ratio inline — copying it reserves the ghost's
    // height before the preview paints, so the message list doesn't jump.
    if (card.style.aspectRatio) media.style.aspectRatio = card.style.aspectRatio;
    ghost.appendChild(media);

    appendPendingEcho(ghost);
    window.scrollToBottom();
}, true);
```

**Machinery reused with zero changes:** FIFO reconciler (`watchForEchoConfirm` — in-order circuit processing means mixed text+GIF in-flight sends drain in send order), 15s self-expiry, `clearPendingEchoes()` on channel switch, `scrollToBottom()`'s pending-image wait (`.pending-echoes` is the last child of `.messages`).

### 2. `Yap/wwwroot/app.css` — GIF ghost sizing

Append directly after the `.pending-message` block (~line 186):

```css
/* GIF variant of the send echo: an <img>/<video> stand-in built from the picker card's
   preview, dimmed by the .pending-message opacity. Sized like the real render (360px
   cap, 8px radius — see MessageItem's gif-message) so the swap barely moves. Fixed
   360px width (not min(360px, natural)) because the client doesn't know the full-size
   dimensions at click time — narrow GIFs shrink slightly when the real message lands.
   The inline aspect-ratio (copied from the card) reserves height before the preview paints. */
.pending-message.pending-gif img,
.pending-message.pending-gif video {
    display: block;
    width: 360px;
    max-width: 100%;
    border-radius: 8px;
}
```

*(The ghost div itself inherits `.pending-message`'s padding — which mirrors `.message-group`'s text column — and its 0.55 opacity. No further rules needed.)*

### 3. `Yap/Components/GifPicker.razor` — surface the rare null-send

Replace both send methods (~lines 588-603):

```csharp
// BEFORE
private async Task SendLocalAsync(Guid gifEntryId)
{
    Logger.LogInformation("GifPicker click: cached entry {EntryId}", gifEntryId);
    var att = await GifService.SendCachedGifAsync(gifEntryId, searchText, CurrentUserId, CurrentUsername);
    if (att != null) await OnGifSelected.InvokeAsync(att);
    else Logger.LogWarning("GifPicker click: SendCachedGifAsync returned null for {EntryId}", gifEntryId);
}

private async Task SendProviderAsync(GifSearchItem item)
{
    Logger.LogInformation("GifPicker click: provider item {SourceId} (formats={Formats}, previews={Previews})",
        item.SourceId, item.Formats.Count, item.PreviewFormats.Count);
    var att = await GifService.SendProviderGifAsync(item, searchText, CurrentUserId, CurrentUsername);
    if (att != null) await OnGifSelected.InvokeAsync(att);
    else Logger.LogWarning("GifPicker click: SendProviderGifAsync returned null for {SourceId}", item.SourceId);
}
```

```csharp
// AFTER
private async Task SendLocalAsync(Guid gifEntryId)
{
    Logger.LogInformation("GifPicker click: cached entry {EntryId}", gifEntryId);
    var att = await GifService.SendCachedGifAsync(gifEntryId, searchText, CurrentUserId, CurrentUsername);
    if (att != null)
    {
        await OnGifSelected.InvokeAsync(att);
    }
    else
    {
        // The client-side pipeline (chat.js) already hid the picker on the tap — tell the
        // user instead of failing silently; their optimistic ghost quietly expires on its own.
        Logger.LogWarning("GifPicker click: SendCachedGifAsync returned null for {EntryId}", gifEntryId);
        await OnUploadError.InvokeAsync("That GIF isn't available anymore — try another one.");
    }
}

private async Task SendProviderAsync(GifSearchItem item)
{
    Logger.LogInformation("GifPicker click: provider item {SourceId} (formats={Formats}, previews={Previews})",
        item.SourceId, item.Formats.Count, item.PreviewFormats.Count);
    var att = await GifService.SendProviderGifAsync(item, searchText, CurrentUserId, CurrentUsername);
    if (att != null)
    {
        await OnGifSelected.InvokeAsync(att);
    }
    else
    {
        Logger.LogWarning("GifPicker click: SendProviderGifAsync returned null for {SourceId}", item.SourceId);
        await OnUploadError.InvokeAsync("That GIF isn't available anymore — try another one.");
    }
}
```

### 4. `Yap/Components/MessageInput.razor` — converge server picker state on failure

Replace `HandleGifUploadError` (~line 533):

```csharp
// BEFORE
private void HandleGifUploadError(string error)
{
    ShowWarning("GIF upload failed", error);
}
```

```csharp
// AFTER
private void HandleGifUploadError(string error)
{
    // The gif-card capture listener (chat.js) hides the picker the moment a card is
    // tapped — converge the server flags so the next toggle click reopens in one tap
    // instead of first "closing" an already-invisible picker. Upload failures land here
    // too; closing before the modal is fine — the modal explains what happened.
    showGifPicker = false;
    showEmojiPicker = false;
    ShowWarning("Couldn't send GIF", error);
}
```

*(Title generalized from "GIF upload failed" — this callback now covers upload failures **and** null-sends, and picker uploads are themselves send attempts. `CombinedPicker` passes `OnUploadError` through unchanged — no edit there.)*

## Explicitly out of scope

- Client-owned open/close for the normal toggle path (roadmap item 2) — this plan's inline-style hide is a one-shot on card click only.
- GIF send→appear telemetry (text-send-only today).
- Ghosts for the `InputFile` upload path — the tus flow and GIF uploads have their own progress UI.

## Test checklist (user builds/runs)

1. Desktop: search a GIF, click a card → picker + backdrop vanish immediately, dimmed GIF appears at the bottom; real message replaces it ~1 RTT later, ghost gone, no layout jump (GIFs ≥360px wide).
2. Narrow GIF (<360px natural width) → ghost slightly wider than the real message; brief shrink on swap is the accepted trade.
3. Rapid mixed sends: text then GIF while both in flight → ghosts drain in order, none left over.
4. Mobile: emoji button → CombinedPicker → GIFs tab → tap GIF → bottom sheet + backdrop close instantly, ghost shows.
5. Favorite star on a card → star toggles only; picker stays open, no ghost, no close.
6. Folder tiles / back buttons / "Manage your GIFs" link / search input → no ghost, no close.
7. Entry whose preview is a `<video>` (older cached entries in Recent/Favorites) → video ghost autoplays muted.
8. Failure path (hard to trigger — entry deleted between render and click): "Couldn't send GIF" modal, picker closed on both sides, ghost expires by 15s.
9. Channel switch with a ghost pending → ghost cleared.
10. Text-send regression: type → Enter → instant clear + text ghost unchanged.
11. Settings → GIF library manager unaffected (listener scoped to `.gif-picker .gif-card`).

## Risk notes

- `display:none` during capture does **not** break the Blazor click — the node stays in the tree and the event keeps propagating, so the `@onclick` RPC fires normally.
- If `SendLocalAsync`/`SendProviderAsync` throws outright (not null — an exception), Blazor's unhandled-event-exception path takes over (circuit error/reconnect banner) — pre-existing behavior, not changed here; the ghost's 15s expiry covers the visual.
- Both pickers can't be open at once (the toggles enforce mutual exclusion), so `querySelector` on the wrapper always finds the backdrop belonging to the open picker.
