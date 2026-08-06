# Item 2: Pickers Mounted-Hidden + Client-Owned Open/Close

*Item 2 of the input-locality roadmap (`docs/input-locality-analysis.md`). 2026-08-06. Status: IMPLEMENTED — awaiting build + manual test (checklist at bottom).*

## What changed conceptually

Opening a picker used to be: click → circuit round trip → server renders ~1000-cell subtree → ~250KB render batch → DOM build → ~1000 CDN image fetches. Now the picker subtrees **mount once in the background** (~1.2s after page load, hidden) and **visibility is client-owned view state**: chat.js flips a `data-picker="emoji|gif"` attribute on `.message-input-container`, CSS shows the matching pane. Opening costs zero round trips. The server no longer knows or cares whether a picker is open — `showEmojiPicker`/`showGifPicker`, `@onfocus`/`@onblur`, and all toggle/close handlers are gone from MessageInput.

## The moving pieces

### New: `Components/PickerPane.razor` — render firewall
`ShouldRender() => false` wrapper around each pane. MessageInput re-renders constantly (typing indicator, reply bar, upload progress); without this, every one of those renders would re-diff the huge hidden picker subtrees. The pickers' own interactions still render — Blazor renders from the component that handled the event. The emoji pane is `@key="isMobileLayout"` so a layout flip rebuilds it with the right picker inside (first render always happens for a fresh instance).

### MessageInput.razor
- Toggle buttons are plain markup with `data-picker-toggle="gif|emoji"` — no Blazor handlers.
- Panes render inside `@if (pickersMounted)` (gif pane additionally `!isMobileLayout` — on mobile the GIF picker lives inside CombinedPicker, so a second mount would be waste), each as `backdrop + container` pair tagged `data-picker-pane`.
- `pickersMounted` flips via `MountPickersAfterIdleAsync` (1.2s delay, off the critical path) or `EnsurePickersMounted` (JSInvokable — a toggle tap that beats the mount asks for it immediately; the pane then appears already-open because the CSS attribute is already set).
- `HandleSend` / `HandleGifSelected` / `HandleGifUploadError` / `OnMobileLayoutChanged` no longer touch picker flags. `HandleGifSelected` calls `closePickers` via JS for the **upload** path (no card tap happened there); item 1's flag-clearing in `HandleGifUploadError` is reverted — there is no server state to converge anymore, and upload failures now leave the picker open behind the modal for a retry (the pre-item-1 behavior, restored).

### chat.js — "Picker visibility — client-owned view state" section
- `window.closePickers()` + one delegated click listener: toggle buttons (set/remove/swap `data-picker`), backdrop tap (close), CombinedPicker tabs (`data-active-tab` flip on `.combined-picker`).
- `focusin`/`focusout` on `.message-input`: focus closes pickers (one-gesture dismiss — the textarea sits above the backdrop via `[data-picker] .message-input`) and sets `data-composing`; blur clears it; the send pipeline clears both attributes explicitly (send keeps focus — mousedown is prevented — so no focusout fires).
- `registerPickerHost(container, ref)` → `EnsurePickersMounted`; `registerPickerOpenHook(el, ref)` → stashes `_openRef` on each picker root, auto-firing `OnPickerOpened` if the pane is already open when the component arrives (the early-tap race).
- `notifyPickerOpened` invokes `OnPickerOpened` on every picker in the opened pane and dispatches a synthetic `scroll` on `.emoji-content` — the scroll-highlight init ran inside `display:none` where every `offsetTop` was 0, so it needs one live-layout pass.
- Gif-card listener (item 1) now closes via the attribute instead of inline styles (the panes persist, so styles would linger). Layout-flip handler closes pickers before Blazor swaps the pane. The mobile action-bar dismiss guard also matches `.message-input-container[data-picker]`.
- Bonus: emoji **category jump is client-side** (capture listener → `scrollIntoView`); the Blazor `@onclick` still clears an active search and re-scrolls idempotently.

### Refresh-on-open (`OnPickerOpened` JSInvokable)
Always-mounted components go stale between opens; each open now refreshes what a remount used to:
- **EmojiPicker**: re-freezes `frozenRecents` (Discord parity — recents update between opens, never mid-open), clears leftover search. `ShouldRender` gained an `_openRefreshPending` branch. Reaction-mode instances (MessageItem) never receive this — they still mount fresh per open.
- **GifPicker**: refreshes recents, clears leftover search, and pays the **deferred trending fetch** — `OnInitializedAsync` no longer calls Klipy, so mounting hidden costs no provider traffic for users who never open the picker.

