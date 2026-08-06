# Item 4: Emoji Picker Search — Fully Client-Side

*Item 4 (final) of the input-locality roadmap (`docs/input-locality-analysis.md`). 2026-08-06. Status: IMPLEMENTED — awaiting build + manual test (checklist at bottom).*

## What changed conceptually

Emoji search was `@bind:event="oninput"`: one circuit dispatch + a full grid re-render per keystroke, with results arriving one RTT later. Now **search never touches the server**: every cell carries a `data-kw` keyword attribute (rendered once, riding the item-2 background mount) and chat.js filters the mounted grid **in place** — hiding non-matching cells and emptied sections. Results are instant and appear grouped under their section headers (an upgrade over the old flat "Search Results" list). With this, every interaction inside the emoji picker — open, scroll, category jump, search, insert — is round-trip-free; the EmojiPicker component renders exactly twice per session pattern: once at mount, once per open (recents refresh).

## The moving pieces

### EmojiPicker.razor
- Search input is a **plain `<input>`** — no `@bind`, Blazor never renders its value, JS owns it. The ✕ clear button is static markup, CSS-hidden while the box is empty via `:placeholder-shown` (send-button pattern; the placeholder must never be empty). A static `emoji-search-empty` div (`hidden` by default) provides the "No emojis found" state.
- The `@if (searchText…)` search/browse branching is gone — browse sections always render; filtering is presentation.
- Every `.emoji-btn` gains `data-kw` via `GetKeywords(e)`: custom emojis → shortcode lowercased, standard → `EmojiData.EmojiKeywords` value lowercased (same substring-match semantics the server search had; emojis without keyword entries stay unsearchable — parity). ~40KB across the grid, on the background mount.
- Sidebar category buttons **lost their Blazor `@onclick`** — the item-2 client-side jump listener is now the only handler. Deleted: `searchText`, `_lastRenderedSearchText`, `GetSearchResults`, `ClearSearch`, `ScrollToCategory`, `RecentCategory`. `ShouldRender` reduces to first-render + `_openRefreshPending`.

### chat.js
- `filterEmojiPicker(picker, query)`: toggles `data-search-miss` on cells / `data-search-empty` on sections, toggles the empty-state, scrolls to top on a query, and dispatches a synthetic `scroll` so the sidebar highlight re-syncs to the filtered layout.
- `resetEmojiSearchIn(root)`: clears box + filter; called by the ✕ (delegated click branch), category jumps (a hidden section can't be scrolled to), and both reopen paths (`notifyPickerOpened` + the `registerPickerOpenHook` auto-fire) — replacing the C#-side `searchText = ""` reset from item 2.
- Delegated `input` listener on `.emoji-picker .emoji-search input` — works identically for the input picker and reaction-mode pickers (which now inherit instant search for free).
- `window.scrollEmojiPickerToSection` deleted (no C# caller left). `initEmojiPickerScroll`'s highlight skips `offsetParent === null` sections (hidden by an active filter).

### EmojiPicker.razor.css
`[data-search-miss]` / `[data-search-empty]` → `display: none` (higher specificity than the base `.emoji-btn` flex rule), plus the `:placeholder-shown` rule for the ✕.

## Semantics notes
- JS-set filter attributes on Blazor-rendered elements survive re-renders (Blazor never renders them — the data-picker principle). The per-open recents re-render produces fresh unfiltered recent cells, consistent because reopen always resets the filter first.
- Filtering ~1000 cells per keystroke is sub-millisecond DOM work, and `content-visibility: auto` keeps the relayout cheap — no debounce needed for purely local work.
- Search now also filters the Recent section in place (previously recents weren't searched; a matching emoji simply shows there *and* in its category — consistent, harmless).

## Test checklist (user builds/runs)
1. **The point:** type in emoji search — results appear per keystroke with zero WS frames (DevTools), grouped under their section headers; clearing restores the full grid.
2. ✕ button: appears only while a query exists; click clears + restores; no round trip.
3. Category jump during an active search → search clears, jumps to the section.
4. "No emojis found" for garbage queries; custom emojis found by shortcode fragments; case-insensitive.
5. Reopen after searching → box empty, full grid (both the normal open and the early-tap mount path).
6. Sidebar highlight tracks correctly while scrolling filtered results and after clearing.
7. Reaction-mode picker (message hover → react): search is instant there too; emoji click still adds the reaction (Blazor path).
8. Insert-from-search-results works in the input picker (client-side splice, picker stays open).
9. Regressions: recents refresh per open, GIF picker search (still server-side by design — provider API), items 1–3 behaviors.