### CombinedPicker — fully static
Tab switching is a client-side `data-active-tab` flip; `Tab`/`activeTab`/`SwitchTab`/`InitialTab` deleted, zero `@code` logic. Active tab now persists across sheet opens (component lives on) — deliberate, Discord-like.

### CSS
- MessageInput.razor.css: panes `display:none` by default, shown by `[data-picker="…"] [data-picker-pane="…"]`; `picker-open`→`[data-picker]` (textarea z-lift), `emoji-open`→`[data-picker="emoji"]` (mobile margin push), `input-focused`→`[data-composing]` (mobile upload-button hide), toggle-button active states→attribute selectors.
- CombinedPicker.razor.css: `.active` rules → `data-active-tab` attribute rules.
- EmojiPicker.razor.css: `content-visibility: auto` + `contain-intrinsic-size: auto 320px` on `.emoji-section` — offscreen sections cost ~nothing to lay out.

### Emoji images lazy (load-bearing)
`GetEmojiHtml` → `ConvertEmojis(..., lazyImg: true)` → `BuildEmojiImg` adds `loading="lazy"`; `RenderCustomEmoji` (picker-only) is lazy too. Inside `display:none` a lazy image never intersects, so the hidden mount fetches **zero** emoji images; they load on first open as cells near the viewport. Message/reaction emoji stay eager. (This also fixes the pre-existing ~1000-fetch burst on picker open.)

### ChatBase.AddRecentEmoji — in-place mutation invariant
Trim is now `RemoveAt` in a loop instead of `Take(20).ToList()`: the mounted EmojiPicker holds the list **reference** as its parameter behind the PickerPane firewall (parameters no longer flow), so in-place edits are what keep the per-open recents refresh honest. Do not reintroduce list replacement here.

## Deliberate behavior changes
1. GIF picker tab + CombinedPicker tab persist across opens (was: reset every open).
2. Upload failures leave the picker open behind the error modal (item 1 had it closing; pre-item-1 behavior restored).
3. A toggle tap in the first ~1.2s after page load shows the button active state immediately but the panel only when the rush-mount lands (one round trip) — same cost as every open used to be, and only in that window.
4. Trending (Klipy) is fetched on first open instead of page load; sheet-open on mobile triggers it for both tabs.

## Test checklist (user builds/runs)
1. Desktop: wait ~2s after load → emoji and GIF pickers open **instantly**; toggling between them swaps directly; second tap closes.
2. Close rules: backdrop click; textarea click (closes + focuses in one gesture); send with picker open (sends + closes); Esc is not handled (parity).
3. Emoji: insertion still instant; recents update on **next** open; category buttons jump instantly; sidebar highlight is correct right after opening (not stuck on the last category).
4. Network tab on page load: **no** emoji CDN flood, **no** Klipy call until first picker open; first open lazy-loads visible cells only.
5. GIF: trending appears on first open (brief "Loading…" acceptable); search + send → instant close + ghost (item 1 regression); favorite star doesn't close; upload error → modal with picker still open behind it.
6. Early tap: reload and click the emoji button within ~1s → button highlights, panel appears after the rush-mount round trip.
7. Mobile: sheet opens instantly; GIFs/Emoji tabs switch instantly and persist across reopen; margin push works; backdrop/textarea taps close; upload button hides on focus, returns on send and on blur.
8. Layout flip across 600px with a picker open → closes, correct picker type afterward.
9. Reaction picker on a message → unchanged (fresh recents per open, category jump now instant there too).
10. Regressions: text send pipeline, typing indicator, reply flow, drag-drop, paste upload, ReadOnly channel flip (input rewires, no errors).

## Risk notes
- All JS state (`data-picker`, `data-composing`, `data-active-tab`, `_openRef`, `_pickerHostRef`) lives in attributes/expando props Blazor never renders — re-renders can't clobber them; the ReadOnly flip destroys the container element and the state dies with it (correct).
- `PickerPane` blocks parameter flow to pickers — safe because every parameter they receive is reference-stable for the component's lifetime (see AddRecentEmoji invariant); anything per-open goes through `OnPickerOpened`.
- If `MountPickersAfterIdleAsync` fires after disposal, `EnsurePickersMounted`'s try/catch swallows the dead-circuit exception.
