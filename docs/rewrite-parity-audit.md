# Parity audit: the pre-rewrite chat page against the offline client

Date: 2026-10-11. Old page: commit 0cb67422 (Blazor Server chat page, the last main before PR #1). New client: main at 78065ddd (`Yap/wwwroot/chat-client/`). Method: four read-only code audits, one per area, each listing every interaction state with file and line citations on both sides. Nothing was run; items marked "device" need a check on a phone before a decision.

The rewrite was asked to keep the look and the behaviour the same for users. Five phone bugs found by the user on 2026-10-11 showed that it did not, and that the automated checks had not compared the mechanisms. This audit does that comparison. The five fixed bugs (Send focus on pointerdown, post-send draft focus, action bar over the editor, two action bars on iOS, bar under the finger, unstyled Reload control) are not repeated below.

## How to read this

- **Class R** (restore): the old behaviour is the target. The rewrite changed it without a reason the user accepted.
- **Class K** (keep): new behaviour that improves on the old page or fixes an old defect. Kept unless the user says otherwise.
- **Class D** (decide): a change that follows from the offline architecture or a documented policy change. The user decides.
- **Severity**: breaks use, visible, minor. Taken from the reports.
- **Device**: the code shows the mechanism, but the visible result needs a phone.
- IDs point into the appendices: `A-01` is item 1 of Appendix A; `B-01` is D01 of Appendix B; `C-01` is item 1 of Appendix C; `D-01` is D1 of Appendix D. Duplicates across reports are listed once with both IDs.

Counts: 112 reported differences, 104 distinct. Class R 64, K 22, D 12, and 6 items with one part to restore and another to keep or decide. Of the distinct items, 9 break use, 39 are visible and 56 are minor.

## Same-shape findings (the class behind the five phone bugs)

Each of these keeps an old mechanism and adds a second one, or makes a call the old page did not make. They are the first to fix.

| ID | Sev | What the user sees | Old page (target) |
| --- | --- | --- | --- |
| A-01 | breaks | Phone: the tap that closes the More sheet also hits what is under the finger (a pill, a link, a bar button), because the backdrop is removed at pointerdown. | The backdrop was a real element with a click handler; the tap closed the menu and did nothing else. |
| C-01 | breaks | Phone: a tap with a small downward movement, or a swipe on a short conversation, turns auto-follow off. New messages and reactions then land below the fold with no indicator. | A tap never changed scroll behaviour; incoming messages always scrolled to the bottom. |
| A-03 / C-02 | breaks | The More menu and the Delete dialog close whenever the list scrolls, including scrolls the client makes (new message, image load, Android keyboard closing). | No scroll rule touched them; the idle and scroll fade skipped rows with an open menu. |
| A-02 | breaks | Touchscreen laptops and iPads with a trackpad: the first automatic scroll after load hides the hover action bar for good. | The scroll fade ran only after a finger tap armed it; mouse hover always worked. |
| A-10 | visible, device | Hybrid devices: a finger tap on row A plus mouse hover on row B shows two bars. | One hover state, one bar. |
| B-02 | visible | With a picker open, the input box floats above the dim backdrop: the box is not dimmed on phones, + opens the file chooser under the sheet, the GIF toggle switches pickers instead of closing. | The backdrop lived inside the box; only the textarea, the picker and Send were above it. |
| B-03 | visible | The draft clears only after the queue write finishes and only if unchanged. Text typed in that window keeps the sent text in the box; a second Enter sends it twice. | The box cleared in the same task as the click. |
| B-06 / D-12 | visible, device | The upload warning modal focuses its button on open and refocuses the draft on close: the phone keyboard closes, then reopens, and the open picker closes. | The modal made no focus call. |
| C-05 | visible, device | The gallery focuses its close button on open and refocuses the thumbnail on close: Chrome and Android can scroll back to the thumbnail; iOS closes and reopens the keyboard. | No focus call on open or close. |
| A-06 | visible, device | Edit does not focus the editor on touch-capable devices. With the keyboard up on iOS, typing goes to the draft; on a touch laptop the user must click into the box. | The editor was always focused with the caret at the end. |
| B-12 | visible, device | Send is a disabled button for whitespace and during queueing. A tap on a disabled button may skip the keep-focus guard and close the keyboard. | Send was never disabled; its guard always ran. |
| A-21 | minor, device | Phone: Reply hides the bar at once because the keyboard resize scrolls the list past the 50 px rule. | The bar stayed until the idle fade. |

## All distinct differences

### Message rows and actions (Appendix A)

| ID | Sev | Class | What differs | Old page (target) |
| --- | --- | --- | --- | --- |
| A-01 | breaks | R | See above. | Backdrop click closed the sheet, nothing else. |
| A-02 | breaks | R | See above. | Scroll fade armed only by a touch. |
| A-03 / C-02 | breaks | R | See above. | Menus unaffected by scrolling. |
| A-04 | breaks | R | Android, keyboard open: More opens and closes again (same rule as A-03). | The sheet stayed open. |
| A-05 | visible | R | Desktop: the reaction picker hides the row's bar and highlight; the picker floats with no anchor. | `picker-open` kept the bar and the highlight. |
| A-06 | visible | R | See above. | Editor always focused. |
| A-07 | visible, device | R | Save shows the old text for a moment before the edited text. | One render: editor to edited text. |
| A-08 / C-08 | visible | R | Only `http(s)://` becomes a link; `www.` and bare domains stay text; trailing `.`, `)`, quotes go into the href. | `www.` and bare domains linked; trailing punctuation stripped. |
| A-09 | visible | R | Phone: the favourite star in the bar and the star on the GIF disagree after a tap. | Both rendered from one source. |
| A-10 | visible, device | R | See above. | One bar. |
| A-11 | visible | R | Phone: after a picker, menu or dialog stayed open more than 5 s, the bar never fades. | Every touch in the row re-armed the fade. |
| A-12 | minor | R | Save with empty text keeps the editor open. | The editor closed, message unchanged. |
| A-13 | minor | K | iOS: the first tap on a pill, link or image in a row with a hidden bar acts at once. | The hover reveal swallowed that first tap. Kept: one tap less, no mis-tap. |
| A-14 / C-14 | minor | R | The first row and the first pending row lose their top margin. | The margin rule never matched. |
| A-15 / C-11 | minor | K | Reply-preview jump: smooth `scrollIntoView`, highlight starts with the scroll, unloaded targets are fetched. | 600 ms list animation, highlight after it, unloaded targets did nothing. |
| A-16 | minor | K | A reply to a missing message shows "Original message unavailable". | No preview and no connector. |
| A-17 | minor | R | Enter saves and Escape cancels during IME composition. | Both ignored while composing. |
| A-18 | minor | R | Copy Text has no `execCommand` fallback; on failure the menu stays open. | Menu closed first, fallback for plain-http. |
| A-19 | minor | R | A row under edit stops updating (reactions, header, reply target). | The row re-rendered live and kept the draft. |
| A-20 | minor | R | Keyboard: Tab from More does not reach the menu; a reaction update drops focus. | Menu and picker followed the button in the row. |
| A-21 | minor, device | R | See above. | Bar stayed until idle. |
| A-22 | minor | R | Reaction tooltip names in alphabetical order, own name appended. | Names in reaction order. |
| A-23 | minor | R | "(edited)" with a leading space inside pre-wrap. | No leading space. |
| A-24 | minor | R | Reply preview is a `<button>` with 1 px browser padding; the connector can miss the avatar centre. | A `<div>`. |
| A-25 | minor | R | Delete tip "Shift" without `<kbd>`; picker icon 18 px instead of 20 px. | `<kbd>`, 20 px. |

### Composer and pickers (Appendix B)

| ID | Sev | Class | What differs | Old page (target) |
| --- | --- | --- | --- | --- |
| B-01 | breaks | R | A file dropped outside the message column, or while the draft is disabled, replaces the chat with the file. | `dragover` and `drop` cancelled on the whole document. |
| B-02 | visible | R | See above. | Backdrop inside the box. |
| B-03 | visible | R | See above. | Cleared at once. |
| B-04 | visible | D + R | Upload progress and failure show in the pending row instead of the strip above the composer; the `<progress>` bar and the file list have no matching CSS (the stylesheet has `.upload-progress-row` rules, the script creates `.upload-progress`). | Strip with a styled bar and a failure modal. Location: decide. Styling: restore. |
| B-05 | visible | D | A file upload during a reply is sent as a reply and clears the reply bar (same as a GIF pick). | Files sent without a reply id; the bar stayed open. |
| B-06 / D-12 | visible, device | R | See above. | No focus call. |
| B-07 | visible | K | The draft has `maxlength` (server limit, 4000). | No maxlength; the server rejected. |
| B-08 | visible, device | R | The + chooser gets an extension list and opens through `click()` on a hidden input. | `accept="image/*,video/*" multiple` in a `<label>`; phones offered the camera and the photo library. |
| B-09 | visible | K | Drops on the message list upload too; the highlight shows only over the composer. | Only the composer accepted drops. |
| B-10 | visible | R | A failed GIF upload from the picker closes the picker and shows no modal. | The picker stayed open behind "Couldn't send GIF". |
| B-11 | visible | K | Escape closes the picker and the warning modal. | Escape did nothing there. |
| B-12 | visible, device | R | See above. | Never disabled. |
| B-13 / C-09 | visible | K | Android: the list stays pinned to the bottom when the keyboard opens or the composer grows. | No resize handling; the last lines went under the composer. |
| B-14 / C-10 | visible | D | The pending echo is a full row at content opacity 0.75, shown after an IndexedDB write; a failed send shows "Failed", the error and Retry. | A plain-text ghost at 0.55 in the same task as the click; silent removal after 15 s. |
| B-15 | visible | R (icon), K (state) | The read-only note lost its lock icon; a new "Connect to open this conversation." state exists. | Lock icon and text. |
| B-16 | visible | K | Drafts and reply bars are kept per conversation and survive reloads. | One textarea carried across rooms; DM switch and reload lost it. |
| B-17 | visible, device | D | Composer controls are disabled until the draft restore finishes. | Never disabled. |
| B-18 | minor | K | One random placeholder per conversation. | A new one on every re-render. |
| B-19 | minor | R | The emoji insertion point can carry over from another conversation. | Caret stored per textarea. |
| B-20 | minor | R | An emoji replaces selected draft text. | Inserted at the selection start, text kept. |
| B-21 | minor | K | Emoji category jump is instant and offset for the search bar. | Smooth, header under the search box. |
| B-22 | minor | R | GIF Browse and folder previews change on every open. | Picked only when the library changed. |
| B-23 | minor, device | R | The GIF grid is rebuilt on each open and after a star; a tap can be lost. | Diffed in place. |
| B-24 | minor | R | The emoji grid rebuilds if the catalog arrives while open; scroll can jump. | Static after mount. |
| B-25 | minor, device | R | The first picker open builds the grid inside the tap. | Panes mounted hidden 1.2 s after load. |
| B-26 | minor | K | Pasting a non-media file is blocked with a warning. | Non-media pastes did their default. |
| B-27 | minor | R (style), K (feature) | GIF "Load more" is a bare browser button; cards are keyboard-selectable. | 24 results, no paging. |
| B-28 | minor | K | Up to 20 files per pick, extension check, one message per rejected file. | 10 files, generic "Upload failed". |
| B-29 | minor | K | Navigation closes an open picker. | Picker stayed open across a room switch. |

### Timeline, scrolling and media (Appendix C)

| ID | Sev | Class | What differs | Old page (target) |
| --- | --- | --- | --- | --- |
| C-01 | breaks | R | See above. | Always to the bottom; taps never changed it. |
| C-02 | breaks | R | Same as A-03. | Menus unaffected by scrolling. |
| C-03 | visible | D | An incoming message while the user reads older messages keeps the position, with no "new messages" indicator, and is marked read unseen. | Always jumped to the bottom. Decide: restore the jump, or keep the position and add an indicator. |
| C-04 / D-21 | visible | R | Returning to the tab or the installed app does not snap to the bottom. | Always scrolled to the bottom on return. |
| C-05 | visible, device | R | See above. | No focus call. |
| C-06 | visible, device | R | iPhone: videos play inline (`playsinline` added). | Native fullscreen player. |
| C-07 | visible | R | Lobby join and leave lines are gone (server code removed too). | Italic centred system lines. |
| C-08 | visible | R | Same as A-08. | Wider link rule. |
| C-09 | visible | K | Same as B-13. | No resize handling. |
| C-10 | visible | D | Same as B-14. | Plain ghost. |
| C-11 | minor | K | Same as A-15. | List animation. |
| C-12 | minor | R | Link preview cards sit inside `.message-content`: they vanish during an edit and inherit pre-wrap. | Cards followed the content inside the body. |
| C-13 | minor, device | R | Possible one-frame duplicate when a send is acknowledged. | Ghost removed before paint. |
| C-14 | minor | R | Same as A-14. | Margin kept. |
| C-15 | minor | R (space), K (button) | Empty history note with paragraph margins and a "Load earlier messages" button above the first row. | Nothing at the top while history remained. |
| C-16 | minor | K | History loads at `scrollTop < 60` with no delay. | 100 px, 500 ms spinner, 200 ms cooldown. |
| C-17 | minor | R | Gallery closes without the fade; a tap on the counter does nothing; counter changes at 50 %. | 0.15 s fade; any tap outside the picture closed; 60 %. |
| C-18 | minor | R | The video play overlay does not return after the end. | Overlay returned at `ended`. |
| C-19 | minor, device | R | Video GIFs blocked from autoplay stay frozen. | The next tap started every paused GIF video. |
| C-20 | minor | K | Cached link videos preload metadata only. | `preload="auto"`. |
| C-21 | minor | K | Emoji-only sizing decided per message; a link disables it. | Per text segment. |
| C-22 | minor | K | Switching back to a room keeps the history loaded earlier. | Reset to the last 50. |
| C-23 | minor, device | R | Room switch: the header updates a frame before the timeline. | No gap. |
| C-24 | minor, device | R | Single-image thumbnail is a `<button>`; a narrow portrait picture may align differently. | A `<div>`, left aligned. |
| C-25 | minor | K | Timestamps refresh every minute. | Only on re-render. |
| C-26 | minor | K | More than 10 images in one message render. | Rows past 10 did not render. |

### Shell, header, sidebar and overlays (Appendix D)

| ID | Sev | Class | What differs | Old page (target) |
| --- | --- | --- | --- | --- |
| D-01 | breaks | R | Phone in landscape, or a touch tablet wider than 768 px: the profile card cannot be opened (info button hidden above 768 px, hover blocked for coarse pointers). | The button and the tap-to-open card on every phone at any width (User-Agent based). |
| D-02 | breaks, device | R | Every presence or typing packet rebuilds all sidebar rows; a tap can miss, the desktop hover card can stay. | Rows diffed in place. |
| D-03 | breaks | R | While the live view is absent the sidebar lists every known person; with the OUTGOING section the list is clipped with no scroll. | Only online users and DM partners. |
| D-04 | visible | R | On every open and every reconnect the status chip turns grey, the status items are disabled, the heading loses its count. | Name and effective status from the first render, kept through a reconnect. |
| D-05 | visible, device | R | A pending reconnect timer can tear down a healthy new connection; the grey state shows twice. | One reconnect loop. |
| D-06 | visible | R | The account menu no longer shows the crossed bell while the account-wide mute is on. | Crossed bell while muted. |
| D-07 | visible | D | Connection state is a small header label; on phones a short label that can name the wrong state. | Orange top banner with the reconnect countdown and a Reload button. |
| D-08 | visible, device | R | DM header on phones can overflow while the connection label or "Updating history…" shows. | Chip, mailbox, hamburger only on the right. |
| D-09 | visible | D | A logged-out visit or an ended session stays on a cleared chat page with a long notice. | Redirect to the login page. Online, the old redirect is possible; offline it is not. |
| D-10 | visible | R (online), D (offline) | Opening a DM that is not in the snapshot shows a placeholder page first; offline it stays. | The DM opened at once with header and note. |
| D-11 | visible | D | On a room page in a hidden tab the title counts arrivals from every unmuted conversation. | Count and sound skipped when the current channel was muted (rooms muted by default). Documented policy change. |
| D-12 | visible, device | R | Same as B-06. | No focus call. |
| D-13 | visible, device | R | No iOS home-screen metas (`apple-mobile-web-app-capable`, status bar style, touch icon). | All three present. |
| D-14 | visible | R (buttons), D (dialog) | The bot's install link opens an intermediate dialog with bare browser buttons. | The native prompt or the guide at once. |
| D-15 | visible, device | D | Errors and lasting states use an in-flow notice strip that shifts the layout, has no close control, and is wiped by the next snapshot. | A fixed banner that hid itself. |
| D-16 | minor | R | The account menu mixes two fonts (links inherit Inter, buttons keep the browser font). | One font. |
| D-17 | minor | R | "Forget offline data" has no icon; its label does not line up. | Every item had an icon. |
| D-18 | minor | R | Shell emoji render at a fixed 18 px (room names, badges, profile card text). | `1em` everywhere. |
| D-19 | minor | R | Push prompt: no fade-out, no "Enabling..." text. | 300 ms fade, label change. |
| D-20 | minor | D | The site root resumes the last conversation in a normal tab. | Only inside the installed PWA. |
| D-21 | minor | R | Same as C-04. | Jump on return. |
| D-22 | minor | R | One Escape closes every open layer at once. | Escape closed only the gallery, the focused dialog, the focused editor. |
| D-23 | minor | R | Sign out shows a cleared chat and a "Sign in" notice before the sign-out page loads. | Straight to the sign-out page. |
| D-24 | minor | D | Any window error or unhandled rejection shows "Something went wrong" with Try Again and Reload, without the error text. | ErrorBoundary for render errors with the message; full-screen modal for circuit errors. |
| D-25 | minor, device | R | Sidebar rows are links: long-press opens the browser link menu on phones. | `<div>` rows. |
| D-26 | minor | K | The app badge no longer asks for notification permission. | Asked inside the installed PWA. |
| D-27 | minor | K | A narrow desktop window gets the info button instead of hover. | Hover on every desktop. |
| D-28 | minor | K | Room header text chosen once per load. | New random text on every navigation. |
| D-29 | minor | R | Back and sidebar buttons lost their tooltips. | `title` on both. |
| D-30 | minor | R | A DM link to an unknown user shows the placeholder plus an error. | Redirect to the lobby. |
| D-31 | minor | D | Settings, Notification settings, Admin and Add Room are full page loads. | In-circuit navigation. |
| D-32 | minor | D | DM sound is skipped when the arriving DM is muted. | Skipped when the current DM was muted. Documented policy change. |

## Proposed order of work

1. The same-shape findings above, phone first: A-01, C-01, A-03/C-02 (with A-04), B-02, B-03, A-02 with A-10, B-06, C-05, A-06, B-12, then B-01.
2. The remaining "breaks use" and "visible" class R items: D-01, D-04, D-05, D-03, C-04, C-06, D-13, A-05, A-07, A-08, A-09, A-11, B-08, B-10, D-06, D-08, D-10, D-14 buttons, D-16, B-04 styling, B-27 styling, C-07.
3. The class R minor items in one cosmetic pass.
4. The class D items need a decision from the user before any work: C-03, B-05, D-07, D-09, D-11, D-15, D-24, D-31, D-32, B-14/C-10, B-17, D-14 dialog, D-20.

Each fix gets a phone-suite check where emulation can reach it, and the feature reference row is updated with the rule. Items marked "device" are re-checked on the phone after the fix.

## Decisions (2026-10-11)

The user ruled on the class D items. "Keep" means the new behaviour stays and the row needs no work.

| ID | Decision |
| --- | --- |
| C-03 | Keep the position while the user reads older messages. Add a floating "new messages" indicator with a down arrow that scrolls to the bottom. Return to the tab re-pins the bottom only when the view was already following it. |
| B-05 | Keep: a file upload during a reply is a reply. |
| D-07 | Keep the header label; the Blazor reconnect banner stays gone. The wrong short labels on phones are a bug to fix. |
| D-09 | Restore: logged out means the login page, as on the old page. |
| D-11, D-32 | Keep the new rule: the arriving conversation's mute setting decides the count and the sound. |
| D-15 | Keep the notice strip as it is. |
| B-14 / C-10, B-04 location | Keep the full-row pending echo and the progress in that row. The bare progress bar still gets its styling. |
| B-17, D-14 dialog, D-20, D-24, D-31 | Keep (minor, architectural). The install dialog's bare buttons still get their styling. |

## Progress

| Items | Commit |
| --- | --- |
| A-01, A-02, A-03 / C-02, A-04, A-11 | Backdrop owns the More sheet's closing tap; no scroll closes a menu; fade armed by touch only, retried under an open menu or picker |
| C-01, C-03 (decision), C-04 | Follow released only by an upward scroll; New messages pill for arrivals while scrolled up. On return to the tab a pending frame pins the bottom when following; otherwise the pill is already showing |
| B-01, B-02, B-03, B-06 / D-12, B-12 | Document-level drop guard; picker backdrop back inside the input box; draft cleared in the click's task and given back on a queue failure; no focus calls in the warning modal; Send disabled only when the account or the room forbids sending |
| A-06, A-07, A-12, A-17, C-05 | Editor focus rule, one-step save, empty save closes, IME keys ignored; gallery makes no focus calls |
| D-01, D-04, D-05, D-07, D-09, D-13, D-14 (buttons), D-16, C-06, A-05, A-08 / C-08, A-09, B-04 (style), B-27 (style) | Info button on every touch screen; chip shows the chosen status without a live view; a direct connect clears the scheduled one; Connect and Reload labels; login redirect when online; iOS metas; styled install, load-more and progress controls; one menu font; no playsinline on videos; picker-open row state; the old link rule; both GIF stars in step. Still open in A-09: a stale snapshot clone can revert the star after a reaction update |
| A-14 / C-14, A-18, A-22, A-23, A-24, A-25, B-08, B-10, B-15, B-19, B-20, C-15, C-17, C-18, D-03, D-10, D-17, D-19, D-23, D-29 | First-row margin kept; Copy Text closes first with an execCommand fallback; reaction names in reaction order (server); (edited) spacing; reply preview padding; keycap tip and 20 px picker icon; image/video chooser; failed GIF upload keeps the picker with a modal; lock icon; emoji insertion keeps the selection and the caret resets on navigation; history note margins; gallery fade, outside tap and 60 % counter; video overlay returns; last known presence without a live view; pending DM header; forget icon; push prompt fade and Enabling... label; sign out goes straight out; tooltips |
| S35 (user report 2026-10-12) | The mailbox counted the open room's unread while the room's row showed none: an Away session keeps the open room's arrivals unread, and both pages excluded only the open DM from the total. The total now excludes the open conversation of either kind, so it equals the sum of the row counts. Old-page behaviour, not a rewrite difference. Suite `mailbox-rules`. |
| Open | A-10 and D-02 (hybrid devices, sidebar rebuild: device checks), A-19, A-20, A-21, B-22 to B-25, C-07 (server feature), C-12, C-13, C-19, C-23, C-24, D-06 (needs a snapshot field for the account-wide mute), D-08, D-18, D-22, D-25, D-30 |

---

## Appendix A: Message rows and actions


Scope: message rows, the action bar, reactions, reply, edit, More menu, delete confirmation, row states.
NEW is the working tree on main at 78065ddd. That commit landed during this audit. It moves the touch
reveal from pointerdown to the tap's click ("bar under the finger"), so that issue is not reported here.
The three known fixes (send-button focus, `.touch-actions` over the editor, two bars from hover plus tap)
are also excluded.

Path keys used below.
- OLD (read with `git show 0cb67422:<path>`): `MI` = Yap/Components/MessageItem.razor, `MIcss` = Yap/Components/MessageItem.razor.css,
  `chat.js` = Yap/wwwroot/js/chat.js, `CB` = Yap/Components/Pages/ChatBase.cs, `RC` = Yap/Components/Pages/RoomChat.razor,
  `LCss` = Yap/Components/Layout/ChatLayout.razor.css, `LPS` = Yap/Services/LinkPreviewService.cs,
  `ESR` = Yap/Services/EmojiService.Rendering.cs, `Rx` = Yap/Models/Reaction.cs, `App` = Yap/Components/App.razor.
- NEW (working tree, Yap/wwwroot/chat-client/ unless a path is given): `actions.js`, `messages.js`, `messages.css`,
  `composer.js`, `pickers.js`, `pickers.css`, `scroll.js`, `app.js`, `content.js`, `history.js`, `gifs.js`,
  `rich-media.js`, `index.html`, `Yap/wwwroot/app.css`, `Yap/Offline/OfflineSnapshotService.cs`.

### Differences

#### Breaks use

1. **A-01 A tap outside the More menu also taps what is under the finger.**
   - Trigger and input: touch (phone). The More bottom sheet is open. The user taps the dimmed area above the sheet to close it.
   - NEW: a document `pointerdown` listener closes the menu and removes the backdrop at finger-down (actions.js:108-111, 112-122). The backdrop has no handler of its own (actions.js:85-86). The tap's click then goes to the element under the finger at touchend. That element can be a reaction pill (toggles a reaction), a link (opens), an image (opens the gallery), a reply preview (jumps), or a button of the same row's bar. That bar stays visible because `dismiss()` returns early while `popupOpen` is true (actions.js:346-353).
   - OLD (target): the backdrop is a real element inside the row with `@onclick="CloseMoreMenu"` (MI:303). The tap closes the menu and does nothing else.
   - OLD MI:300-304, MIcss:688-696 and 988-990. NEW actions.js:85-86, 108-122, 346-353.
   - Confidence: needs a device check. Commit 78065ddd proves the same mechanism on a device: a DOM change at pointerdown moved the tap's click to the element under the finger. Mouse on desktop is probably safe because Chrome sends the click to the common ancestor when the pressed node is gone (unsure).

2. **A-02 Touchscreen laptops and tablets with a mouse or trackpad: the hover action bar stops showing.**
   - Trigger and input: mouse or trackpad on a device where `isTouchDevice()` is true (`maxTouchPoints > 0`, composer.js:8), for example a Windows 2-in-1, a touch Chromebook, or an iPad with a trackpad.
   - NEW: `scrollStart` starts at 0 (actions.js:345). The scroll listener calls `dismiss()` when `scrollTop` is more than 50 px from it (actions.js:404-407). The first automatic scroll to the bottom on load does this. `dismiss()` passes its `isTouchDevice()` gate and adds `.scroll-dismissing` (actions.js:346-354). That class forces `opacity: 0 !important; pointer-events: none !important` on every bar, hover bars included (messages.css:72-76). Only a finger tap on a row removes it (actions.js:389). Escape also calls `dismiss()` (actions.js:398-402). Result: mouse hover shows no bar, so Reply, Edit, react and More are unreachable by mouse.
   - OLD (target): `dismissActions` ran only after a real `touchstart` on a row armed it (chat.js:1213-1229). The scroll handler did nothing until then (`scrollWatchActive`, chat.js:1240-1246). Mouse-only use always showed the bar on hover (MIcss:434-439).
   - OLD chat.js:1192-1261, LCss:119-123. NEW actions.js:345-358, 389, 398-407; messages.css:72-76; composer.js:8.
   - Confidence: read from code. A device check is needed only to confirm that `isTouchDevice()` is true on the target hardware.

3. **A-03 A scroll of the message list closes the More menu and the Delete confirmation.**
   - Trigger and input: any input. The More menu or the Delete dialog is open. The list scrolls without the user, for example a new message arrives while the view follows the bottom, or an image or a reaction grows a row near the bottom.
   - NEW: every `scroll` event on `.messages` calls `closePopup()` (actions.js:404-405). `closePopup` is the menu closer (actions.js:112-122) or, while the dialog is open, the dialog closer (actions.js:187-191). Follow-bottom sets `scrollTop` after each render and on each layout change (app.js:723; scroll.js:7-15, 59-61). In an active conversation the dialog can vanish before the user presses Delete.
   - OLD (target): no scroll rule closes either one. The menu closes on a backdrop click (MI:303). The dialog closes on Cancel, a backdrop click, or Escape (MI:329, 360, 608-625). The old idle and scroll dismiss refused to act while `.menu-open` was present (chat.js:1205).
   - OLD MI:303, 327-365, 608-625; chat.js:1199-1211. NEW actions.js:112-122, 187-191, 404-405; scroll.js:7-15, 59-61; app.js:723.
   - Confidence: read from code.

4. **A-04 Android with the keyboard open: More opens and then closes again.**
   - Trigger and input: touch on Android Chrome. The user types in the composer (keyboard up), the view is at the bottom, and the user taps ⋯ More on a row.
   - NEW: the tap moves focus off the draft, so the keyboard closes. The page uses `interactive-widget=resizes-content` (index.html:6), so `.messages` grows. The browser clamps `scrollTop` and ResizeObserver follow-bottom runs (scroll.js:59-61). The resulting `scroll` event calls `closePopup()` (actions.js:404-405), so the sheet closes. The same event can pass the 50 px threshold and hide the bar (actions.js:406).
   - OLD (target): the same keyboard close happened (App:11 has the same viewport rule), but no scroll rule closed the menu, and `dismissActions` skipped rows with `.menu-open` (chat.js:1205). The sheet stayed open.
   - OLD App:11, chat.js:1205, MI:572-579. NEW index.html:6, scroll.js:59-61, actions.js:404-407.
   - Confidence: needs a device check. The scroll-closes-popup rule is certain from code. The device check must confirm that the keyboard close produces a scroll event on `.messages`.

#### Visible

5. **A-05 Desktop: the full reaction picker hides the row's action bar and highlight.**
   - Trigger and input: mouse. The user clicks the smiley "Add Reaction" button on a row.
   - NEW: no code sets `.picker-open` (only `.menu-open` exists, actions.js:84, 275). The picker backdrop lives in `.chat-container` (pickers.js:16-20, 92-98), outside the row, so the row loses `:hover`. The bar fades and the row background clears while the picker is open. The picker floats with no visible anchor.
   - OLD (target): `ToggleEmojiPicker` adds `picker-open` to the row (MI:11, 508-515). CSS keeps the bar visible and the row highlighted (MIcss:434-439, 520-524). The old backdrop sat inside the row, so the row also stayed `:hover` (MI:233-237).
   - OLD MI:11, 233-237, 508-515; MIcss:434-439, 520-524. NEW actions.js:84, 275, 289; pickers.js:16-20, 92-111; messages.css:459-463, 536-539.
   - Confidence: read from code. On phones the bar stays because of `.touch-actions` and the guard at actions.js:350, so phones match OLD.

6. **A-06 Edit does not focus the edit box on touch-capable devices.**
   - Trigger and input: (a) touch on iOS with the composer keyboard open, tap Edit; (b) mouse on a touchscreen laptop, click Edit.
   - NEW: `if (!isTouchDevice()) input.focus()` (actions.js:245). (a) The tap on a button does not blur the draft on iOS, so the keyboard stays on the composer and typed text lands in the draft. (b) The laptop user must click into the box before typing. Escape and Enter do nothing until then.
   - OLD (target): `setupEditBox` always focused the box and put the caret at the end (chat.js:533-536). With the keyboard already up, focus moved to the edit box. Without it, the box showed the focus border and a tap raised the keyboard.
   - OLD chat.js:512-558. NEW actions.js:245-246; composer.js:8.
   - Confidence: (b) read from code. (a) needs a device check.

7. **A-07 Save shows the old text for a moment before the edited text.**
   - Trigger and input: any input. The user saves a changed edit.
   - NEW: `save()` awaits `mutate()`, which renders while the row is still frozen by the editor (actions.js:225, 77; messages.js:218). Then `cancel()` puts the old content nodes back (actions.js:213-216, 226). Then a second `render()` starts (actions.js:227), and its first step awaits IndexedDB (app.js:682) before the row is rebuilt.
   - OLD (target): `SaveEdit` changes the message and clears `editingMessageId` in one render (CB:536-545). The editor turns into the edited text in one step.
   - OLD CB:536-545, MI:436-438. NEW actions.js:219-228, 77; messages.js:218; app.js:682.
   - Confidence: the restore-before-async-render order is read from code. Whether a frame paints the old text needs a device check (likely on slow phones).

8. **A-08 Link detection in message text changed.**
   - Trigger and input: any input. A message contains `example.com`, `www.example.com`, or a URL followed by punctuation such as `https://x.com/a).`.
   - NEW: only `https?://[^\s<>]+` becomes a link (content.js:137). Bare domains and `www.` stay plain text. Trailing `.`, `,`, `)` and quotes become part of the href, so the link can open a wrong address.
   - OLD (target): links also covered `www.` and bare domains with common TLDs (href gets `https://`). Trailing punctuation, `)`, `]` and quotes were stripped (LPS:42-72, 356-358; ESR:160-204).
   - OLD LPS:42-72, 356-358; ESR:160-204. NEW content.js:132-146.
   - Confidence: read from code.

9. **A-09 Phone: the GIF favorite in the bar and the star on the GIF disagree.**
   - Trigger and input: touch, at 600 px width or less. The user taps the star in a GIF message's action bar.
   - NEW: `favorite()` toggles only the button it receives (gifs.js:36-45). The corner star on the GIF (the favorited indicator on phones) does not change (rich-media.js:84-88). The message object is a per-render clone (actions.js:428-429), so a later re-attach of the bar after a reaction update rebuilds the star from the stale snapshot value (messages.js:219-224; actions.js:295-303). Other messages with the same GIF keep the old state.
   - OLD (target): both stars render from `GifService.IsFavorite` on every render (MI:137, 163-169, 259-271), so they always agree.
   - OLD MI:137, 163-169, 259-271. NEW gifs.js:36-45; rich-media.js:84-88; actions.js:295-303; messages.js:219-224.
   - Confidence: the indicator mismatch is read from code. The revert after a reaction update needs a device check.

10. **A-10 Touchscreen laptops: two action bars at once.**
    - Trigger and input: mixed touch and mouse on a device that reports `(hover: hover)`. A finger tap on row A, then the mouse moves over row B.
    - NEW: row A keeps `.touch-actions` (messages.css:948-952). Row B shows its bar from the hover rule (messages.css:453-458). Both bars show. This is the shape of the known fix (3), on hybrid devices.
    - OLD (target): only `:hover` reveals (MIcss:434-439). The tap and the mouse move the same hover, so one bar shows.
    - OLD MIcss:434-439. NEW messages.css:453-458, 948-952; actions.js:393.
    - Confidence: needs a device check.

11. **A-11 Phone: the bar can stay on screen with no idle fade.**
    - Trigger and input: touch. The user opens the reaction picker, the More menu, or the Delete dialog and keeps it open for more than 5 s (for example, searching for an emoji). Then the user picks, copies, or cancels.
    - NEW: the 5 s timer starts at the tap's click in a row (actions.js:394). If it fires while a popup or picker is open, `dismiss()` returns early (actions.js:349-351). Nothing starts it again. Taps inside the body-level popups are not in a row, so they do not re-arm it (actions.js:371-373). The bar stays until the next touch outside the rows or a scroll of more than 50 px.
    - OLD (target): every `touchstart` inside the row re-armed the timer (chat.js:1227-1229). The old popups, backdrops and dialog were inside the row (MI:233-237, 292-365), so the bar faded 5 s after the last tap.
    - OLD chat.js:1199-1229; MI:233-237, 292-365. NEW actions.js:346-353, 366-397.
    - Confidence: read from code.

#### Minor

12. **A-12 Save with empty text does nothing.** Any input, the edit box is empty or blank, Save or Enter. NEW: `save()` returns and the editor stays open (actions.js:220). OLD (target): the editor closes and the message stays unchanged (MI:436-438, CB:536-543). Confidence: read from code.

13. **A-13 iOS: the first tap on a not-yet-active row now acts.** Touch on iOS, a tap on a pill, link, image or reply preview in a row whose bar is hidden. NEW: the tap acts at once and the bar appears (actions.js:382-397; hover reveal is off on touch, messages.css:453-458). OLD: the `:hover` reveal of the bar made iOS swallow that first click (the 78065ddd commit message states this), so the first tap only showed the bar and a second tap acted (MIcss:434-439). Confidence: needs a device check. This OLD behaviour is probably not wanted; a decision is needed.

14. **A-14 The first row and the first pending row lose their top margin.** Any input. NEW: rows are children of `#timeline` and `#pending` (index.html:226-227), so `.message-group:first-child { margin-top: 0 }` matches. It ties with `.has-header` on specificity and wins by order (messages.css:106-110, 171-173). The oldest loaded message sits about 17 px higher. A queued own message with a header has no gap above it and jumps 17 px when it moves into `#timeline`. OLD: the same rule was dead because the history note and spacer always came first in `.messages-flow` (RC:17-46; MIcss:103-105). Confidence: read from code.

15. **A-15 Reply-preview jump timing and scroll method changed.** Tap or click on a reply preview. NEW: `scrollIntoView({block:'center', behavior:'smooth'})`, which also scrolls ancestor containers. The highlight starts with the scroll and ends 2 s later. A target that is not loaded is fetched, or a notice appears (history.js:222-233). OLD: a 600 ms ease-out animation of `.messages.scrollTop` only. The highlight started after arrival and lasted 2 s. An unloaded target did nothing (chat.js:1290-1320; CB:580-586). Confidence: read from code.

16. **A-16 Reply to a missing message shows a placeholder line.** NEW: "Original message unavailable" with the connector line (messages.js:51, 55-71). OLD: no preview and no connector when the target could not be found (MI:11, 16-25; CB:572-578). Confidence: read from code.

17. **A-17 Edit keys during IME composition.** Keyboard with an IME (for example desktop Safari). NEW: Enter saves when `isComposing` is false, with no `keyCode 229` check, and Escape cancels the edit during composition (actions.js:254-262). OLD: both keys were ignored while composing or on `keyCode 229` (chat.js:545). Confidence: read from code; impact needs a device check.

18. **A-18 Copy Text has no fallback.** NEW: only `navigator.clipboard.writeText`. On failure the menu stays open and an error notice shows (actions.js:318-321, 57). OLD: the menu closed first, then copy with an `execCommand('copy')` fallback for plain-http origins (MI:586-591; chat.js:979-992). Confidence: read from code.

19. **A-19 A row under edit stops updating.** NEW: the timeline skips the editing row (messages.js:218). New reactions, a new header, or a changed reply target show only after Save or Cancel. OLD: the row re-rendered live and kept the draft (MI:383-392, 477-489). Confidence: read from code.

20. **A-20 Keyboard use of popups and focus after a reaction.** Keyboard. NEW: the More menu and the picker go to the end of `<body>` (actions.js:87; pickers.js:97), so Tab from the More button does not reach them. A reaction update replaces the bar and the pills (messages.js:148, 219-224), so a focused button loses focus. OLD: the menu and the picker were rendered inside the row after the bar (MI:292-324), and Blazor kept the button elements. Confidence: read from code.

21. **A-21 Phone: Reply hides the bar at once.** Touch, view at the bottom, tap Reply. NEW: the keyboard opens, ResizeObserver follow-bottom scrolls (scroll.js:59-61), and the 50 px rule hides the bar (actions.js:406). OLD: no code scrolled on keyboard open, so the bar stayed until the 5 s idle fade (chat.js:1199-1221). Confidence: needs a device check.

22. **A-22 Reaction tooltip name order.** Mouse hover on a pill. NEW: names in alphabetical order (OfflineSnapshotService.cs:117-118), and a local toggle appends your name at the end (actions.js:447-448). OLD: names in reaction order (Rx:31-33; MI:225). Confidence: read from code.

23. **A-23 "(edited)" spacing.** NEW: text `' (edited)'` with a leading space inside a `pre-wrap` block (messages.js:101; messages.css:218). OLD: `(edited)` with no leading space (MI:191-194). Both add `margin-left: 4px`. Confidence: read from code.

24. **A-24 Reply preview is now a `<button>` with browser padding.** NEW: `.reply-preview` overrides `padding-left` only, so the default 1 px top/bottom padding remains (messages.js:57; messages.css:117-127, 986-991). The curved connector uses fixed offsets (messages.css:150-161) and can miss the avatar centre by 1 to 2 px. OLD: a `<div>` (MI:18). Confidence: needs a device check.

25. **A-25 Small cosmetic changes.** The Delete dialog tip shows "Shift" without `<kbd>` styling (actions.js:170-174 vs MI:358). The smiley picker icon is 18 px instead of 20 px (actions.js:46 vs MI:250). Confidence: read from code.

### Mechanism pairs

- **P-01 More menu close: document `pointerdown` plus an inert backdrop.** The listener closes at finger-down (actions.js:108-111). The backdrop has no handler (actions.js:85-86). OLD had one mechanism, the backdrop click (MI:303). Leads to A-01.
- **P-02 Popup close on every list scroll.** `closePopup()` in the scroll listener (actions.js:404-405). OLD made no such call. Leads to A-03 and A-04.
- **P-03 Bar visibility: hover rule, row class and container class, gated by a device test.** Hover reveal (messages.css:453-458), `.touch-actions` (messages.css:948-952; actions.js:390-393), and `.scroll-dismissing` with `!important` (messages.css:72-76; actions.js:354). `isTouchDevice()` (composer.js:8) asks "can this device touch", not "was this input a touch". OLD had `:hover` plus `.scroll-dismissing`, armed only by real touches (chat.js:1227-1229). Leads to A-02 and A-10.
- **P-04 Row highlight and bar on touch use two states.** The row background follows sticky `:hover` and is not limited to hover devices (messages.css:102-104). The bar follows `.touch-actions` (messages.css:948-952). They agree when the tap's click reaches the scroller (actions.js:382-397). They can disagree when iOS gives `:hover` but swallows the click, for example on a GIF whose hover reveals the uploader label (messages.css:387-389). OLD used `:hover` for both (MIcss:11-13, 434-439). No visible difference proven; needs a device check.
- **P-05 Reaction-picker open state has no CSS class.** The guard is a DOM query (actions.js:350); no rule keeps the bar or highlight (messages.css:459-463, 536-539 still list `.picker-open`, which nothing sets). OLD had one class, `picker-open`, for bar, highlight and guard (MI:11; MIcss:434-439, 520-524; chat.js:1205). Leads to A-05.
- **P-06 GIF favorite has two buttons with separate DOM state.** Bar star (actions.js:295-303) and corner star (rich-media.js:84-88), each toggled alone (gifs.js:42-43). OLD rendered both from one server value (MI:137, 264). Leads to A-09.
- **P-07 `setSelectionRange` on an unfocused textarea.** On touch the code skips `focus()` but still calls `input.setSelectionRange(...)` (actions.js:245-246). OLD called it only after `focus()` (chat.js:535-536). Some WebKit versions focus or scroll on this call; needs a device check.
- **P-08 Focus restore after a row move: `focused?.focus({ preventScroll: true })`.** (messages.js:240-246). OLD never moved a focused row, because Blazor `@key` kept elements in place (RC:53-56). NEW moves a row when a pending row under edit is accepted (messages.js:207-216) and when the message above it is deleted. On iOS the blur and refocus can drop or re-raise the keyboard during an edit; needs a device check.
- **P-09 `scrollIntoView` for the reply jump.** (history.js:230). It scrolls every scroll ancestor, the document included. OLD set `.messages.scrollTop` only (chat.js:1298-1311). Leads to A-15.
- **P-10 Follow-bottom ResizeObserver plus the 50 px dismiss rule.** (scroll.js:59-61; actions.js:406). Keyboard open and close now create list scroll events that OLD never created. Leads to A-04 and A-21.
- **P-11 One Escape key, five document listeners.** actions.js:398-403, pickers.js:115-117, profiles.js:100-102, warnings.js:40-42, plus the edit box handler that stops propagation (actions.js:255-257). One press closes every open layer. OLD handled Escape per element (MI:329, 619-625; chat.js:547). No visible difference proven.
- **P-12 `stopPropagation()` on every bar, menu and dialog button.** (actions.js:55-56). OLD stopped it only on the picker and More buttons (MI:249, 283). The capture-phase click listener on the scroller exists to see these clicks (actions.js:380-397). No visible difference proven.
- **P-13 Idle timer guard without re-arm.** `dismiss()` returns while a popup or picker is open (actions.js:346-353), and only a row click arms the timer (actions.js:394). OLD re-armed on every touch in the row (chat.js:1227-1229). Leads to A-11.
- **P-14 Two renders around one Save.** `mutate()` renders while the row is frozen (actions.js:77), `cancel()` restores old nodes (actions.js:213-216), and a second `render()` runs (actions.js:227). OLD had one render (CB:536-545). Leads to A-07.
- **P-15 Reaction updates replace the bar and the pills.** (messages.js:148, 219-224). OLD's Blazor diff kept the elements. A press that starts on a button before the swap and ends after it loses its click, and keyboard focus is lost. Leads to A-20.
- **Checked and same:** Reply focuses the draft in the gesture (composer.js:290-295 vs chat.js:1272-1277). The Delete button gets focus (actions.js:195 vs MI:546-552). The edit controls scroll into view (actions.js:250 vs chat.js:519-526).

### State table

| ID | State / trigger | Input | OLD behaviour (file:line) | NEW behaviour (file:line) | Verdict |
|---|---|---|---|---|---|
| S01 | Bar reveal on hover | mouse, desktop | `:hover` shows bar (MIcss:434-439) | `@media (hover:hover)` hover rule (messages.css:453-458) | same |
| S02 | Bar reveal on hover after a scroll or Escape | mouse on touch-capable device | bar shows; dismiss needs a touch to arm (chat.js:1213-1246) | `.scroll-dismissing` hides all bars (actions.js:345-354, 398-407; messages.css:72-76) | different (A-02) |
| S03 | Bar reveal on tap | touch | sticky `:hover` after the tap (MIcss:434-439) | capture click on scroller adds `.touch-actions` (actions.js:382-397) | same (78065ddd) |
| S04 | First tap on a hidden row | touch, iOS | click swallowed, only the bar shows (78065ddd message) | tap acts and bar shows (actions.js:382-397) | different (A-13) |
| S05 | Scroll gesture that starts on a row | touch | `touchstart` re-shows last tapped row's bar until 50 px (chat.js:1213-1229, 1240-1246) | no reveal; `pointercancel` clears the row (actions.js:366-379) | different (OLD quirk not kept) |
| S06 | Bar reveal on keyboard focus | keyboard Tab | no rule; buttons focusable but invisible (MIcss:428-431) | same (messages.css:444-447) | same |
| S07 | Bar hide after 5 s idle | touch | timer from last touch in row (chat.js:1197, 1213-1221) | timer from last row click (actions.js:394) | same |
| S08 | Idle fade after a popup open > 5 s | touch | re-armed by touches in in-row popups (chat.js:1227-1229; MI:233-365) | never re-armed (actions.js:346-353, 394) | different (A-11) |
| S09 | Bar hide on scroll > 50 px | touch | only after a touch armed it (chat.js:1240-1246) | always on, from last row touch (actions.js:372, 404-407) | same on phones; different on hybrid (A-02) |
| S10 | Bar hide on tap outside rows | touch | hover moves away, 0.1 s fade (MIcss:430) | `dismiss()`, 0.3 s fade (actions.js:373; messages.css:72-76) | different (trivial) |
| S11 | Bar hide on Escape | keyboard on touch device | none | `dismiss()` (actions.js:398-402) | new-only |
| S12 | Bar hide on navigation | any | `cleanupScrollDismiss` (chat.js:1252-1261) | `actions.reset()` (actions.js:421-425; app.js:678-680); rows replaced | same |
| S13 | Bar while a row is edited | any | bar not rendered (MI:240) | `display:none` (messages.css:940-952) | same (000e0d25) |
| S14 | Bar after Reply on phone | touch | stays until idle (chat.js:1199-1221) | follow-bottom scroll hides it (scroll.js:59-61; actions.js:406) | different (A-21) |
| S15 | Bar position and overflow at <= 600 px | touch | right 8px, top -12px, can overflow at 320 px (MIcss:889-899) | same (messages.css:848-857) | same |
| S16 | Buttons, other user's text | any | 3 quick, divider, picker, Reply, More/Copy (MI:240-288) | same (actions.js:278-338) | same |
| S17 | Buttons, own text | any | + Edit; More: Copy, Delete (MI:273-288, 305-322) | same (actions.js:304-333) | same |
| S18 | Buttons, own image/video/GIF | any | no Edit (`!HasMedia`, MI:273) | no Edit (actions.js:304) | same |
| S19 | Buttons, GIF message | touch <= 600 px | bar star shown only on phones (MI:259-272; MIcss:507-518, 904-906) | same (actions.js:295-303; messages.css:524-534, 861-863) | same |
| S20 | Buttons, System message | any | no bar (MI:240) | no bar (actions.js:273) | same |
| S21 | Buttons, own pending message | any | ghost text, no bar (chat.js:1929-1934) | Edit (text) and More (actions.js:278-338; app.js:574-576) | new-only |
| S22 | Quick reactions: which, how many, order | any | top 3 from reaction history, padded ❤️😂👍 (CB:721-739) | same server rule (Yap/Offline/OfflineContent.cs:82-86); defaults until catalog loads (content.js:11-13, 96) | same |
| S23 | Picker button icon | any | 20 px (MI:250) | 18 px (actions.js:46) | different (A-25) |
| S24 | Full picker position | mouse, desktop | below or above anchor, width assumed 340 (chat.js:904-939) | measured size (pickers.js:99-111) | same |
| S25 | Full picker on phone | touch | bottom sheet 50vh, dim backdrop (MIcss:951-969) | same (pickers.css:663-681, 683-698) | same |
| S26 | Bar and highlight while picker open | mouse | kept by `picker-open` (MI:11; MIcss:434-439, 520-524) | lost (pickers.js:16-20; no class) | different (A-05) |
| S27 | Bar while picker open | touch | `picker-open` plus guard (chat.js:1205) | `.touch-actions` plus guard (actions.js:350) | same |
| S28 | Close picker by backdrop | any | `CloseEmojiPicker` (MI:234-237) | `closePickers` on click (pickers.js:20) | same |
| S29 | Close picker by Escape | keyboard | none | closes (pickers.js:115-117) | new-only |
| S30 | Pick emoji in full picker | any | picker closes, reaction toggles (MI:643-647) | same (content.js:348-351; actions.js:289) | same |
| S31 | Pill tap or click | any | toggles own reaction after round trip (MI:223-224) | toggles at once (actions.js:340-342, 264-270) | same |
| S32 | Fast double tap on a pill | touch | two server toggles (CB:524-530) | second tap may reuse the stale message and add again (actions.js:266-268) | unsure (timing) |
| S33 | Pill hover names | mouse | usernames in reaction order (Rx:31-33; MI:225) | usernames sorted (OfflineSnapshotService.cs:117-118; messages.js:161) | different (A-22) |
| S34 | Own-reaction styling | any | `.reacted`, case-insensitive match (MI:222; MIcss:572-584) | `.reacted`, exact match (messages.js:154; messages.css:569-579) | same (unsure for mixed-case names) |
| S35 | Pill order | any | first use of each emoji (Rx:31-33) | same (OfflineSnapshotService.cs:117) | same |
| S36 | Scroll to show a new reaction near bottom | any | within 100 px (CB:490-509) | follow within 80 px (scroll.js:5-6, 59-61) | same |
| S37 | Reply: composer focus, keyboard | touch, mouse | focus in gesture (chat.js:1272-1277) | focus in gesture (composer.js:290-295) | same |
| S38 | Reply-target highlight | any | `reply-target` (MI:11; MIcss:98-101) | `reply-target`, re-applied after render (composer.js:44-54; app.js:713) | same |
| S39 | Reply preview tap: jump and highlight | any | custom 600 ms scroll, highlight after (chat.js:1290-1320) | smooth `scrollIntoView`, highlight at start (history.js:222-233) | different (A-15) |
| S40 | Reply preview, target missing | any | no preview (MI:16; CB:572-578) | "Original message unavailable" (messages.js:55-71) | different (A-16) |
| S41 | Who can edit which message | any | own, no media (MI:273) | own, no images/videos/GIF (actions.js:304) | same |
| S42 | Edit focus and caret on desktop | mouse | focus, caret at end (chat.js:535-536) | same (actions.js:245-246) | same |
| S43 | Edit focus on touch-capable devices | touch, hybrid mouse | always focused (chat.js:535) | not focused (actions.js:245) | different (A-06) |
| S44 | Edit autogrow and keep Save visible | any | autogrow to 200 px, `scrollIntoView` (chat.js:516-531) | same (actions.js:247-253) | same |
| S45 | Enter / Shift+Enter in edit | keyboard; touch | desktop Enter saves, touch Enter newline (chat.js:550-554) | same (actions.js:258-261) | same (IME: A-17) |
| S46 | Escape in edit | keyboard | cancels (chat.js:547-549) | cancels (actions.js:255-257) | same (IME: A-17) |
| S47 | Save with unchanged text | any | closes, no request (MI:436-438) | same (actions.js:221-224) | same |
| S48 | Save with empty text | any | closes, message unchanged (CB:536-543) | nothing happens (actions.js:220) | different (A-12) |
| S49 | Save with changed text | any | one render to new text (CB:536-545) | old text flash, then new (actions.js:225-227) | different (A-07) |
| S50 | Cancel button | any | closes (MI:57) | closes and re-renders (actions.js:231, 213-218) | same |
| S51 | Edit another message while editing | any | first draft discarded (CB:534) | same (actions.js:199) | same |
| S52 | Edit hint text | any | shown on desktop, hidden on touch (MI:58; MIcss:662-666) | same (actions.js:232; messages.css:646-649) | same |
| S53 | Incoming update while editing | any | row updates, draft kept (MI:383-392, 477-489) | row frozen until edit ends (messages.js:218) | different (A-19) |
| S54 | Message deleted elsewhere while editing | any | editor disappears (CB:464-482) | editor disappears (messages.js:250-257) | same |
| S55 | Edited label | any | `(edited)` (MI:191-194) | `' (edited)'` (messages.js:101) | different (A-23) |
| S56 | Edit length limit | any | none in the box | `maxLength` (actions.js:206) | new-only |
| S57 | More menu items | any | Copy if text, Delete if own (MI:305-322) | same (actions.js:313-333) | same |
| S58 | More menu position | mouse, desktop | below or above, clamped (chat.js:945-975) | same (actions.js:89-105) | same |
| S59 | More menu on phone | touch | bottom sheet, dim backdrop (MIcss:971-990) | same CSS (messages.css:898-915) | same |
| S60 | Close More by tapping outside | touch | backdrop click consumes the tap (MI:303) | closes at pointerdown; tap goes through (actions.js:108-122) | different (A-01) |
| S61 | Close More or Delete dialog by list scroll | any | no | yes (actions.js:404-405) | different (A-03, A-04) |
| S62 | Close More by Escape | keyboard | no | yes (actions.js:398-400) | new-only |
| S63 | Copy Text | any | close, copy with fallback (MI:586-591; chat.js:979-992) | copy without fallback, then close (actions.js:318-321) | different (A-18) |
| S64 | Delete dialog preview | any | avatar, name, time, 3-line text, image/poster/GIF label (MI:333-357) | same, plus file names for uploads (actions.js:133-165) | same |
| S65 | Delete dialog Cancel or backdrop click | any | closes (MI:329, 360, 608-611) | closes (actions.js:179, 192-194) | same |
| S66 | Delete dialog Escape | keyboard | closes (MI:619-625) | closes (actions.js:398-400) | same |
| S67 | Shift+click Delete skips dialog | mouse | yes (MI:597-602) | yes (actions.js:126) | same |
| S68 | Delete dialog focus | any | Delete button (MI:546-552) | Delete button (actions.js:195) | same |
| S69 | Delete dialog stacking | any | inside the flow; send button shows through (Yap/wwwroot/app.css:174-187) | on `<body>`, covers all (actions.js:185) | different (improvement) |
| S70 | Delete dialog Shift tip | mouse | `<kbd>Shift</kbd>` (MI:358) | plain text (actions.js:170-174) | different (A-25) |
| S71 | Row hover background | mouse; sticky on touch | `:hover` (MIcss:11-13) | same (messages.css:102-104) | same |
| S72 | Row background while More open | any | `menu-open` (MIcss:520-524) | `menu-open` (actions.js:84, 275; messages.css:536-539) | same |
| S73 | Jump highlight | any | after scroll, 2 s (chat.js:1313-1316) | at scroll start, 2 s (history.js:231-232) | different (A-15) |
| S74 | Pending (queued or sending) styling | any | ghost text (chat.js:1929-1934) | full row, text at 0.75 opacity (messages.css:936-939; app.js:586) | new-only |
| S75 | Failed send, Retry, Cancel upload | any | ghost disappears after 15 s (chat.js:1921-1926) | Failed label, Retry, Cancel upload (app.js:591-621) | new-only |
| S76 | Header grouping rule | any | new author, reply, or 1 h gap (CB:240-257) | same (messages.js:29-36) | same |
| S77 | Header on a System message | any | suppressed in rooms (RC:49-50) | not suppressed (messages.js:72-90) | unsure (no code creates "System" messages) |
| S78 | Top margin of first row and first pending row | any | 17 px (MIcss:16-20; rule at 103-105 never matched) | 0 (messages.css:171-173) | different (A-14) |
| S79 | Header time text refresh | any | only on re-render (MI:409-426) | every 60 s (app.js:742-748) | new-only |
| S80 | Link click in a row | any | new tab, noopener (ESR:193) | same (content.js:139-142) | same (detection: A-08) |
| S81 | Link tap also reveals the bar | touch | yes, via hover (MIcss:434) | yes, via click (actions.js:382-397) | same |
| S82 | Long-press on a row | touch | no bar (no tap, no hover) | no bar (no click; actions.js:377-379) | same |
| S83 | GIF favorite from the bar | touch | both stars update (MI:137, 163-169, 259-271) | only the tapped star (gifs.js:42-43) | different (A-09) |
| S84 | Mouse hover plus finger tap on two rows | hybrid | one bar (MIcss:434-439) | two bars (messages.css:453-458, 948-952) | different (A-10) |
| S85 | Tab to More menu or picker items | keyboard | next in DOM (MI:292-324) | at end of body (actions.js:87; pickers.js:97) | different (A-20) |
| S86 | Focus after a reaction update | keyboard | kept | lost (messages.js:148, 219-224) | different (A-20) |
| S87 | Bot "install it" link | touch, mouse | span with click and touchend (MI:185) | button with click (messages.js:96-97) | same |
| S88 | Reply preview look | any | `<div>` (MI:18) | `<button>`, default padding (messages.js:57; messages.css:986-991) | different (A-24) |

---

## Appendix B: Composer and pickers


### Differences

Key to citations. OLD files are at commit 0cb67422: `chat.js` = Yap/wwwroot/js/chat.js, `MI.razor` / `MI.css` = Yap/Components/MessageInput.razor(.css), `EP.*` = EmojiPicker.razor(.css), `GP.*` = GifPicker.razor(.css), `CP.*` = CombinedPicker.razor(.css), `RoomChat.razor`, `ChatBase.cs`, `App.razor`, `old app.css` = Yap/wwwroot/app.css. NEW files are in Yap/wwwroot/chat-client/ unless the path says otherwise. The three phone regressions fixed today and the removed post-send focus are not repeated here.

Severity order: breaks use, then visible, then minor. Inside one level, the most likely trigger comes first.

#### Breaks use

**D01. A file dropped outside the messages column opens in the browser tab.**
- Trigger and input: mouse drag of an image or video from the desktop. The drop lands on the header, the sidebar, a modal, or anywhere while the draft is disabled (read-only channel, draft restore in progress).
- NEW: nothing cancels `dragover`/`drop` at document level. The browser does its default action and replaces the chat page with the dropped file. Drafts survive in IndexedDB, but the user leaves the app.
- OLD (target): document-level `dragover` and `drop` listeners always called `preventDefault()`, so a drop anywhere on the page did nothing. Only the composer accepted files.
- OLD: chat.js:320-337 (preventDefault at 327-328). NEW: composer.js:248-260 (preventDefault only on `.messages-container`, and only when the draft is enabled).
- Confidence: read from code (browser default action for an uncancelled file drop).

#### Visible

**D02. With a picker open, the whole input box now sits above the backdrop.**
- Trigger and input: emoji or GIF picker open; touch or mouse.
- NEW: the backdrop is a child of `.chat-container` (z 999). The `.input-box` is lifted to z 1000 in the same stacking context, so the box background, the + button, the textarea and the toggles all paint above the backdrop. Effects: on a phone the box is not dimmed. Tapping + opens the file chooser while the sheet stays open (it closes only after files queue). On desktop, a click on the GIF toggle while the emoji picker is open switches directly to the GIF picker.
- OLD (target): the backdrop lived inside `.emoji-toggle-wrapper`, inside the lifted `.input-box` stacking context. It painted above the box background, the + label and both toggles. Only the textarea (lifted again to z 1000 inside the box), the picker and the Send button were above it. On a phone the box and its buttons were under the 50% dim. A tap on + or on the other toggle only closed the picker.
- OLD: MI.razor:125 and 142 (backdrop placement), MI.css:313-321, 343-353, 524-526. NEW: pickers.js:16-20, composer.css:296-305, pickers.css:623-630, 653-655.
- Confidence: read from code (stacking order). The look on a phone needs a device check.

**D03. Text typed during the send window stays in the box together with the sent text.**
- Trigger and input: send by Enter or the Send button, then type again before the IndexedDB enqueue resolves. Keyboard or touch.
- NEW: the draft clears only after `await draftWrite` and `await storage.enqueue(...)`, and only if the value is unchanged. If the user typed in that window, the sent text stays in the box with the new characters after it. Send is enabled again, so a second Enter sends the old text again.
- OLD (target): the click capture listener cleared the textarea at once, in the same task as the click. New typing went into an empty box.
- OLD: chat.js:1902-1907. NEW: composer.js:113-126, storage.js:211-214.
- Confidence: read from code. The window length (IndexedDB plus the account lock) needs a device check.

**D04. Upload progress and upload failure moved out of the typing strip.**
- Trigger and input: upload with +, paste, or drop.
- NEW: a pending message row in the timeline shows the file names, a browser-default `<progress>` bar (no CSS rule exists for `.upload-progress` or `.attachment-preview`) and a "Cancel upload" button. A failure shows "Failed", the error text and Retry in that row. No modal.
- OLD (target): the typing strip above the composer showed "Uploading... N%" or "File n/m, N%" with a styled bar. A failure showed the "Upload failed" modal; a partial failure showed "n of m files uploaded".
- OLD: MI.razor:37-56, 567-577, 605-609. NEW: app.js:591-621, composer.js:212-216 (warnings only for local validation).
- Confidence: read from code.

**D05. A file upload during a reply is sent as a reply and clears the reply bar.**
- Trigger and input: reply bar open, then +, paste or drop.
- NEW: `sendExtra` attaches `replyToMessageId` and `finishReply` closes the reply bar.
- OLD (target): file messages were sent without a reply id, and the reply bar stayed open for the next text message. (GIF picks did reply and clear the bar in both versions.)
- OLD: MI.razor:595-600. NEW: composer.js:140-155.
- Confidence: read from code.

**D06. The warning modal moves focus, and closing it can open the phone keyboard and close a picker.**
- Trigger and input: a rejected paste, drop or upload (size, type, count). Most visible on a phone after pasting an unsupported or oversized file into the focused draft.
- NEW: `warn()` focuses "Got It" (the keyboard closes). Dismiss calls `previous.focus()`. When `previous` is the draft, that focus runs inside the tap, so the keyboard opens again, and the draft focus listener closes any open picker.
- OLD (target): the modal made no focus calls. Focus stayed where the tap left it.
- OLD: MI.razor:24-34. NEW: warnings.js:4, 26-31, 38; pickers.js:113.
- Confidence: read from code. Keyboard effect needs a device check.

**D07. The draft now has maxlength (default 4000).**
- Trigger and input: paste or type a long text.
- NEW: the browser silently cuts the text at `snapshot.maxTextLength` (4000 by default).
- OLD (target): no maxlength on the textarea.
- OLD: MI.razor:112-113. NEW: composer.js:70.
- Confidence: read from code.

**D08. The + chooser uses an extension list instead of `image/*,video/*`.**
- Trigger and input: tap + on a phone.
- NEW: `accept` is `snapshot.allowedExtensions.join(',')` (for example `.png,.jpg,...,.mkv`) and the input opens through `input.click()` on a `[hidden]` input.
- OLD (target): `accept="image/*,video/*" multiple` on an InputFile inside a `<label>`. Phones offered the photo library and the camera.
- OLD: MI.razor:95-102. NEW: composer.js:218-224, index.html:271-277.
- Confidence: needs a device check. The OS chooser can differ (camera entry, photo picker against document picker, HEIC conversion).

**D09. Drops on the message list now upload, without a highlight there.**
- Trigger and input: mouse drag of files over the message list.
- NEW: the drop target is the whole `.messages-container`. The "Drop images here" highlight appears only while the pointer is over the composer.
- OLD (target): only the composer accepted the drop, and the highlight marked exactly that area.
- OLD: chat.js:339-385. NEW: composer.js:238-258.
- Confidence: read from code.

**D10. A failed GIF upload from the picker closes the picker and shows no modal.**
- Trigger and input: + inside the GIF picker, then the server rejects or the upload fails.
- NEW: when the outbox job leaves the busy state (failed jobs are excluded from "busy"), `onOutgoing` closes the picker. The failure appears as a failed pending row.
- OLD (target): the picker stayed open behind a "Couldn't send GIF" modal, ready for a retry.
- OLD: GP.razor:694-703, MI.razor:525-531. NEW: gifs.js:503-513, app.js:611-621.
- Confidence: read from code.

**D11. Escape closes the picker, and one Escape closes the warning modal too.**
- Trigger and input: Escape key anywhere on the page.
- NEW: a document keydown listener closes any picker. A second document listener dismisses the warning. One press does both.
- OLD (target): Escape did nothing to the pickers or the warning modal.
- OLD: none (chat.js has Escape only in the gallery and the edit box, 185 and 547). NEW: pickers.js:115-117, warnings.js:40-42.
- Confidence: read from code.

**D12. Send is a disabled button while queueing and for whitespace-only drafts.**
- Trigger and input: whitespace-only draft, or the short window after a send.
- NEW: `disabled` dims the visible button (`.send-button:disabled`) and removes its glow. A tap on a disabled button may not deliver `pointerdown`, so the keep-focus `preventDefault()` may not run and the keyboard may close.
- OLD (target): the button was never disabled. With whitespace it looked active and the server guard ignored it. After a send it collapsed at once. Its mousedown guard always ran.
- OLD: MI.css:150-174, MI.razor:465, chat.js:461-472, 1905. NEW: composer.js:68, 110-111, 131-132, 277.
- Confidence: the look is read from code. The keyboard effect needs a device check.

**D13. The message list stays pinned to the bottom when the composer area changes size.**
- Trigger and input: phone keyboard opens (Chrome, `resizes-content`), the picker sheet opens or closes, the draft grows.
- NEW: a ResizeObserver on `.messages` and `.messages-flow` scrolls to the bottom while the view is "following".
- OLD (target): no resize handling. The list kept its scrollTop, so the newest lines moved out of view behind the composer or sheet.
- OLD: none (no resize, ResizeObserver or visualViewport code in chat.js). NEW: scroll.js:57-61, 7-15.
- Confidence: read from code. Keyboard behaviour needs a device check.

**D14. The pending echo is a full message row.**
- Trigger and input: any send.
- NEW: a full message row (avatar or header rules, rich text, links) with the class `pending-message`, content opacity 0.75.
- OLD (target): a plain-text ghost, indented to the text column, whole ghost at opacity 0.55, removed when the real row arrived.
- OLD: chat.js:1929-1934, old app.css:338-343. NEW: app.js:574-588, messages.css:936-939.
- Confidence: read from code.

**D15. The read-only note lost its lock icon and gained a second text.**
- Trigger and input: open a channel without write permission, or a conversation that is not available offline.
- NEW: text only, "You do not have permission to send messages in this channel." (with a period). A new state shows "Connect to open this conversation." above a visible but disabled composer.
- OLD (target): lock icon plus "You do not have permission to send messages in this channel" (no period). No second state.
- OLD: MI.razor:11-20. NEW: composer.js:71-79, index.html:243.
- Confidence: read from code.

**D16. Drafts and replies are kept per conversation (new behaviour).**
- Trigger and input: switch rooms or DMs, reload the page.
- NEW: each conversation restores its own draft and reply bar from IndexedDB.
- OLD (target): a room-to-room switch reused the same textarea, so the same draft text and the same reply bar carried into the next room (a send there replied to a message from the previous room). A DM switch or a reload lost both.
- OLD: RoomChat.razor:162-190 (no reply reset), MI.razor:112-113, ChatBase.cs:562-570. NEW: composer.js:81-94, 296-319, app.js:674-714.
- Confidence: read from code.

**D17. Composer controls start disabled and are disabled during a draft restore.**
- Trigger and input: page load, conversation switch, no account.
- NEW: draft, +, GIF, emoji and Send are `disabled` in the HTML and until `restore()` finishes. Taps on them do nothing in that window.
- OLD (target): the controls were never disabled.
- OLD: MI.razor:95-169. NEW: index.html:254, 284, 288, 296, 305; composer.js:60-80, 296-319; app.js:705.
- Confidence: read from code. The window length needs a device check.

#### Minor

**D18. The placeholder is chosen once per conversation.**
- NEW: one random configured text per conversation; fallback "Type a message..." (the HTML default is "Type a message…").
- OLD (target): a new random text on every parent render (RoomChat re-renders on each incoming message), fallback "Say something…". DMs show "Message @name" in both.
- OLD: MI.razor:258-270, 378-385. NEW: app.js:669-671, content.js:98-112.
- Confidence: read from code.

**D19. The emoji insertion point can come from another conversation.**
- NEW: the caret is one module variable, captured on blur. After a switch to a conversation with a restored draft, the first emoji (draft not focused) goes to the old offset, clamped to the new length.
- OLD (target): the caret was stored on the textarea element. A fresh textarea (DM switch) had none and inserted at the end.
- OLD: chat.js:1175-1183, 1092. NEW: composer.js:24-32.
- Confidence: read from code.

**D20. An emoji replaces selected draft text.**
- NEW: `setRangeText(value, start, end)` replaces the selection captured on blur.
- OLD (target): the emoji went in at the selection start and the selected text stayed.
- OLD: chat.js:1092-1095. NEW: composer.js:29-31.
- Confidence: read from code.

**D21. The emoji category jump is instant and offset for the search bar.**
- NEW: `scrollIntoView({behavior:'instant'})`, then subtracts the sticky search height, so the header is visible.
- OLD (target): smooth scroll; the section header ended under the sticky search box.
- OLD: chat.js:1810-1818. NEW: content.js:272-281.
- Confidence: read from code.

**D22. GIF Browse and folder tiles pick a new preview on every open.**
- NEW: each open fetches the library and clears the preview cache, so the tile images change after the picker appears.
- OLD (target): previews were picked only when favourites or the server library changed.
- OLD: GP.razor:432-461, 392-400. NEW: gifs.js:402-419 (406).
- Confidence: read from code.

**D23. The GIF grid is rebuilt from scratch after the open-time fetch and after a star.**
- NEW: `render()` replaces every card. It runs when the library fetch returns (each open), when trending returns, and after each favourite change. A tap that starts before the rebuild and ends after it can be lost; previews can flicker.
- OLD (target): Blazor diffed the grid, so existing cards stayed in place. (OLD also re-rendered once per open for recents.)
- OLD: GP.razor:392-400, 715-731. NEW: gifs.js:230-367 (253), 402-419, 515-518, 529-537.
- Confidence: needs a device check.

**D24. The emoji grid rebuilds if the catalog arrives while the picker is open.**
- NEW: each connect calls `loadContent`, which bumps the revision and fires `chat-content`. An open emoji picker rebuilds all sections, and its scroll position can jump.
- OLD (target): the grid was static after mount.
- OLD: EP.razor:132-148. NEW: app.js:935-937, content.js:29-44, 287-297, pickers.js:131-134.
- Confidence: read from code.

**D25. The first picker open builds the picker inside the tap.**
- NEW: the emoji grid (and on a phone the GIF picker too) is created on the first tap.
- OLD (target): the panes were mounted hidden about 1.2 s after load, so the first open was a CSS flip.
- OLD: MI.razor:327-340. NEW: pickers.js:21-23, 36-73.
- Confidence: needs a device check (first-open delay on a slow phone).

**D26. Pasting a non-media file is blocked with a warning.**
- NEW: any clipboard item of kind `file` cancels the paste and goes to validation, which rejects it with "is not a supported image or video".
- OLD (target): only `image/*` and `video/*` items were taken; other pastes did their default action.
- OLD: chat.js:1336-1346. NEW: composer.js:261-269.
- Confidence: read from code.

**D27. GIF "Load more" button and keyboard-selectable cards (new behaviour).**
- NEW: a "Load more" button after provider results has no CSS rule, so it is a browser-default button. Cards have `tabindex=0` and send on Enter or Space.
- OLD (target): 24 results, no paging; cards were not keyboard reachable.
- OLD: GP.razor:526, 557-595. NEW: gifs.js:164-166, 186-191, 224-228.
- Confidence: read from code.

**D28. File count and type checks changed.**
- NEW: up to `maxFilesPerMessage` (20) per pick, extension check only, one message per rejected file.
- OLD (target): `GetMultipleFiles(10)` threw for more than 10 files and showed a generic "Upload failed". A file passed on extension OR MIME type.
- OLD: MI.razor:541, 605-609, chat.js:1370-1393. NEW: composer.js:174-207.
- Confidence: read from code.

**D29. Navigation closes an open picker.**
- NEW: `beginNavigation` closes pickers on every conversation switch (for example Android Back).
- OLD (target): a room-to-room switch kept the picker open in the next room. (A click on the sidebar hit the backdrop first in both versions.)
- OLD: RoomChat.razor:162-190 (no close). NEW: composer.js:296-297.
- Confidence: read from code.

### Mechanism pairs

**M01. Picker close on send has two paths that disagree.** The Send click closes the picker (pickers.js:114). Enter calls `send()` directly and does not close it (composer.js:278-289). OLD routed Enter through `.click()`, so one capture listener ran for both (chat.js:497, 1894-1895). On desktop a focused draft normally means no open picker, so no visible difference is proven. On a touch device with a hardware keyboard Enter is a newline in both.

**M02. The backdrop moved, and the old textarea lift stayed.** NEW appends the backdrop to `.chat-container` (pickers.js:16-20) but keeps the OLD rules that lift `.input-box` and the textarea (composer.css:296-305). The textarea lift is now redundant, and the box lift now raises the + button and toggles too. This is the cause of D02.

**M03. "Send dimmed" has two mechanisms.** CSS dims it with `:placeholder-shown` while a picker is open (composer.css:159-163). JS also sets `disabled` (composer.js:68). They disagree for a whitespace-only draft and during queueing (D12). The `disabled` state also decides whether the button receives the `pointerdown` that keeps the keyboard open (composer.js:277). OLD had only the CSS state (MI.css:170-174).

**M04. The keep-focus guard moved from mousedown to pointerdown.** OLD cancelled `mousedown` on Send (chat.js:469-471). NEW cancels `pointerdown` (composer.js:277). Both aim to keep the draft focused. Whether iOS WebKit suppresses the tap's focus change for a cancelled `pointerdown` needs a device check (Send with the keyboard open).

**M05. New focus() calls in the warning modal.** `okay.focus()` on show and `previous.focus()` on dismiss (warnings.js:38, 28). OLD made neither call (MI.razor:24-34). With the draft as `previous`, the second call opens the phone keyboard and fires the draft focus listener that closes pickers (pickers.js:113). See D06.

**M06. One Escape key, several document listeners.** pickers.js:115-117 (close picker), warnings.js:40-42 (dismiss warning), plus the edit box and gallery handlers elsewhere. All run on the same keydown. OLD had none for the composer. See D11.

**M07. Two closers for "GIF upload finished".** composer.js:210 closes at once when offline. gifs.js:511 closes when the outbox job stops being busy online. The split depends on `navigator.onLine` read at two different times. A failed job also counts as "not busy" and closes the picker (D10). OLD had one closer, the success callback (MI.razor:505).

**M08. The draft is disabled and enabled by several writers.** composer.js:69 (every update), 301 and 306 (navigation), 318 (restore), 331 (clear), and app.js:705. Disabling a focused textarea blurs it, so the phone keyboard closes. OLD never disabled the textarea. No visible difference is proven for normal flows, because a sidebar tap blurs the draft first. Android Back during typing is the case to check on a device.

**M09. Send is disabled while focused.** composer.js:110-111 disables Send during queueing. If Send had keyboard focus (Tab, then Enter or Space), the disabled button drops focus to `body`. OLD never disabled it. Desktop keyboard users only.

**M10. The draft clear is conditional and late.** composer.js:123-126 clears only when the value still equals the sent text, after two awaits; storage.js:212-214 keeps the stored draft on the same condition. OLD cleared synchronously in the click (chat.js:1905). See D03.

**M11. Typing stop on send rides on reply bookkeeping.** `live.stopTyping()` runs inside `finishReply`, which returns early when the account, channel or reply selection changed during the enqueue (composer.js:95-103). The live.js 500 ms timer is a second stopper (live.js:63-64). OLD stopped typing in `HandleSend` every time (MI.razor:479). Others can see the indicator up to about 3.5 s longer in the early-return case.

**M12. Two scroll-to-bottom paths after a send.** `onQueued` calls `scrolling.bottom()` (app.js:241-245), and the ResizeObserver fires again when the draft shrinks (scroll.js:57-61). OLD had one `scrollToBottom()` (chat.js:1907). No visible difference is proven.

**M13. The GIF grid is replaced, not updated.** `results.replaceChildren()` on every render (gifs.js:253), with a library fetch on every open (gifs.js:531-536). OLD diffed. See D23.

**M14. The emoji grid has a second refresh trigger.** `chat-content` calls `refresh()` while the picker is open (pickers.js:131-134, content.js:287-297), in addition to the per-open `opened()` (content.js:335-341). OLD refreshed only on open (EP.razor:159-165). See D24.

**M15. The file chooser opens by `click()` on a hidden input.** composer.js:168, 223 call `input.click()` on `#upload-files`, which has `hidden` and the global `[hidden]{display:none !important}` (chat.css:2-4). OLD used native `<label>` activation of the InputFile (MI.razor:95-102). Programmatic click on a display:none file input on iOS needs a device check.

**M16. The caret has one owner for all conversations.** composer.js:24 keeps `caret` in module scope; OLD kept it on the textarea element (chat.js:1181). See D19.

**M17. The draft resize timing changed.** NEW resizes synchronously on input and resets to `height:auto` after a send (composer.js:9-12, 125). OLD resized in requestAnimationFrame and reset to `44px` (chat.js:388-403). With `rows=1`, `min-height:44px` and border-box, both give 44 px. No visible difference is proven.

**M18. Checked and equal: reply focus and emoji search blur.** NEW `reply()` calls `draft.focus()` synchronously inside the click (composer.js:290-295); OLD focused in a capture-phase click listener (chat.js:1272-1277). The emoji cell blurs the search field on `pointerdown` in both (content.js:238-240, chat.js:1066-1074). These calls exist on both sides, so they are not new calls.

### State table

| ID | State / trigger | Input | OLD behaviour (file:line) | NEW behaviour (file:line) | Verdict |
|---|---|---|---|---|---|
| S01 | Textarea grows with content | keyboard, touch | rAF resize, cap 200 px (chat.js:388-396, 1122-1130) | sync resize, cap 200 px (composer.js:9-12, 85) | same |
| S02 | Max height and hidden scrollbar | all | max-height 200, scrollbar hidden (MI.css:96-108) | same rules (composer.css:90-101) | same |
| S03 | Height reset after send | all | `44px` (chat.js:398-403, 1906) | `auto`, one row = 44 px (composer.js:125) | same |
| S04 | Height after draft restore | all | no restore | resize after restore (composer.js:314-315) | new-only |
| S05 | Phone textarea 16 px, 11 px padding | touch | MI.css:473-476 | composer.css:410-413 | same |
| S06 | Placeholder text | all | random per render, fallback "Say something…" (MI.razor:258-270, 378-385) | random per conversation, fallback "Type a message..." (app.js:669-671) | different (D18) |
| S07 | maxlength | keyboard, paste | none (MI.razor:112-113) | 4000 default (composer.js:70) | new-only (D07) |
| S08 | Draft disabled states | all | never | load, restore, no conversation (index.html:284; composer.js:69, 296-319; app.js:705) | new-only (D17) |
| S09 | +, GIF, emoji disabled states | all | never | disabled when not allowed (composer.js:73-74) | new-only (D17) |
| S10 | No write permission | all | lock icon + text, no input (MI.razor:11-20) | text with period, composer hidden (composer.js:71-79) | different (D15) |
| S11 | Conversation not available | all | n/a | "Connect to open this conversation." (composer.js:76-79) | new-only (D15) |
| S12 | Enter sends on desktop | keyboard | `.click()` on Send (chat.js:488-498) | `send()` direct (composer.js:278-289) | same (path differs, M01) |
| S13 | Enter with empty or whitespace draft | keyboard | no newline, no send (chat.js:495-496) | no newline, Send disabled (composer.js:286-287, 105) | same |
| S14 | Shift+Enter | keyboard | newline (chat.js:491) | newline (composer.js:281) | same |
| S15 | Enter on a touch device | touch | newline (chat.js:486, 491) | newline (composer.js:284, 8) | same |
| S16 | IME composition, keyCode 229 | keyboard | ignored (chat.js:494) | ignored (composer.js:282-283) | same |
| S17 | Double Enter or double tap on Send | all | second click finds an empty box (chat.js:1869-1875) | `submitting` guard (composer.js:105, 110-111) | same |
| S18 | Send collapsed while empty, no picker | all | MI.css:161-166 | composer.css:151-156 | same |
| S19 | Send dimmed, picker open, empty draft | touch, mouse | CSS dim, enabled (MI.css:170-174) | CSS dim plus disabled (composer.css:159-163; composer.js:68) | same look (M03) |
| S20 | Send with whitespace-only draft | all | visible, accent, enabled (MI.razor:465) | visible, dimmed, disabled (composer.js:68) | different (D12) |
| S21 | Send during queueing | all | collapses at once (chat.js:1905) | dimmed until enqueue ends (composer.js:110-132) | different (D12) |
| S22 | Pending echo | all | plain ghost, opacity .55 (chat.js:1929-1934; old app.css:338-343) | full row, content opacity .75 (app.js:574-588; messages.css:936-939) | different (D14) |
| S23 | Draft clear after send | all | synchronous (chat.js:1905) | after enqueue, only if unchanged (composer.js:113-126) | different (D03) |
| S24 | Scroll after send | all | `scrollToBottom()` (chat.js:1907) | `scrolling.bottom()` (app.js:241-245) | same |
| S25 | Keyboard after Send tap | touch | mousedown preventDefault (chat.js:461-472) | pointerdown preventDefault (composer.js:270-277) | unsure (M04, device check) |
| S26 | Focus after Enter send | keyboard | stays in draft | stays in draft (composer.js:133) | same |
| S27 | Picker closes on Send click | touch, mouse | chat.js:1894 | pickers.js:114 | same (Enter path differs, M01) |
| S28 | Typing start | all | first non-empty input (chat.js:1157-1162) | first non-empty input (live.js:140-153) | same |
| S29 | Typing heartbeat | all | none | every timeout/2 (live.js:65-71) | new-only (not visible to self) |
| S30 | Typing stop after 3 s idle | all | chat.js:1163-1164 | 500 ms tick (live.js:63-64) | same |
| S31 | Typing stop when box empties | all | chat.js:1158 | live.js:142-144 | same |
| S32 | Typing stop on send | all | always (MI.razor:479) | inside finishReply (composer.js:95-103) | same (edge in M11) |
| S33 | Typing stop on channel switch | all | MI.razor:262-268 | live.js:136-139; app.js:675 | same |
| S34 | Typing stop on hidden tab | all | none | live.js:51-55 | new-only |
| S35 | Typing indicator text and place | all | strip above composer (MI.razor:57-69, 389-411) | same strip, server text (app.js:271-273; index.html:229-242; Yap/Offline/OfflineHub.cs:74-75) | same |
| S36 | Emoji picker opens from its button | touch, mouse | attribute flip (chat.js:1736-1749) | togglePicker (composer.js:172; pickers.js:74-91) | same |
| S37 | Tap emoji button while open | touch, mouse | backdrop catches it, closes (MI.css:313-321, 343-353) | toggle closes (pickers.js:76-78) | same (path differs, M02) |
| S38 | Backdrop tap | touch, mouse | closes (chat.js:1752-1756) | closes (pickers.js:20) | same |
| S39 | Escape | keyboard | nothing | closes (pickers.js:115-117) | new-only (D11) |
| S40 | Textarea tap while open | touch, mouse | focusin closes (chat.js:1776-1780) | focus closes (pickers.js:113) | same |
| S41 | GIF selected | touch, mouse | closes at once (chat.js:1981-1982) | closes at once (gifs.js:178) | same |
| S42 | Layout flip at 600 px | all | closes, pane rebuilt (chat.js:437-443; MI.razor:141) | closes, panel removed (pickers.js:118-122) | same |
| S43 | Conversation switch with picker open | all | stays open room to room (RoomChat.razor:162-190) | closes (composer.js:297) | different (D29) |
| S44 | Other toggle while a picker is open | mouse | backdrop catches, closes only | switches picker (pickers.js:80-90) | different (D02) |
| S45 | + while a picker is open | touch, mouse | backdrop catches, closes only | chooser opens, close after queue (composer.js:210) | different (D02) |
| S46 | Dim over the input box on a phone | touch | box and buttons dimmed, text and Send bright (MI.css:343-353, 524-526) | box not dimmed (composer.css:296-299; pickers.js:16-19) | different (D02) |
| S47 | Sheet position and size at phone width | touch | fixed bottom, 50vh max 400 (MI.css:508-522; CP.css:1-11) | same (pickers.css:638-652, 3-13) | same |
| S48 | Composer pushed above the sheet | touch | margin min(50vh,400px) (MI.css:529-531) | same (composer.css:441-443) | same |
| S49 | List when keyboard, sheet or draft resizes it | touch | scrollTop kept | pinned to bottom when following (scroll.js:57-61) | new-only (D13) |
| S50 | Keyboard open while picker open | touch | no code blurs or focuses | no code blurs or focuses | same (device check) |
| S51 | Viewport meta | touch | `interactive-widget=resizes-content` (App.razor:10-11) | same (index.html:4-7) | same |
| S52 | Emoji insertion point | all | at captured selection start (chat.js:1092-1095) | replaces captured range (composer.js:28-32) | different (D20) |
| S53 | Caret after a conversation switch | all | per textarea element (chat.js:1175-1183) | one module variable (composer.js:24-27) | different (D19) |
| S54 | Emoji insert does not focus the draft | touch | chat.js:1104-1105 | composer.js:33 | same |
| S55 | Emoji tap blurs the search field first | touch | chat.js:1066-1074 | content.js:238-240 | same |
| S56 | Emoji search focus on open | touch | no autofocus; 0.85rem font, iOS zooms on tap (EP.razor:36; EP.css:83) | same (content.js:214-219; pickers.css:149) | same |
| S57 | Emoji search filter and clear | all | chat.js:1683-1716, 1758-1764 | content.js:220-226, 307-323 | same |
| S58 | Emoji search reset on reopen | all | chat.js:1732 | content.js:335-341 | same |
| S59 | Recents frozen per open, max 20 | all | EP.razor:159-165; ChatBase.cs:695 | content.js:174-184, 325-334 | same |
| S60 | Category tab jump | all | smooth scrollIntoView (chat.js:1810-1818) | instant plus offset (content.js:272-281) | different (D21) |
| S61 | Custom emoji packs, shortcode insert | all | packs first (EP.razor:15-20, 77-92) | packs first (Yap/Offline/OfflineContent.cs:68-81; content.js:250-284) | same |
| S62 | Emoji grid refresh while open | all | none after mount | rebuild on catalog (content.js:287-297; pickers.js:131-134) | new-only (D24) |
| S63 | First picker open | touch | pre-mounted at about 1.2 s (MI.razor:327-340) | built on the tap (pickers.js:36-73) | different (D25) |
| S64 | GIF picker first tab | all | Browse if anything to browse (GP.razor:361-363) | same rule (gifs.js:413-416) | same |
| S65 | GIF tab and folder persist across opens | all | component state (GP.razor:302, 323) | closure state (gifs.js:67-70) | same |
| S66 | GIF search | keyboard, touch | 250 ms debounce, library then provider (GP.razor:484-542) | instant cached title matches, then 250 ms (gifs.js:483-494, 431-482) | same flow (cached matches new-only) |
| S67 | GIF search field focus on open | touch | no autofocus, 0.85rem font (GP.razor:56-57) | no autofocus, 0.85rem (gifs.js:82-97; pickers.css:345-356) | same |
| S68 | Trending load | all | first open (GP.razor:396-397) | first open, cached offline (gifs.js:378-401, 418) | same |
| S69 | GIF select sends | touch, mouse | ghost, close, scroll (chat.js:1974-2015; GP.razor:624-655) | close, queue, pending row (gifs.js:169-185) | same |
| S70 | GIF select by keyboard | keyboard | not reachable | Enter or Space (gifs.js:164-166, 186-191) | new-only (D27) |
| S71 | Favourite star | touch, mouse | hover reveal, no close (GP.razor:571-576; GP.css:238-239; chat.js:1979) | same (gifs.js:192-201; pickers.css:493-495) | same |
| S72 | Grid after a star or open fetch | all | diff render (GP.razor:715-731) | full rebuild (gifs.js:253, 402-419, 515-518) | different (D23) |
| S73 | Browse tile previews | all | stable (GP.razor:432-461) | re-picked each open (gifs.js:406) | different (D22) |
| S74 | GIF "Load more" | all | none | unstyled button (gifs.js:224-228) | new-only (D27) |
| S75 | GIF upload from the picker, success | touch, mouse | overlay, favourite, send, close (GP.razor:663-713) | overlay, queue, close on finish (composer.js:163-169; gifs.js:503-513) | same |
| S76 | GIF upload from the picker, failure | touch, mouse | picker stays, modal (GP.razor:694-703) | picker closes, failed row (gifs.js:504-511) | different (D10) |
| S77 | GIF upload too large (over 50 MB) | touch, mouse | modal "File is too large" (GP.razor:667-671) | modal "exceeds the upload size limit" (composer.js:178-185) | same |
| S78 | Combined picker tabs on a phone | touch | Emoji first, local flip (CP.razor:7-10; chat.js:1766-1768) | Emoji first, local flip (pickers.js:40-65) | same |
| S79 | Combined tab after a layout flip | touch | reset to Emoji (MI.razor:141) | last tab kept (pickers.js:42, 118-122) | different (trivial, not listed) |
| S80 | + opens the chooser | touch, mouse | label + InputFile, `image/*,video/*`, multiple (MI.razor:95-102) | button + `click()`, extension list, multiple (composer.js:218-224) | different (D08, M15) |
| S81 | File validation | all | ext or MIME, size, max 10 (chat.js:1370-1393; MI.razor:541) | ext only, size, max 20 (composer.js:174-207) | different (D28) |
| S82 | Upload progress | all | typing strip (MI.razor:37-56) | pending row (app.js:591-609) | different (D04) |
| S83 | Upload failure | all | modal (MI.razor:567-577) | Failed + Retry row (app.js:611-621) | different (D04) |
| S84 | Upload during a reply | all | not a reply, bar stays (MI.razor:595-600) | reply, bar clears (composer.js:140-155) | different (D05) |
| S85 | Drag highlight | mouse | composer only (chat.js:354-361) | composer only (composer.js:238-247) | same |
| S86 | Drop on the message list | mouse | ignored (chat.js:327-328) | uploads (composer.js:248-258) | different (D09) |
| S87 | Drop outside the column or with draft disabled | mouse | ignored (chat.js:327-328) | browser opens the file | different (D01) |
| S88 | Paste image or video | keyboard, touch | upload (chat.js:1331-1352) | upload (composer.js:261-269) | same |
| S89 | Paste another file kind | keyboard | default paste (chat.js:1336-1343) | blocked, warning (composer.js:262-268) | different (D26) |
| S90 | Warning modal focus | all | no focus calls (MI.razor:24-34) | focus Got It, restore previous (warnings.js:4, 28, 38) | different (D06) |
| S91 | Escape on the warning | keyboard | nothing | dismiss (warnings.js:40-42) | new-only (D11) |
| S92 | Composing state on a phone | touch | `data-composing` set, no CSS uses it (chat.js:1776-1784; MI.css:501-503) | no attribute | same (no visible effect) |
| S93 | + stays visible while typing, textarea width | touch | MI.css:501-506 | composer.css:434-439 | same |
| S94 | GIF toggle collapses while typing | all | MI.css:240-244 | composer.css:226-230 | same |
| S95 | GIF toggle hidden on a phone | touch | MI.css:504-506 | composer.css:437-439 | same |
| S96 | Reply bar shows, draft focused | touch, mouse | focus in capture click, bar after a round trip (chat.js:1272-1277) | bar at once, focus in click (composer.js:290-295) | same |
| S97 | Reply cancel | touch, mouse | no focus change (MI.razor:78, 495-498) | no focus change (composer.js:55-59) | same |
| S98 | Reply cleared after send or GIF | all | MI.razor:480-483, 516-517 | composer.js:95-103 | same |
| S99 | Reply bar on a room switch | all | carried to the next room (RoomChat.razor:162-190) | per conversation (composer.js:310, 316) | different (D16) |
| S100 | Draft on navigation and reload | all | same text room to room, lost on reload | saved and restored per conversation (composer.js:81-94, 304-319) | different (D16) |
| S101 | Emoji toggle icon | mouse | random per instance, re-random on hover (MI.razor:240-247; chat.js:407-424) | random at load, re-random on hover (composer.js:13-22) | same |

---

## Appendix C: Timeline, scrolling and media


OLD = commit 0cb67422 (Blazor Server page). NEW = working tree on main (`Yap/wwwroot/chat-client`).
Old paths are relative to the repo at 0cb67422. New paths are relative to `Yap/wwwroot/chat-client/` unless a full path is given.
Read-only audit. Nothing was built or run. "Read from code" means the code settles the claim. "Needs a device check" means the claim depends on browser or device behaviour that the code alone does not settle.

### Differences

#### Breaks use

1. **C-01. Auto-follow turns off without any scroll, then new content lands below the fold.**
   - Trigger and input: touch. A tap on a message (to show the action bar or react) with a small downward finger movement. Also any downward swipe on a conversation that is too short to scroll. Desktop: one wheel-up notch on a conversation that cannot scroll, or ArrowUp/PageUp/Home outside an input.
   - NEW: `touchmove` with any downward delta sets `following = false` (scroll.js:42-49). The `wheel` and `keydown` listeners do the same (scroll.js:27-33, 50-56). Only a later `scroll` event that ends near the bottom sets it back (scroll.js:16-26). A tap or a swipe that does not move the scroller fires no `scroll` event, so the flag stays off. After that, each render anchors the first visible row (app.js:698-700, scroll.js:64-84), and the ResizeObserver follow is a no-op (scroll.js:9). Incoming messages, and a new reaction row under the last message, appear below the visible area. There is no indicator. The flag comes back only on the user's own send (app.js:241-245), a conversation switch (app.js:703), or a manual scroll to the bottom.
   - OLD (target): every incoming message scrolled to the bottom, whatever the position (RoomChat.razor:209, DmChat.razor:202). A reaction scrolled to the bottom when the view was within 100px of it (ChatBase.cs:490-509). A tap never changed scroll behaviour.
   - Old: RoomChat.razor:194-210, DmChat.razor:193-202, ChatBase.cs:484-518. New: scroll.js:2-56, app.js:698-703, 723.
   - Confidence: the state change is read from code. How often a tap carries a downward `touchmove` needs a device check. iOS dispatches `touchmove` for small movements. Android behaviour inside the touch slop is unsure.

2. **C-02. The "More" menu and the delete confirmation close when the timeline scrolls by itself.**
   - Trigger and input: any input. The user opens the message "More" menu or the "Delete Message" confirmation while the view follows the bottom. Then a message arrives, an image or link preview finishes loading, or a history prepend restores the position. On Android, a keyboard that closes while the view is near the bottom has the same effect (the browser clamps `scrollTop`).
   - NEW: the timeline `scroll` listener calls `closePopup()` on every scroll event, with no check for who moved the scroller (actions.js:385-388). Programmatic follow scrolls fire that event (scroll.js:10-14). `closePopup` removes the menu and its backdrop (actions.js:112-122), or the delete overlay (actions.js:187-191). The dialog disappears under the user's finger. In a busy room, a delete can need several attempts.
   - OLD (target): the menu and the confirmation were component state and stayed open while the list scrolled (MessageItem.razor:301-365). The old scroll handler only faded the action bar, and it did nothing while a menu or picker was open (chat.js:1199-1211, guard at 1205).
   - Old: chat.js:1199-1211, 1240-1249; MessageItem.razor:301-365. New: actions.js:80-123, 124-196, 385-388; scroll.js:7-15, 59-61.
   - Confidence: read from code (programmatic scrolls fire `scroll` events). The Android keyboard path needs a device check.

#### Visible

3. **C-03. An incoming message while the user reads older messages no longer jumps to the bottom, and it is marked read unseen.**
   - Trigger and input: none (another user posts). The user is scrolled up.
   - NEW: the view stays where it is (anchored by preserve(), app.js:698-700). No "new messages" indicator exists. `reader.observe` still marks the conversation read on arrival when the tab is visible and the user is not Away (app.js:719, reader.js:92-107).
   - OLD (target): the page always scrolled to the bottom, so the message that was marked read was also on screen (RoomChat.razor:207-209, DmChat.razor:200-202).
   - Old: RoomChat.razor:194-210. New: app.js:698-700, scroll.js:64-84, reader.js:74-108.
   - Confidence: read from code.

4. **C-04. Returning to the tab or the installed app no longer snaps to the bottom.**
   - Trigger and input: the page becomes visible again.
   - NEW: the visibility handler refreshes the sidebar, reconnects or observes reads, and does not scroll (app.js:1207-1215). If the user was scrolled up (or C-01 turned follow off), messages that arrived while away stay below the fold.
   - OLD (target): `OnPageVisibilityChanged(true)` always called `scrollToBottom` (ChatBase.cs:667-673).
   - Old: ChatBase.cs:636-674. New: app.js:1207-1215.
   - Confidence: read from code.

5. **C-05. The gallery moves focus on open and on close.**
   - Trigger and input: tap or click a thumbnail, then close with the close button, Escape, a backdrop tap, or a drag down.
   - NEW: on open, focus goes to the close button (gallery.js:214). On close, `previous?.focus()` returns focus to the element that had it before (gallery.js:4, 28). The thumbnail is now a `<button>` (messages.js:117). Chrome (desktop and Android) focuses a button on tap or click, so `previous` is the thumbnail. `focus()` without `preventScroll` scrolls the timeline to the thumbnail when it is not fully visible. That happens after auto-follow moved the list while the gallery was open. The upward scroll then turns follow off (scroll.js:22). On iOS Safari a button tap does not take focus, so `previous` is the composer when the keyboard was up. Open then hides the keyboard, and close (inside the click or pointerup handler) opens it again.
   - OLD (target): no focus call on open or on close. The thumbnail was a `div` (MessageItem.razor:73, 88). The keyboard and the scroll position did not change.
   - Old: ImageGalleryModal.razor:1-111, chat.js:181-224. New: gallery.js:2-29, 211-214; messages.js:117-128.
   - Confidence: the calls are read from code. The scroll jump and the iOS keyboard behaviour need a device check.

6. **C-06. On iPhone, videos play inline instead of in the native fullscreen player.**
   - Trigger and input: tap the play overlay of an uploaded video, or the play control of a cached link video.
   - NEW: both video kinds set `playsInline = true` (rich-media.js:28, 133). Playback stays in the timeline.
   - OLD (target): neither `<video>` had `playsinline` (MessageItem.razor:107-108, MediaPlayer.razor:26). iPhone Safari opens its fullscreen player when such a video starts.
   - Old: MessageItem.razor:99-121, MediaPlayer.razor:26, chat.js:1493-1515. New: rich-media.js:21-48, 130-140.
   - Confidence: attribute difference read from code. The fullscreen behaviour (Safari and the home-screen app) needs a device check.

7. **C-07. Lobby join and leave lines are gone.**
   - Trigger and input: none. A user's first session connects, or the last one closes, while the viewer is in the lobby.
   - NEW: no equivalent. No client code adds "System" lines, and the server no longer has `GetRandomSystemMessage`. The only "System" references are styling and action-bar suppression (messages.js:100, actions.js:273).
   - OLD (target): the lobby added an italic centred system line with a random join or leave text (RoomChat.razor:236-264, events from ChatService.cs:540-544 and 815-821). These lines did not scroll the list.
   - Old: RoomChat.razor:236-264, ChatConfigService.cs:44. New: none.
   - Confidence: read from code.

8. **C-08. Fewer things become links, and trailing punctuation goes into the link.**
   - Trigger and input: a message with `www.example.com`, `example.com/path`, or `(https://x.com).`
   - NEW: only `http(s)://` text becomes a link, and the match runs to the next space, so `)` `.` `,` stay inside the href (content.js:137-145).
   - OLD (target): links for `http(s)://`, `www.`, and bare domains with common TLDs, with `https://` added; trailing punctuation and closing brackets stay outside the link (LinkPreviewService.cs:356-359, 42-72; EmojiService.Rendering.cs:160-204).
   - Old: LinkPreviewService.cs:42-72, 356-359. New: content.js:132-170.
   - Confidence: read from code. (This touches text rendering; it is listed here because link previews depend on the same URL rule on the server.)

9. **C-09. When the Android keyboard opens or the composer grows, the bottom of the list stays in view.**
   - Trigger and input: touch the composer (Android Chrome, `interactive-widget=resizes-content`). Also multi-line typing, the reply bar, or the notice strip.
   - NEW: a ResizeObserver on the scroller re-pins the bottom when follow is on (scroll.js:59-60, 7-15).
   - OLD (target): no resize or visualViewport handler existed. `scrollTop` stayed the same while the scroller got shorter, so the last messages moved under the composer (App.razor:11; chat.js has no resize listener).
   - iOS: no layout resize in either version, so no difference.
   - Old: App.razor:10-11, ChatLayout.razor.css:100-105. New: index.html:4-7, scroll.js:59-61.
   - Confidence: read from code. The new behaviour is likely better, but it differs from the old page.

10. **C-10. Pending rows look and behave differently.**
    - Trigger and input: send a message (tap Send, or Enter on desktop).
    - NEW: the row appears after an IndexedDB write and read (composer.js:113-127, app.js:241-245, 501-624). It is a full message row with avatar, name and time when a header is due, and only the content is dimmed to 0.75 (app.js:574-588, messages.css:936-939). The row stays until it is accepted. A failed send shows "Failed", the error text, and a Retry button (app.js:611-621). Uploads show file names, a progress bar and "Cancel upload" (app.js:591-609).
    - OLD (target): the ghost appeared in the same click task as the tap (chat.js:1862-1908). It was plain text without header, whole row at opacity 0.55, padded to the text column (app.css:338-343). A ghost that never got its echo vanished after 15 s with no message (chat.js:1925).
    - Old: chat.js:1902, 1921-1934, 1944-1967; app.css:338-357. New: app.js:501-624; messages.css:936-939.
    - Confidence: read from code. The delay before the row appears needs a device check.

11. **C-11. Reply-preview jump changed.**
    - Trigger and input: tap or click the reply preview above a reply.
    - NEW: when the original is loaded, `scrollIntoView({block:'center', behavior:'smooth'})` runs and the highlight starts at the same time (history.js:228-233). When it is not loaded, the client fetches it, inserts it into the timeline at its date (with no gap marker between it and the loaded rows), then jumps (history.js:184-221, view() at 59-67). Offline or deleted: a notice.
    - OLD (target): only loaded messages were reachable; otherwise nothing happened (ChatBase.cs:580-586). The scroll was a 600 ms ease-out animation of the list only, and the highlight began after the scroll ended (chat.js:1290-1320).
    - Old: chat.js:1290-1320, ChatBase.cs:580-586. New: history.js:184-234, messages.js:57-69.
    - Confidence: read from code. Whether the auto-follow cancels a jump to a fetched target needs a device check (see MP-6).

#### Minor

12. **C-12. Link preview cards and cached players sit inside `.message-content`.**
    - NEW: they are appended to `.message-content` (rich-media.js:102, 163). The edit box replaces all children of `.message-content`, so a card disappears while its message is edited (actions.js:200-201, 244). The card inherits `white-space: pre-wrap`, so line breaks in a description show.
    - OLD (target): cards and players followed `.message-content` inside `.message-body` (MessageItem.razor:196-212) and stayed visible during an edit.
    - Confidence: read from code.

13. **C-13. Possible one-frame duplicate when a send is acknowledged.**
    - NEW: render() adds the accepted row to `#timeline` (app.js:699), then awaits `renderPending()`, which awaits `storage.outbox()` before it removes the pending row (app.js:505, 544-546, 715). A paint can fall between the two.
    - OLD (target): a MutationObserver removed the ghost in the same task in which the real row was inserted, before paint (chat.js:1944-1961).
    - Confidence: unsure. Needs a device check.

14. **C-14. Top margin of the first row.**
    - NEW: `#timeline` and `#pending` use `display: contents` (chat.css:46-48, 76-78), so `.message-group:first-child { margin-top: 0 }` (messages.css:171-173) matches the first timeline row and the first pending row. The first message has no 1.0625rem gap. A pending row with a header has no gap, then gains it (about 17px jump) when it moves into the timeline.
    - OLD (target): the rule never matched, because a loading row, history note or spacer always came first in `.messages-flow` (RoomChat.razor:16-45, MessageItem.razor.css:103-105).
    - Confidence: read from code.

15. **C-15. Extra space and a button at the top of the timeline.**
    - NEW: while more history exists, an empty `<p id="history-note">` (padding 1rem plus default paragraph margins) and a "Load earlier messages" button render above the first row (index.html:222-225, messages.js:258-268, messages.css:60-66, 993-999). When history ends in a room, the note keeps the paragraph margins.
    - OLD (target): nothing rendered at the top while more history existed; the end note was a `div` with no margin (RoomChat.razor:17-43, ChatLayout.razor.css:192-198).
    - Confidence: read from code (no `p` margin reset in app.css or chat-client CSS).

16. **C-16. History auto-load rules.**
    - NEW: loads when `scrollTop < 60` on a scroll event, with no artificial delay and no cooldown (app.js:734-741); a button does the same (app.js:733).
    - OLD (target): loaded at `scrollTop <= 100`, waited 500 ms with the spinner visible, then had a 200 ms cooldown (chat.js:1523, 1553-1563; ChatBase.cs:351-402).
    - Confidence: read from code.

17. **C-17. Gallery close and counter details.**
    - NEW: close removes the modal at once (gallery.js:23); the `.closing` fade rule is still in gallery.css:15-19 but nothing sets it. A tap on the counter does nothing (gallery.js:66-69, 118-120). The counter changes when the strip passes 50% (gallery.js:70-77).
    - OLD (target): every close path faded the modal out over 0.15 s (chat.js:209, 266; ImageGalleryModal.razor.css:16-20). A tap anywhere outside the picture, the counter included, closed the gallery (ImageGalleryModal.razor:13, chat.js:266). The counter changed at 60% visibility (chat.js:231-243).
    - Confidence: read from code.

18. **C-18. Uploaded video: the play overlay does not come back after the end.**
    - NEW: the overlay hides on play and returns only if `play()` rejects (rich-media.js:38-44).
    - OLD (target): at `ended` (or a pause at time 0) the overlay returned and the controls hid (chat.js:1503-1514).
    - Confidence: read from code.

19. **C-19. Video GIFs blocked from autoplay stay frozen.**
    - NEW: `oncanplay` calls `play()` and ignores a rejection (rich-media.js:67-70). Video GIFs have no controls.
    - OLD (target): a `NotAllowedError` installed a one-shot listener, and the next tap or key anywhere started every paused GIF video (chat.js:1624-1645).
    - Confidence: read from code. Whether it occurs (for example iOS Low Power Mode) needs a device check. Only GIF entries without a `.gif` file render as video (Offline/OfflineContent.cs:28).

20. **C-20. Cached link videos preload less.**
    - NEW: `preload = 'metadata'` (rich-media.js:132). OLD (target): `preload="auto"` (MediaPlayer.razor:26). Desktop and Android may start playback later. Safari ignores both.
    - Confidence: read from code.

21. **C-21. Emoji-only sizing rule.**
    - NEW: one decision for the whole message; any link makes it not emoji-only (content.js:135-168).
    - OLD (target): the decision ran per text segment between links, so emoji next to a link could render at 3em (EmojiService.Rendering.cs:84, 186-200).
    - Confidence: read from code.

22. **C-22. Switching back to a room shows older history loaded earlier.**
    - NEW: loaded pages stay in memory and IndexedDB and merge into the view (history.js:22-67).
    - OLD (target): a switch reset paging and showed only the last 50 messages (RoomChat.razor:170-171, ChatBase.cs:320-335).
    - Confidence: read from code. Both versions land at the bottom.

23. **C-23. Room switch: header before timeline.**
    - NEW: the title, avatar and placeholder update before `await storage.outbox()`, and the timeline updates after it (app.js:640-699). The old room's rows can show under the new title for a frame.
    - OLD (target): no client-side gap of this kind (RoomChat.razor:162-190).
    - Confidence: unsure. Needs a device check.

24. **C-24. Single-image thumbnail is a `<button>`.**
    - NEW: `button.gallery-item` (messages.js:117) with border, padding and background reset (messages.css:1032-1036). The UA button layout can align a narrow portrait picture differently inside the 400px box.
    - OLD (target): `div.gallery-item`, picture left-aligned (MessageItem.razor:73-77).
    - Confidence: unsure. Needs a device check.

25. **C-25. Timestamps refresh every minute.** NEW-only: app.js:742-748. OLD (target): a label changed only when the component re-rendered (MessageItem.razor:409-426). Format rules match (dates.js:61-67).

26. **C-26. More than 10 images in one message.** NEW renders all of them in rows (messages.js:110-116). OLD (target) rendered no rows past 10 (row table ends at 10, MessageItem.razor:447-458). Edge case only.

### Mechanism pairs

- **MP-1. Three restorers for one prepend.** NEW: `scrolling.preserve()` anchors the first visible row inside render (app.js:698-700, scroll.js:64-84); then history.load sets an absolute `scrollTop = top + (scrollHeight - height)` in a rAF, using `top` captured before render (history.js:157-164); Chrome and Firefox also apply native scroll anchoring (no `overflow-anchor: none` on `.messages`). render awaits IndexedDB between the two writes (app.js:715-719). A user scroll in that window (iOS momentum at the top) is overwritten with the stale `top`. OLD: one relative rAF restore, `scrollTop += heightDiff` (chat.js:1532-1542). Visible effect not proven; needs a device check.
- **MP-2. Bottom follow from four sources.** NEW: ResizeObserver on the scroller and on `.messages-flow` (scroll.js:59-61), `scrolling.update()` at the end of every render (app.js:723), `bottom()` on switch (app.js:703), `bottom()` after an own send (app.js:243). All are rAF-driven (scroll.js:10-14). New triggers that OLD lacked: keyboard open and close, composer growth, reply bar, notice strip. OLD: explicit `scrollToBottom` per event (chat.js:101-152 called from ChatBase.cs:219, RoomChat.razor:189 and 209, chat.js:1907 and 2014, MessageInput.razor:515, ChatBase.cs:508 and 672, RoomChat.razor:328).
- **MP-3. Four inputs for one "following" flag.** NEW: scroll direction (scroll.js:16-26), wheel (27-33), touchmove (34-49), keydown (50-56). Wheel, touchmove and keydown release the flag without a scroll; only a scroll event re-arms it. OLD: no follow state. See C-01.
- **MP-4. Close-on-scroll fed by programmatic scrolls.** NEW: actions.js:385-388 closes popups on every `.messages` scroll; MP-1, MP-2, gallery focus scroll (MP-5) and the Android keyboard clamp all fire such scrolls. OLD: the scroll handler skipped open menus (chat.js:1205). See C-02.
- **MP-5. focus() calls OLD did not make.** NEW: gallery open `dismiss.focus()` (gallery.js:214); gallery close `previous?.focus()` without `preventScroll` (gallery.js:28); re-focus after a row move with `preventScroll` (messages.js:241-246). OLD: none in the gallery; Blazor moved rows without re-focus. The delete dialog focus exists in both (actions.js:195, MessageItem.razor:546-552). See C-05.
- **MP-6. Reply jump: native smooth scroll beside the rAF follow.** NEW: `scrollIntoView({behavior:'smooth', block:'center'})` in a rAF (history.js:228-233). When the target was fetched, render inserted a row, so the ResizeObserver schedules a follow rAF while `following` is still true (no scroll event yet). Setting `scrollTop` in that rAF can cancel the smooth scroll. `scrollIntoView` also scrolls every scrollable ancestor; with the iOS keyboard up it may pan the visual viewport. OLD: rAF animation of the list's `scrollTop` only (chat.js:1290-1320). Unsure; needs a device check.
- **MP-7. Two renderers for one sent message.** NEW: timeline render (app.js:699) and `renderPending` (app.js:715, 501-624), with an IndexedDB await between. OLD: MutationObserver swap before paint (chat.js:1944-1961). See C-13.
- **MP-8. Android: browser "focused editable into view" beside the follow.** NEW: when an edit box near the bottom gets focus, Chrome scrolls it into view after the keyboard resize, and the ResizeObserver follow may scroll to the bottom first (scroll.js:59-60). The list may move down, then up. OLD: only the browser scroll. Unsure; needs a device check.
- **MP-9. Gallery keyboard and resize handling.** NEW: `preventDefault` on ArrowLeft, ArrowRight and Tab, plus a Tab focus trap (gallery.js:87-103); a window `resize` handler re-aligns the strip and drops fill on every stage (gallery.js:104-107, 210). OLD: no `preventDefault`, no resize handler; native scroll-snap re-snapped (chat.js:184-189).
- **MP-10. Gallery counter source moved.** NEW: `strip.onscroll` drives the counter; the IntersectionObserver only upgrades pictures and drops fill (gallery.js:70-86). OLD: the IntersectionObserver drove the counter and video play state (chat.js:231-243).
- **MP-11. GIF readiness per element.** NEW: `oncanplay`, `onload`, `onerror` on each element (rich-media.js:67-81). OLD: document-level capture listeners for `canplay`, `load`, `error`, plus a gesture unlock (chat.js:1613-1664). See C-19.
- **MP-12. Two history triggers.** NEW: scroll threshold (app.js:734-741) and button (app.js:733), both guarded by `busy`. Harmless as read.
- **MP-13. preventDefault on touch or wheel.** None in the timeline. scroll.js listeners are passive (scroll.js:25, 32, 40, 48). Gallery drag listeners do not call `preventDefault` (gallery.js:166-207). Same as OLD.

### State table

| ID | State / trigger | Input | OLD behaviour (file:line) | NEW behaviour (file:line) | Verdict |
|---|---|---|---|---|---|
| S-01 | Open a conversation, first load | any | Bottom after first render, re-scroll after pending images (ChatBase.cs:218-219, chat.js:101-152) | `bottom()` then ResizeObserver follow (app.js:703, scroll.js:59-61) | same |
| S-02 | Own text send | tap / Enter | Ghost and `scrollToBottom` in click capture (chat.js:1862-1908) | Enqueue, pending row, `bottom()` (composer.js:104-127, app.js:241-245) | same (timing, C-10) |
| S-03 | Own GIF send | tap | Ghost and two scrolls (chat.js:1974-2015, MessageInput.razor:511-515) | `sendExtra`, then `bottom()` (composer.js:140-155) | same |
| S-04 | Incoming message, at bottom | none | `scrollToBottom` (RoomChat.razor:209) | Follow via ResizeObserver and render (scroll.js:59-61, app.js:723) | same |
| S-05 | Incoming message, scrolled up | none | Always jumps to bottom (RoomChat.razor:209, DmChat.razor:202) | Stays, anchored, no indicator (app.js:698-700) | different (C-03) |
| S-06 | Incoming after a tap with small downward finger movement at bottom | touch | Jumps to bottom | Follow off, message below fold (scroll.js:42-49) | different (C-01) |
| S-07 | Incoming on a short, non-scrollable conversation after a downward swipe or wheel-up | touch / wheel | Bottom | Follow off; rows go below fold once it overflows (scroll.js:27-49) | different (C-01) |
| S-08 | Arrow/PageUp/Home outside inputs | keyboard | No effect on later scrolls | Follow off (scroll.js:50-56) | new-only |
| S-09 | Reaction change near bottom | any | Scroll to bottom if within 100px (ChatBase.cs:490-509) | Follow if following (scroll.js:59-61) | same (unless C-01) |
| S-10 | Reaction, edit or delete above viewport, scrolled up | none | No compensation beyond browser anchoring | First visible row anchored (scroll.js:64-84) | different (new-only compensation) |
| S-11 | Link preview or media cache ready near bottom | none | Scroll if within 100px, after 50 ms (RoomChat.razor:310-329) | Follow if following | same |
| S-12 | Image loads, at bottom | none | Wait for load, rAF scroll (chat.js:113-143) | ResizeObserver follow (scroll.js:59-61) | same |
| S-13 | Image loads above, scrolled up | none | Browser anchoring only (none on iOS) | Same | same |
| S-14 | "More" menu or delete confirm open, list auto-scrolls | any | Stays open (chat.js:1205, MessageItem.razor:301-365) | Closes (actions.js:385-388) | different (C-02) |
| S-15 | Action bar fades after 50px scroll | touch | chat.js:1240-1249 | actions.js:385-388 | same |
| S-16 | Lobby join/leave | none | System line added (RoomChat.razor:236-264) | Nothing | old-only (C-07) |
| S-17 | Scroll near top | touch / wheel | Load at <=100px, 500 ms spinner (chat.js:1553-1563, ChatBase.cs:351-402) | Load at <60px, spinner (app.js:734-741, history.js:96-98) | same (C-16) |
| S-18 | "Load earlier messages" button | tap / click | None | Button (index.html:224-225, app.js:733) | new-only |
| S-19 | Position after prepend | none | Relative rAF restore (chat.js:1532-1542) | preserve() plus absolute rAF (app.js:698-700, history.js:157-164) | unsure (MP-1) |
| S-20 | History exhausted | none | `div.history-start` or DM notice (RoomChat.razor:24-43, DmChat.razor:22-30) | `p#history-note` (messages.js:258-289) | same (spacing C-15) |
| S-21 | Older history while offline | none | n/a | Note "Earlier uncached history needs a connection." (messages.js:260-262) | new-only |
| S-22 | Short conversation, no overflow, more history exists | none | No trigger, no button | Button visible (messages.js:267) | new-only |
| S-23 | Room or DM switch | tap | Bottom (RoomChat.razor:189, DmChat.razor:182) | Bottom (app.js:703) | same |
| S-24 | Switch back to a room | tap | Last 50 only, bottom (RoomChat.razor:170-171) | Earlier pages kept, bottom (history.js:59-67) | different (C-22) |
| S-25 | Header vs timeline during switch | tap | Updated in one server pass | Header first, timeline after IndexedDB read (app.js:640-699) | unsure (C-23) |
| S-26 | Tab or app resume | none | `scrollToBottom` (ChatBase.cs:667-673) | No scroll (app.js:1207-1215) | different (C-04) |
| S-27 | Phone keyboard opens, following | touch | `scrollTop` kept, last rows hidden (no handler) | Bottom kept (scroll.js:59-60) | different (C-09) |
| S-28 | Phone keyboard opens, scrolled up | touch | Unchanged | Unchanged | same |
| S-29 | Composer grows, reply bar or notice appears | typing / tap | Unchanged | Follow if following | different (C-09) |
| S-30 | Viewport meta | none | `interactive-widget=resizes-content` (App.razor:11) | Same (index.html:4-7) | same |
| S-31 | Overscroll / pull-to-refresh | touch | No `overscroll-behavior` on `.messages`; root overflow hidden (ChatLayout.razor.css:100-105, app.css:199-215) | Same (messages.css:2-7, app.css:200-216) | same |
| S-32 | Pending row look | send | Plain text, opacity 0.55 (app.css:338-343) | Full row, content 0.75 (app.js:574-588, messages.css:936-939) | different (C-10) |
| S-33 | Pending row replaced by real row | ack | Pre-paint swap (chat.js:1944-1961) | Two renders, IndexedDB between (app.js:699, 715) | unsure (C-13) |
| S-34 | Send never confirmed | none | Ghost removed after 15 s (chat.js:1925) | Row stays; Failed, error, Retry (app.js:611-621) | new-only |
| S-35 | Retry a failed send | tap | n/a | Requeue and flush (app.js:614-620) | new-only |
| S-36 | Pending rows on switch | tap | Ghosts cleared (RoomChat.razor:167) | Per-channel rows (app.js:506-508) | same |
| S-37 | Upload in progress row | send | n/a in timeline | File names, progress, Cancel (app.js:591-609) | new-only |
| S-38 | Thumbnail size and rows | none | 800x600 single, 800x800 rows (MessageItem.razor:64-98, .css:198-253) | Same (messages.js:102-139, messages.css:243-280) | same (C-24 unsure) |
| S-39 | Thumbnail fails to load | none | Broken image | Alt text, then cache (messages.js:130-137) | new-only |
| S-40 | Tap thumbnail | tap / click | Gallery after circuit round trip (MessageItem.razor:74, ChatBase.cs:287-293) | Gallery at once, focus on close button (messages.js:128, gallery.js:211-214) | different (C-05) |
| S-41 | Close with close button | tap / click | Fade, then circuit close (chat.js:266, ImageGalleryModal.razor:47) | Remove, `previous.focus()` (gallery.js:19-29, 65) | different (C-05, C-17) |
| S-42 | Close with Escape | keyboard | Close (chat.js:185) | Close (gallery.js:88) | same (focus C-05) |
| S-43 | Backdrop tap | tap / click | Close on any non-picture tap (ImageGalleryModal.razor:13, chat.js:266) | Close on stage tap (gallery.js:118-120) | same (counter tap C-17) |
| S-44 | Drag down to close | touch | >120px closes, backdrop fades over 300px (chat.js:288-313) | Same values (gallery.js:166-207) | same |
| S-45 | Swipe between images | touch | Scroll-snap strip (ImageGalleryModal.razor.css:24-54) | Same (gallery.css:21-49) | same |
| S-46 | Prev/next buttons and arrow keys | click / keyboard | `scrollBy` smooth (chat.js:186-187, 200-203, 263-264) | `scrollTo` smooth, `preventDefault` (gallery.js:40-46, 89-96) | same |
| S-47 | Tab inside gallery | keyboard | Browser default | Focus trap (gallery.js:97-102) | new-only |
| S-48 | Double-tap fill | touch / mouse | 300 ms / 25 px (chat.js:271-284) | Same (gallery.js:191-206) | same |
| S-49 | Pinch zoom | touch | Native (`touch-action: pan-x pinch-zoom`) | Same (gallery.css:44) | same |
| S-50 | Fill button | tap / click | toggleFill (chat.js:265) | Same (gallery.js:64) | same |
| S-51 | Counter | swipe | IntersectionObserver at 60% (chat.js:231-243) | onscroll rounding (gallery.js:70-77) | different (C-17) |
| S-52 | Resize or rotate with gallery open | none | Native re-snap | Re-align, drop fill (gallery.js:104-107) | new-only |
| S-53 | Caption or download in gallery | none | None | None | same |
| S-54 | Back navigation with gallery open | Android back | Gallery state not reset on switch (RoomChat.razor:162-190) | Closes only on `chat-clear` (gallery.js:209) | same (unsure, no device test) |
| S-55 | Uploaded video play | tap | Overlay, controls, `play()`, no `playsinline` (MessageItem.razor:106-117, chat.js:1493-1502) | Same with `playsInline` (rich-media.js:25-44) | different (C-06) |
| S-56 | Uploaded video ends | none | Overlay back, controls off (chat.js:1503-1514) | Controls stay, no overlay | different (C-18) |
| S-57 | Cached link video | tap | `controls`, `preload=auto`, no `playsinline` (MediaPlayer.razor:26) | `preload=metadata`, `playsInline` (rich-media.js:130-140) | different (C-06, C-20) |
| S-58 | Cached audio | tap | `preload=metadata` (MediaPlayer.razor:46) | Same (rich-media.js:117-121) | same |
| S-59 | GIF as image | none | Lazy `img`, spinner until load (MessageItem.razor:145-147, chat.js:1652-1663) | Same (rich-media.js:71-75) | same |
| S-60 | GIF as video | none | Autoplay, canplay play, gesture unlock (MessageItem.razor:152-155, chat.js:1613-1664) | Autoplay, oncanplay play, no unlock (rich-media.js:61-70) | different (C-19) |
| S-61 | GIF load error | none | Spinner removed, broken image | "GIF unavailable" (rich-media.js:77-81) | new-only |
| S-62 | Link preview card click | tap / click | `<a target=_blank>` whole card (LinkPreviewCard.razor:5) | Same (rich-media.js:145-163) | same |
| S-63 | Link preview position and edit | edit | After `.message-content`, visible during edit (MessageItem.razor:196-212) | Inside `.message-content`, hidden during edit (rich-media.js:163, actions.js:244) | different (C-12) |
| S-64 | Link detection in text | none | http(s), www., bare domains, punctuation stripped (LinkPreviewService.cs:42-72, 356-359) | http(s) only, punctuation kept (content.js:137-145) | different (C-08) |
| S-65 | Emoji-only sizing | none | Per segment, 3em (EmojiService.Rendering.cs:84-93, 186-200) | Whole message, 3em (content.js:135-168, messages.css:959-963) | different (C-21) |
| S-66 | Timestamps | none | Today / Yesterday / date (MessageItem.razor:409-426) | Same rules (dates.js:61-67), refresh each 60 s (app.js:742-748) | same (+ new-only refresh) |
| S-67 | Date separators, unread divider | none | None | None | same |
| S-68 | Mark read on open, receive, resume | none | RoomChat.razor:132, 207-208; ChatBase.cs:660-665 | reader.observe (app.js:719, 1213; reader.js:74-108) | same (scroll context C-03) |
| S-69 | Reply jump, target loaded | tap / click | 600 ms ease-out, highlight after (chat.js:1290-1320) | Native smooth, highlight at start (history.js:222-234) | different (C-11) |
| S-70 | Reply jump, target not loaded | tap / click | Nothing (ChatBase.cs:580-586) | Fetch, insert, jump, or notice (history.js:184-234) | new-only (C-11) |
| S-71 | Edit box near bottom | tap | `scrollIntoView({block:'nearest'})` on open and input (chat.js:523-531) | Same (actions.js:247-253) | same |
| S-72 | First row top margin | none | 1.0625rem (rule never matched) | 0 (messages.css:171-173, chat.css:46-48) | different (C-14) |
| S-73 | Message with more than 10 images | none | No rows (MessageItem.razor:447-458) | All rows (messages.js:110-116) | different (C-26) |
| S-74 | System message styling | none | Centred italic (MessageItem.razor.css:175-194) | Same (messages.css:78-91, 232-240) | same |

---

## Appendix D: Shell, header, sidebar and overlays


Scope: the shell around the conversation. OLD is commit 0cb67422 (Blazor Server). NEW is the working tree on main.

Path convention:
- OLD paths are at 0cb67422 and start under `Yap/` (for example `Components/ChatHeader.razor` means `Yap/Components/ChatHeader.razor`). Old `chat.js` is `Yap/wwwroot/js/chat.js`.
- NEW paths without a folder are in `Yap/wwwroot/chat-client/`. Other NEW paths are named in full.

Method: read from code only. Nothing was built or run. "needs a device check" means the code shows the mechanism, but the visible result depends on timing, browser or screen size.

The four issues fixed on main today (double action bar, edit bar on phones, send focus, emoji-picker send, unstyled Reload in the notice strip) are not repeated here.

---

### Differences

Sorted by severity: breaks use, then visible, then minor.

#### Breaks use

**D1. Profile card cannot be opened on a phone in landscape or on a wide Android tablet.**
- Trigger and input: touch, viewport wider than 768px with a coarse pointer (most phones in landscape: 844 to 932px; Android tablets).
- NEW: the info button is hidden above 768px (`shared.css:995-1002`). The name hover is blocked for coarse pointers (`app.js:392-395`). No other path opens the card.
- OLD target: the info button and the centered card depended on the User-Agent, not on width. Every iPhone and Android device got the button and the tap-to-open card at any width.
- OLD: `Components/ChatSidebar.razor:86-99`, `:203-216`, `Middleware/DeviceDetectionMiddleware.cs:15-16`. NEW: `shared.css:995-1002`, `app.js:392-406`.
- Confidence: read from code.

**D2. The sidebar is rebuilt from scratch on every live packet. Taps on a row can be lost, and the desktop hover card can stay on screen.**
- Trigger and input: touch on a room or person row (or the info button) while presence or typing packets arrive; mouse hover on a name on desktop.
- NEW: every activity packet calls `changed()`, which calls `sidebar()`. `sidebar()` runs `replaceChildren()` on `#rooms` and `#dms` and creates new `<a>` rows. `render()` also calls `sidebar()` twice. If a row is replaced between touch-down and touch-up, the click can miss (Android pointer path; iOS synthesizes the click at touch-up and probably survives). A hovered name that is replaced never gets `mouseleave`, so the hover card can stay until another name is hovered. Keyboard focus on a sidebar link is lost on each packet.
- OLD target: Blazor diffed the rows in place. The DOM nodes under the finger or pointer stayed the same, so taps, hover and focus were stable.
- OLD: `Components/ChatSidebar.razor:19-56`, `:71-127` (diffed `@foreach`). NEW: `app.js:207-217`, `:288-437` (`replaceChildren` at 311 and 348), `:697`, `:718`; `live.js:112`, `:118`, `:131`; `profiles.js:107-109`.
- Confidence: needs a device check (lost-tap rate depends on browser and packet rate).

**D3. Long sidebar content is clipped with no scroll. The new OUTGOING section and later people can be unreachable.**
- Trigger and input: any input; phone with the sidebar open, or desktop with a short window.
- NEW: `.users-sidebar` keeps `overflow: hidden` and has no scroll. While the live view is absent (first load, reconnect, offline) the list shows every known person, not only online people and DM partners (`app.js:366-368`). The OUTGOING section (unsent work for removed conversations) sits after that list.
- OLD target: same `overflow: hidden`, but the list held only online users and DM partners, and there was no section after it.
- OLD: `Components/ChatSidebar.razor.css:2-7`, `Components/ChatSidebar.razor:486-546`. NEW: `shared.css:328-333`, `app.js:366-368`, `:520-541`, `index.html:318-319`.
- Confidence: read from code (how much is clipped depends on screen height and user count).

#### Visible

**D4. The status chip turns grey, the menu items are disabled, and the person list changes whenever the live connection is absent. This happens on every app open.**
- Trigger and input: none (app open, resume from background, reconnect, offline).
- NEW: first paint shows a green chip with the text "Account" (`index.html:82-88`). The first cached render sets the chip to `status-invisible` (grey) because `live.view` is null (`app.js:258-261`). The Online/Away/Invisible items are disabled (`app.js:267`). The heading falls back to "Online Users" without a count and the desktop online count is blank (`app.js:269-270`). The list shows all known people with grey dots (`app.js:366-368`). All of this returns to normal only when the first activity packet arrives.
- OLD target: the chip showed the user's name and effective status on first render, and it kept that state during a reconnect. The status items were always enabled.
- OLD: `Components/ChatHeader.razor:48-54`, `:180-181`, `:59-70`. NEW: `index.html:82-88`, `app.js:258-274`, `:366-368`, `live.js:126-132`.
- Confidence: read from code (the length of the grey state depends on the network).

**D5. After a reconnect, a pending retry timer can tear down the new connection. The presence UI then flickers a second time.**
- Trigger and input: socket loss followed by a return to the app (visibility), an `online` event or the Retry button within about 3 seconds.
- NEW: `onclose` and stream errors schedule `connectChat()` after 2.5 to 3.5 s. A visibility return, `online` or Retry calls `connectChat()` directly and does not clear that timer. When the timer fires after the direct connect has finished, `connectChat()` detaches live state and stops the healthy hub (`app.js:895-896`). D4's grey state shows again for one more connect.
- OLD target: one reconnect loop (Blazor retries every 4 s, or a visible-tab retry after a failure). The UI did not reset after a successful reconnect.
- OLD: `Components/App.razor:126-144`, `Components/Layout/ReconnectModal.razor.js:68-106`. NEW: `app.js:866-870`, `:890-896`, `:989-995`, `:1010-1014`, `:1200-1202`, `:1207-1215`, `:1240-1249`.
- Confidence: needs a device check (timing window).

**D6. The account menu no longer shows the crossed bell while the account-wide mute is on.**
- Trigger and input: tap or click the status chip while the server-level mute ("disable notifications temporarily") is active.
- NEW: the "Notification settings" item always shows the plain bell (static markup).
- OLD target: the item showed the crossed bell while the server mute was active. The comment calls this the only reminder of that mute on phones.
- OLD: `Components/ChatHeader.razor:78-95`, `:185-188`. NEW: `index.html:123-133`.
- Confidence: read from code.

**D7. Connection state is a small header label instead of the old top banner. On phones the short label can name the wrong state.**
- Trigger and input: socket loss, offline, account or setup problems.
- NEW: `#connection` shows text in the header. On phones it shows only a short label (`connectionLabel`). "Connect to continue" and "Account changed" map to "Syncing". "Synced · offline reload unavailable" stays visible as "Synced" while the worker is not ready or is unsupported. There is no reconnecting countdown and no banner.
- OLD target: an orange fixed banner across the top: "You're awesome. (Reconnecting...)", then "We love you. Reconnecting in Ns...", "Session took a nap. Waking it up...", and a Reload button as a last resort. No label when connected.
- OLD: `Components/Layout/ReconnectModal.razor:32-70`, `ReconnectModal.razor.css:22-60`, `:100-115`. NEW: `app.js:57-71`, `:850-852`, `:981`, `:992`, `:1006`, `:1106`, `:1152`, `:1203-1206`, `chat.css:5-8`, `:61-75`, `index.html:80`.
- Confidence: read from code.

**D8. The phone header can overflow in a DM while the connection label or "Updating history…" is visible.**
- Trigger and input: none (first load, reconnect, offline, after a server restart).
- NEW: `.header-controls` does not shrink. On phones it can hold the connection label (up to 65px), `#window-state` at full width (about 100px, no phone rule), the status chip (about 140px), the mailbox (36px) and the hamburger (44px), plus 12px gaps. In a DM the left side needs at least back button + avatar (about 68px). At 360 to 390px width this exceeds the space, so the title area collapses and the back button or avatar paints under the controls.
- OLD target: the header held only the chip, mailbox and hamburger on the right, which fit next to back + avatar + name.
- OLD: `Components/ChatHeader.razor:46-142`, `ChatHeader.razor.css:51-56`, `:306-318`. NEW: `index.html:78-80`, `chat.css:61-75`, `:106-109`, `shared.css:91-96`.
- Confidence: needs a device check (estimate from CSS widths).

**D9. A logged-out visit or an ended session stays on a cleared chat page with a long notice. It does not go to the login page.**
- Trigger and input: open `/lobby` (or a room or DM link) without a valid cookie; session revoked or expired.
- NEW: the server serves the shell without an auth check. Bootstrap or session refresh returns AUTH_REQUIRED and `loseAccount()` clears the sidebar, title and timeline. It shows "Sign in required" and a notice: "Sign in to the same account to resume saved work. If this server lost its accounts after a restart, registering again creates a new account…" with a Sign in link. A revoked session is found by the 30 s session poll.
- OLD target: an unauthenticated visit redirected to `/?returnUrl=…` (login flow). A kicked session was sent to `/auth/signout` at once and landed on the login page.
- OLD: `Components/Layout/ChatLayout.razor:73-79`, `:117-133`. NEW: `Yap/Offline/OfflineEndpoints.cs:31-36`, `app.js:142-155`, `:1100-1103`, `:1222-1230`.
- Confidence: read from code.

**D10. Opening a DM that is not in the snapshot yet shows a placeholder page first. Offline it stays that way.**
- Trigger and input: tap a person you never messaged (or open a DM link).
- NEW: the route has no conversation, so the title shows "Yap", the back button and avatar are hidden, the composer is disabled and the note reads "This conversation is not cached or is no longer accessible. Connect to refresh." `resolveDm()` then creates the DM over the network. Offline, `resolveDm()` returns at once with no message.
- OLD target: the DM opened at once with back button, avatar and name in the header, and the "beginning of your direct message history" note.
- OLD: `Components/Pages/DmChat.razor:103-131`, `Components/ChatHeader.razor:16-30`. NEW: `app.js:468-487`, `:645-668`, `:704-705`, `messages.js:258-266`.
- Confidence: read from code.

**D11. On a room page in a hidden tab, the tab title now counts arrivals. The old page showed no count there with default settings.**
- Trigger and input: desktop tab hidden while on a room; a DM (or an unmuted room) receives a message.
- NEW: the title counts arrivals from every unmuted conversation while on a room page, and only DMs while on a DM page. Mute is read from the arriving conversation.
- OLD target: the title count and sound were skipped when the CURRENT channel was muted. Rooms are muted by default (`NotifRoomMode = MuteAll`), so a room page showed no count at all. On a DM page, every DM to the user added a count and played the sound unless the current DM was muted.
- OLD: `Components/Pages/ChatBase.cs:607-633`, `Components/Pages/RoomChat.razor:220-233`, `Components/Pages/DmChat.razor:213-230`, `Models/User.cs:126`. NEW: `notifications.js:79-95`, `:110-123`.
- Confidence: read from code.

**D12. The upload warning modal moves focus. On a phone this closes the keyboard when it opens and reopens it when it closes.**
- Trigger and input: paste or drop an unsupported file, or pick too many files, while typing (touch).
- NEW: `warn()` stores the active element, focuses "Got It", and on dismiss focuses the stored element again (the draft). Escape also dismisses (new).
- OLD target: the modal appeared with no focus change. Focus and the keyboard stayed where they were. Close by ✕, "Got It" or backdrop tap. No Escape.
- OLD: `Components/MessageInput.razor:24-33`, `:649-653`. NEW: `warnings.js:4`, `:26-30`, `:38`, `:40-42`.
- Confidence: needs a device check (keyboard behaviour on focus).

**D13. The new shell has no iOS home-screen metas.**
- Trigger and input: add to home screen on iOS, open the installed app.
- NEW: no `apple-mobile-web-app-capable`, no `apple-mobile-web-app-status-bar-style`, no `apple-touch-icon`. The install flow starts from the chat page, so iOS reads this shell.
- OLD target: `black-translucent` status bar (content under a transparent status bar tinted by the root colour), `apple-mobile-web-app-capable=yes`, and `icon-192.png` as the touch icon.
- OLD: `Components/App.razor:70`, `:73-74`. NEW: `index.html:4-8`, `:17-21`.
- Confidence: needs a device check (iOS version decides how much the manifest replaces).

**D14. The bot's install link opens a new intermediate dialog with bare browser buttons.**
- Trigger and input: tap the `[pwa-install]` link in the Ping bot welcome DM (phones get this link).
- NEW: a `<dialog class="install-guide">` asks "Add Yap to your home screen" with "Use existing app" and "Install app" buttons. Only then the native prompt or the add-to-homescreen guide opens (now with a close control). The buttons get only margin, padding and cursor, so they render with the browser's grey bevel and system font on a dark card. `--border-color` is not defined, so the card has no border.
- OLD target: the link opened the native install prompt at once, or the add-to-homescreen guide (`allowClose: false`). No intermediate dialog.
- OLD: `wwwroot/js/chat.js:709-744`, `Components/MessageItem.razor:180-186`, `:651`. NEW: `pwa.js:22-97`, `messages.js:93-98`, `shared.css:1004-1027`.
- Confidence: read from code. The extra dialog looks deliberate (comment at `pwa.js:26-27`); the bare buttons do not.

**D15. Errors and lasting states use an in-flow notice strip. It shifts the layout, has no close control, and can be wiped by the next snapshot.**
- Trigger and input: any notice (offline history, failed status change, worker problems, update available).
- NEW: `#notice` sits between the header line and the conversation, so showing or hiding it resizes the conversation. Every accepted snapshot calls `notice('')`, which clears transient text at once while online. Offline, a transient notice stays until reconnect, with no way to dismiss it. A failed first worker install (manifest fetch has a 10 s timeout) or an insecure origin sets a permanent `workerWarning` that refills the strip after every clear.
- OLD target: the reconnect banner was a fixed overlay (no layout shift) and it hid itself on reconnect. There was no persistent strip.
- OLD: `Components/Layout/ReconnectModal.razor.css:22-35`. NEW: `index.html:207`, `chat.css:15-45`, `app.js:72-92`, `:853`, `:1073-1089`, `worker-updates.js:69-72`.
- Confidence: needs a device check (how long messages stay depends on packet rate).

#### Minor

**D16. The account menu mixes two fonts.** Settings, Notification settings and Admin are now `<a>` elements and inherit Inter. Online, Away, Invisible, Forget offline data and Sign Out are `<button>` elements and keep the browser button font, because `.dropdown-item` sets no font family. OLD: all items were buttons, so one font. OLD `Components/ChatHeader.razor:59-110`, `ChatHeader.razor.css:228-242`. NEW `index.html:101-159`, `shared.css:247-263`. Read from code.

**D17. "Forget offline data" has no icon.** All its siblings have a dot or an SVG, so its label does not line up. New item, no OLD equivalent. NEW `index.html:147`. Read from code.

**D18. Emoji sizes in shell text changed.** OLD rendered every shell emoji at `1em` (`inline: true`). NEW `richText(…, true)` gives a fixed 18px unless an override exists. Overrides exist only for `.user-name`, `#title` and `#status-username`. So room names, the 👑 and 🤖 sidebar badges, and the profile card name, badges, bio, "No bio yet" line, 📍 and 🗓️ show 18px emoji (the card badge was 13.6px, the meta line 13px). The message-scope rule `.bot-badge { font-size: 0.85rem }` now also applies to the sidebar badge. OLD `Services/EmojiService.cs:83-91`, `Components/ChatSidebar.razor.css:200-209`, `UserProfileCard.razor:22-31`. NEW `content.js:132-170`, `messages.css:953-984`, `shared.css:975-979`. Read from code.

**D19. Push prompt: no fade-out and no "Enabling..." text.** NEW removes the overlay at once and only disables the button. OLD faded out over 300 ms and changed the label to "Enabling...". Show rules, copy, and "Maybe Later" counting are the same. OLD `Components/PushPermissionPrompt.razor:25-29`, `:107-115`, `PushPermissionPrompt.razor.css:13-15`. NEW `pwa.js:105-108`, `:145-176`. Read from code.

**D20. Opening the site root in a normal browser tab resumes the last conversation.** NEW `pwa.resume()` replaces `/` with the saved route for every visit. OLD resumed the last route only inside the installed PWA, else `/lobby`. OLD `Components/Pages/Welcome.razor:63-69`, `wwwroot/js/chat.js:640-643`. NEW `pwa.js:255-260`, `app.js:1091`, `Yap/Offline/ChatRoutes.cs:9-12`. Read from code.

**D21. Returning to the app does not jump to the bottom.** OLD always scrolled to the bottom on visibility return. NEW keeps the position unless the view was already following the bottom. Overlaps the conversation area. OLD `Components/Pages/ChatBase.cs:667-673`. NEW `app.js:1207-1215`, `scroll.js:7-15`. Read from code.

**D22. One Escape now closes every open layer.** Profile card, warning modal, pickers, message menu or delete dialog, touch action bar and gallery each listen on `document` with no order. OLD Escape only closed the gallery, the focused delete confirm and the focused edit box. Edit still wins in NEW (it stops propagation). The account menu and the sidebar have no Escape in either version. OLD `wwwroot/js/chat.js:185`, `:547`, `Components/MessageItem.razor:621-625`. NEW `profiles.js:100-102`, `warnings.js:40-42`, `pickers.js:115-117`, `actions.js:254-258`, `:398-403`, `gallery.js:87-88`, `:208`. Read from code.

**D23. Sign out shows a cleared chat and a "Sign in" notice before the sign-out page loads.** NEW runs `loseAccount(true)` (clears UI, writes the sign-in notice) and then sets `location.href`. A second confirm appears when unsent work exists. OLD navigated straight to `/auth/signout` after the optional "no way back" confirm. OLD `Components/ChatHeader.razor:210-257`. NEW `app.js:787-803`. Read from code.

**D24. The error surface changed.** NEW shows `#recovery` ("Something went wrong", Try Again, Reload Page) at the top of the conversation on any `window` error or any unhandled promise rejection, without the error text. OLD showed the ErrorBoundary only for render exceptions, with the message line, and the full-screen "gremlins" modal for circuit errors. Fire-and-forget async handlers in NEW (`popstate` render, Forget, Sign out, `navigate()`) can reach it on an IndexedDB failure. OLD `Components/Layout/ChatLayout.razor:33-45`, `Components/App.razor:84-107`. NEW `index.html:210-216`, `app.js:1232-1249`, `chat.css:101-104`. Unsure: how often it fires needs a device check.

**D25. Sidebar rows are links now.** Long-press on a room or person opens the browser link menu or link preview on phones. Ctrl, Shift or middle click opens a new tab on desktop. OLD rows were `<div>` elements. OLD `Components/ChatSidebar.razor:24`, `:80-81`. NEW `app.js:289-296`, `:313-318`, `:382-388`. Needs a device check.

**D26. The app badge no longer asks for notification permission.** OLD `setAppBadge` called `Notification.requestPermission()` inside the installed PWA when permission was "default". NEW only sets or clears the badge. OLD `wwwroot/js/chat.js:561-581`. NEW `app.js:433-436`. Read from code.

**D27. A narrow desktop window (768px or less) gets the info button and the centered card instead of hover.** OLD used the User-Agent, so desktop always used hover. OLD `Components/ChatSidebar.razor:86`, `:225-232`. NEW `shared.css:995-1002`, `app.js:392-395`. Read from code.

**D28. Room header text is chosen once per page load, and it changes once after load.** OLD picked a new random room header on every room navigation. NEW caches one per conversation. Before the label catalog loads, NEW shows the fallback "# name", then re-renders with a random label. OLD `Services/ChatNavigationState.cs:62-69`, `Components/Pages/RoomChat.razor:140`, `:181`, `Services/ChatConfigService.cs:72-73`. NEW `content.js:98-112`, `app.js:645-654`, `:1112-1114`. Read from code.

**D29. Desktop tooltips are gone on the back and sidebar buttons.** OLD had `title="Back to lobby"` and `title="Toggle sidebar"`. NEW has only `aria-label`. OLD `Components/ChatHeader.razor:20`, `:131`. NEW `index.html:72`, `:173-177`. Read from code.

**D30. A DM link to an unknown user no longer redirects to the lobby.** NEW shows the D10 placeholder plus the server error in the notice strip. OLD redirected to `/lobby`. OLD `Components/Pages/DmChat.razor:108-114`. NEW `app.js:468-487`. Read from code.

**D31. Settings, Notification settings, Admin, Add Room and channel settings are full page loads.** OLD used in-circuit navigation. All of them still close the sidebar and menu first. OLD `Components/ChatHeader.razor:394-420`, `Components/ChatSidebar.razor:460-470`. NEW `index.html:112-145`, `app.js:330-345`, `:770-777`. Read from code.

**D32. The DM sound mute rule changed.** OLD skipped the sound when the CURRENT DM was muted. NEW skips it when the ARRIVING DM is muted. OLD `Components/Pages/ChatBase.cs:611-612`. NEW `notifications.js:113-123`. Read from code.

---

### Mechanism pairs

Places where NEW has two mechanisms for one visible state, or makes a call OLD did not.

**M1. Several reconnect triggers with no shared timer.** `scheduleReconnect()` (from `onclose`, stream error, failed attempt, superseded attempt in `finally`) and direct `connectChat()` calls (visibility, `online`, `pageshow`, Retry) do not clear each other. `connectChat()` starts with `live.detach()` and `hubConnection.stop()`, so a late timer restarts a healthy connection. `app.js:866-870`, `:890-896`, `:1010-1014`, `:1195-1215`, `:1240-1249`. OLD had one loop: `Components/App.razor:126-144`. See D5.

**M2. Account menu close: backdrop element plus a document click listener.** `#menu-backdrop` has no handler of its own. Closing happens in the document `click` listener, which also runs for every click on the page. Status items, menu links and Sign out also call `toggleMenu(false)`. The visible result matches OLD today, but removing either part changes it: without the backdrop a tap outside would reach the control under it. `index.html:99`, `app.js:751-780`, `:788`. OLD: one mechanism, `Components/ChatHeader.razor:57`, `:195-198`.

**M3. Full rebuild of the sidebar on every live packet and twice per render.** OLD diffed in place. See D2. `app.js:207-217`, `:697`, `:718`.

**M4. Focus calls on overlays that OLD did not make.** `warnings.js:38` (focus "Got It"), `warnings.js:28` (restore focus). Same pattern outside this area: `gallery.js:214` (focus dismiss on open) and `gallery.js:28` (restore focus on close); OLD gallery made no focus calls. On a phone, restoring focus to the draft inside a tap opens the keyboard. See D12.

**M5. Global error listeners added.** `app.js:1235-1238` listens to `error` and `unhandledrejection` and shows `#recovery`. OLD had only the render ErrorBoundary and the Blazor error UI. See D24.

**M6. The notice strip has many writers and one blanket clear.** `notice('')` after every accepted snapshot (`app.js:853`) clears any transient text. Lasting text is refilled only from `workerWarning` and `shellUpdated` (`app.js:75-76`). The "Sign in" text and the "Client update required" text are not in that refill set. A later `notice('')` (for example the boot worker-registration `catch` at `app.js:1073-1080`) can replace the update-required text and its Reload button. Unsure: the race needs a slow worker registration plus a 426 bootstrap.

**M7. Three indicators for one connection state, and one of them reuses a user state.** The chip class (`live.view?.status || 'invisible'`), the disabled status items (`live.connected`) and the `#connection` label all report connection loss. The chip shows "disconnected" with the same grey as the user's own Invisible choice. `app.js:258-268`, `:65-71`. See D4.

**M8. Last route is stored twice.** `pwa.remember()` writes IndexedDB metadata and `localStorage['yap-last-route']`. `pwa.resume()` reads only IndexedDB. The localStorage copy feeds the retained Blazor Welcome page. `pwa.js:255-270`. See D20.

**M9. Notification audio priming is gated and one-shot.** The first `click` or `keydown` primes audio only if `identity()` is already set, and the listener is removed after that first event either way. A tap during boot leaves audio unprimed, and the element is then created at play time outside a gesture. OLD primed without a gate, and again in `setupVisibilityListener`. `notifications.js:34-43`, `:141-144`. OLD `wwwroot/js/chat.js:52-63`, `:67-69`. Unsure: browser autoplay rules decide whether this matters.

**M10. Calls OLD did not make on every foreground.** `worker-updates.js:94-99` fetches the shell manifest and calls `registration.update()` on every visibility return. A found update can show "Yap was updated. Reload when convenient." in the middle of a session (`app.js:1050-1067`). `pwa.start()` is also called from both the hub connect and each `CHAT_OFFLINE_READY` reply, each time with a `get('session')` (`app.js:953`, `:1036-1041`). Guards prevent a second prompt.

**M11. A call OLD made that NEW dropped.** OLD `setAppBadge` requested notification permission inside the installed PWA (`wwwroot/js/chat.js:565-567`). See D26.

**M12. Theme is applied from four places.** `js/appearance.js:21-34` (blocking, before CSS), `app.js:833` (each state snapshot), `app.js:1120-1124` (boot from IndexedDB), `app.js:1180-1181` (sibling tab). OLD: server-rendered `data-theme`, plus `applyTheme` from Settings only. NEW applies a theme changed on another device during the session. No conflict found.

**M13. Escape has five independent document listeners.** No precedence, so one key closes all layers. See D22.

**M14. Sign out clears the account UI and then navigates.** `app.js:800-802`. OLD navigated only. See D23.

**M15. Duplicate CSS rules for one element.** `#connection` has two `@media (max-width: 600px)` blocks; the second overrides `max-width: 110px` with `65px` (`chat.css:61-75`). `.bot-badge` (`shared.css:501-504` and `:975-979`), `.avatar` (`:3-11` and `:980-993`) and `.avatar-image` (`:27-31` and `:983-990`) are each defined twice. The second `.bot-badge` came from the old MessageItem scope and now reaches the sidebar (D18).

---

### Unstyled or phone-unready elements

Stylesheets searched for each class or id: `Yap/wwwroot/app.css`, `themes.css`, `themes/teahouse.css`, and in `chat-client/`: `shared.css`, `pickers.css`, `gallery.css`, `media.css`, `composer.css`, `messages.css`, `chat.css`.

| # | Element | Created at | Problem | Rule searched / found |
|---|---|---|---|---|
| U1 | `.install-guide button` ("Use existing app", "Install app") | `pwa.js:38-40`, `:49-58` | Only margin, padding and cursor. Browser grey bevel, system font and dark text on a dark card. | `shared.css:1023-1027` |
| U2 | `.install-guide` border | `pwa.js:28-29` | `border: 1px solid var(--border-color)`; `--border-color` is defined nowhere, so no border. | `shared.css:1010`; no definition in any searched file |
| U3 | `#window-state` "Updating history…" | `index.html:79` | No rule under the phone breakpoint. Sibling `#connection` has two (`chat.css:61-75`). Full text at 0.75rem in a non-shrinking header row (D8). | `chat.css:106-109` only |
| U4 | `.dropdown-item` as `<a>` (Settings, Notification settings, Admin) | `index.html:112`, `:123`, `:134` | Inherit Inter while button siblings keep the browser button font (D16). | `shared.css:247-263` (no font-family), `chat.css:9-11` |
| U5 | `#forget` "Forget offline data" | `index.html:147` | No icon; siblings all have one, so the label is out of line (D17). | `.dropdown-item` only |
| U6 | `.attachment-preview` | `app.js:592-594` | No rule anywhere. (Conversation area, pending upload row.) | none found |
| U7 | `progress.upload-progress` | `app.js:595-601` | No rule; native progress bar. (Conversation area.) | none found |
| U8 | `.history-load-button` "Load earlier messages" | `index.html:224-225` | No font rule; browser button font. (Conversation area.) | `messages.css:993-999` |
| U9 | `#history-note` (`<p class="history-start">`) | `index.html:222-223` | `<p>` keeps the browser's 1em top and bottom margin; OLD used a `<div>`. (Conversation area.) | `messages.css:60-66` (no margin) |
| U10 | `.retry-send` buttons (Retry, Cancel upload) | `app.js:602`, `:614` | No font rule; browser button font. `#notice button` copied this look and added `font: inherit`, but `.retry-send` itself did not. (Conversation area.) | `chat.css:92-100` |
| U11 | Emoji inside `.room-name`, `.admin-badge`, `.bot-badge`, `.profile-card-*` | `app.js:320`, `:414`, `:420`; `profiles.js:31-41`, `:59`, `:64`, `:69`, `:75` | Fixed 18px instead of the OLD 1em (D18). Overrides exist only for `.user-name`, `#title`, `#status-username`. | `messages.css:968-984` |
| U12 | `#outgoing-section` | `index.html:318-319` | Inside `.users-sidebar` with `overflow: hidden` and no scroll; can be clipped (D3). | `shared.css:328-348` |

Checked and styled (no finding): `#notice` and its `a`/`button` (`chat.css:15-45`), `#connection` (`chat.css:5-8`, `:61-75`), `#recovery` (`shared.css:720-765`, `chat.css:101-104`), `.push-prompt-*` (`shared.css:767-854`), `.profile-*` (`shared.css:540-614`, `:856-974`), `.warning-modal*` (`composer.css:446-510`), `.delete-confirm-*` (`messages.css:726+`), `#menu-backdrop`/`.dropdown-backdrop` (`shared.css:227-234`), `.sidebar-backdrop` (`shared.css:701-718`), `#header-avatar` (plain wrapper, needs no rule), `[hidden]` states (`chat.css:2-4`, `!important`, so `display: flex` rules cannot override it).

---

### State table

Verdict values: same, different, new-only, old-only, unsure.

| ID | State / trigger | Input | OLD behaviour (file:line) | NEW behaviour (file:line) | Verdict |
|---|---|---|---|---|---|
| S1 | Open sidebar with hamburger (768px or less) | touch | Slides in from the right, transparent backdrop below header (`ChatHeader.razor:131`, `ChatSidebar.razor.css:10-24`, `ChatLayout.razor.css:206-216`) | Same classes and CSS (`app.js:728-732`, `:762`, `shared.css:335-348`, `:704-714`) | same |
| S2 | Close sidebar by backdrop tap | touch | Closes (`ChatLayout.razor:51`) | Closes (`app.js:763`) | same |
| S3 | Close sidebar by Escape | keyboard | No handler | No handler | same |
| S4 | Close sidebar by swipe | touch | Not supported | Not supported | same |
| S5 | Pick a room in the sidebar | touch/mouse | Closes sidebar and profile card, navigates (`ChatSidebar.razor:452-458`) | Same (`app.js:438-443`) | same |
| S6 | Pick a person / own row | touch/mouse | DM opens, sidebar closes; own row does nothing (`ChatSidebar.razor:476-484`) | Same (`app.js:387-388`, `:438-443`) | same |
| S7 | Mailbox button | touch/mouse | Opens sidebar (`ChatHeader.razor:384-387`) | Opens sidebar (`app.js:750`) | same |
| S8 | Person list while not connected | none | Not reachable; list kept last state during reconnect | All known people with grey dots (`app.js:366-368`) | new-only |
| S9 | Sidebar longer than screen | touch/mouse | Clipped, no scroll (`ChatSidebar.razor.css:6`) | Clipped, no scroll, longer list and OUTGOING at the end (`shared.css:332`, D3) | different |
| S10 | Live packet while sidebar is open | touch/mouse | Rows diffed in place | Rows rebuilt (`app.js:311`, `:348`, D2) | different |
| S11 | Back button in a DM | touch/mouse | Goes to /lobby, tooltip "Back to lobby" (`ChatHeader.razor:20-24`, `:389-392`) | Goes to /lobby, no tooltip (`index.html:72`, `app.js:749`) | same (tooltip D29) |
| S12 | Header title in a room | none | Random room header per navigation (`ChatNavigationState.cs:62-69`, `ChatConfigService.cs:72-73`) | One random label per conversation per page load, fallback first (`content.js:98-112`, `app.js:645-654`) | different |
| S13 | Header title in a DM | none | Avatar + display name (`ChatHeader.razor:16-29`) | Avatar + display name (`app.js:658-668`) | same |
| S14 | Header for a DM not yet created | touch/mouse | Not possible; DM created synchronously (`DmChat.razor:116-131`) | "Yap", no back, placeholder note (`app.js:645-668`, D10) | different |
| S15 | Online count (desktop) | none | Count of online users (`ChatHeader.razor:140`) | Count from live view; blank while not live (`app.js:269`) | different |
| S16 | Status chip colour | none | Effective status from first render (`ChatHeader.razor:48`, `:180-181`) | Grey until first live packet and while disconnected (`app.js:258-261`, D4) | different |
| S17 | Open/close status menu | touch/mouse | Chip toggles; backdrop tap closes (`ChatHeader.razor:48`, `:55-58`) | Chip toggles; backdrop and document click close (`app.js:764-780`) | same |
| S18 | Status menu by Escape | keyboard | No handler | No handler | same |
| S19 | Choose Online/Away/Invisible | touch/mouse | Sets status, menu closes (`ChatHeader.razor:200-208`) | Same when connected; items disabled when not (`app.js:751-761`, `:267`) | different |
| S20 | Notification settings icon under server mute | none | Crossed bell (`ChatHeader.razor:82-93`) | Plain bell always (`index.html:123-133`) | old-only |
| S21 | Settings / Notification settings / Admin | touch/mouse | In-circuit navigation, closes sidebar (`ChatHeader.razor:394-420`) | Full page load, closes menu and sidebar (`app.js:770-777`) | different |
| S22 | Forget offline data | touch/mouse | Not present | Confirm, clear, sign-in notice (`app.js:781-786`) | new-only |
| S23 | Sign out | touch/mouse | Optional confirm, then /auth/signout (`ChatHeader.razor:210-257`) | Two possible confirms, cleared UI flash, then /auth/signout (`app.js:787-803`) | different |
| S24 | Profile card, desktop hover | mouse | Card near cursor, no pointer events (`ChatSidebar.razor:225-232`, `.css:274-280`) | Same (`profiles.js:95-98`, `shared.css:565-571`) | same |
| S25 | Profile card, phone portrait | touch | Info button opens centered card + dim backdrop (`ChatSidebar.razor:86-99`, `:137-143`) | Same (`app.js:397-406`, `profiles.js:91-94`) | same |
| S26 | Profile card, phone landscape / wide tablet | touch | Info button by User-Agent | No button, no hover (D1) | different |
| S27 | Profile card, narrow desktop window | mouse | Hover | Info button + centered card (D27) | different |
| S28 | Close card by backdrop tap | touch | Closes (`ChatSidebar.razor:139`) | Closes (`profiles.js:93`) | same |
| S29 | Close card by Escape | keyboard | No handler | Closes (`profiles.js:100-102`) | new-only |
| S30 | Close card by navigation | touch/mouse | Closes (`ChatSidebar.razor:454`, `:478`) | Closes (`app.js:439`) | same |
| S31 | Card position at phone width | touch | Centered, `min(320px, 100vw - 32px)` (`ChatSidebar.razor.css:282-289`) | Same (`shared.css:572-579`) | same |
| S32 | Emoji size in card, room names, badges | none | 1em (`EmojiService.cs:87`) | 18px (D18) | different |
| S33 | Room row unread: badge, dot, bell | none | Count if unmuted, dot if muted, bell for per-room choice (`ChatSidebar.razor:27-45`) | Same (`app.js:297-309`) | same |
| S34 | Person row unread: badge, dot, bell | none | Same rule as rooms, bell for any effective mute (`ChatSidebar.razor:108-125`) | Same (`app.js:297-309`, server `muteBell`) | same |
| S35 | Mailbox count | none | Unmuted unread, minus the open DM (`ChatHeader.razor:259-289`, `ChatService.cs:1599-1610`) | Same (`app.js:427-432`) | same; changed 2026-10-12 after a user report, see Progress |
| S36 | App icon badge | none | Same count; asks permission in PWA (`chat.js:561-581`) | Same count; no permission request (`app.js:433-436`) | different |
| S37 | Add Room | touch/mouse | Closes sidebar, in-circuit navigation (`ChatSidebar.razor:460-464`) | Closes sidebar, full load (`app.js:340-346`) | different |
| S38 | Room settings gear | touch/mouse | Closes sidebar, in-circuit navigation (`ChatSidebar.razor:466-470`) | Closes sidebar, full load (`app.js:323-337`) | different |
| S39 | Sound, DM page, tab hidden | none | Any DM to user; skipped if current DM muted (`ChatBase.cs:611-624`, `DmChat.razor:213-221`) | Any unmuted arriving DM (`notifications.js:110-144`) | different |
| S40 | Title count, room page, tab hidden | none | None with default room mute (`ChatBase.cs:611-612`) | Counts unmuted arrivals (`notifications.js:80-95`, D11) | different |
| S41 | Title count, DM page, tab hidden | none | Counts DMs (`ChatBase.cs:619-625`) | Counts unmuted DMs (`notifications.js:80-95`) | same |
| S42 | Title reset on return | none | Count cleared (`ChatBase.cs:648-653`) | Count cleared (`notifications.js:51`, `:63`) | same |
| S43 | Push prompt shown | none | Installed PWA, permission default, fewer than 3 dismissals (`chat.js:661-670`) | Same (`pwa.js:195-200`) | same |
| S44 | Push prompt Enable | touch | "Enabling...", then fade-out (`PushPermissionPrompt.razor:25-27`, `:107-115`) | Button disabled, instant removal (`pwa.js:156-167`) | different |
| S45 | Push prompt Maybe Later | touch | Count +1, fade-out (`PushPermissionPrompt.razor:170-175`) | Count +1, instant removal (`pwa.js:168-174`) | different |
| S46 | Push prompt by Escape / backdrop | keyboard/touch | No close | No close | same |
| S47 | PWA install banner | none | Disabled (`PwaInstallBanner.razor:1-6`, `ChatLayout.razor:53`) | None | same |
| S48 | Install link in bot DM | touch | Native prompt or guide at once (`chat.js:709-744`) | Intermediate dialog with bare buttons first (D14) | different |
| S49 | Online and synced | none | Nothing shown | `#connection` hidden (`app.js:68`) | same |
| S50 | First load | none | Blank until circuit renders; then full state | "Opening cached chat…"/"Syncing", grey chip, "Account" text (`index.html:80`, `:88`, D4) | different |
| S51 | Socket lost, reconnecting | none | Orange top banner with countdown (`ReconnectModal.razor:32-70`) | Header label "Disconnected · cached chat"/"Offline" (`app.js:981`, `:992`) | different |
| S52 | Device offline | none | Same banner as S51 | "Offline · cached chat", cached chat usable (`app.js:1203-1206`, `:1128`) | different |
| S53 | Session ended or revoked | none | Redirect to /auth/signout, then login (`ChatLayout.razor:117-133`) | Cleared page, "Sign in" label and notice (`app.js:142-155`, D9) | different |
| S54 | Logged-out visit to /lobby | none | Redirect to `/?returnUrl=` (`ChatLayout.razor:73-79`) | Shell served, sign-in notice (`OfflineEndpoints.cs:31-36`, D9) | different |
| S55 | New release deployed | none | Circuit rejected, auto-resume or reload (`ReconnectModal.razor.js:156-181`) | Notice "Yap was updated…" + Reload; or forced update notice on 426 (`app.js:72-110`) | different |
| S56 | Warning modal opens | touch/mouse | Modal, focus unchanged (`MessageInput.razor:24-33`) | Modal, focus to "Got It" (`warnings.js:38`) | different |
| S57 | Warning modal ✕ / Got It / backdrop | touch/mouse | Closes (`MessageInput.razor:26-31`) | Closes and restores focus (`warnings.js:26-34`) | different |
| S58 | Warning modal Escape | keyboard | No handler | Closes (`warnings.js:40-42`) | new-only |
| S59 | Delete dialog layer | touch/mouse | Inside `.messages-flow` (z 1 context); send button could paint over it (`app.css:173-181`) | On `body`, z 1100, covers everything (`actions.js:185`, `messages.css:726-733`) | different |
| S60 | Delete dialog Escape | keyboard | Cancels (focused button) (`MessageItem.razor:621-625`) | Cancels (`actions.js:398-403`) | same |
| S61 | Escape with several layers open | keyboard | Only gallery/delete/edit react | All layers close at once (D22) | different |
| S62 | Unhandled script error | none | ErrorBoundary for render errors; gremlins modal (`ChatLayout.razor:33-45`, `App.razor:84-107`) | `#recovery` on any error or rejection (`app.js:1232-1249`) | different |
| S63 | Viewport meta | none | `width=device-width, initial-scale=1.0, interactive-widget=resizes-content` (`App.razor:11`) | Same content (`index.html:4-7`) | same |
| S64 | iOS home-screen metas | none | Present (`App.razor:70`, `:73-74`) | Absent (D13) | old-only |
| S65 | Safe-area insets, viewport-fit | none | No `viewport-fit`; bottom inset only in mobile sheets (`MessageItem.razor.css:980`) | Same (`messages.css:907`) | same |
| S66 | Theme and font size at first paint, online | none | Server-rendered on `<html>` (`App.razor:5-6`) | Server-injected, mirrored by blocking script (`OfflineEndpoints.cs:41-51`, `js/appearance.js:21-26`) | same |
| S67 | Theme at first paint, worker-served offline | none | Not possible | From localStorage mirror; default theme if the mirror is empty, then IndexedDB (`js/appearance.js:27-34`, `app.js:1120-1124`) | new-only |
| S68 | Tea House scene before first paint | none | Inline script before CSS (`App.razor:19-56`) | Blocking script before CSS (`index.html:9`, `js/appearance.js:36-79`) | same |
| S69 | Theme changed on another device | none | Applied on next load | Applied live (`app.js:833`) | new-only |
| S70 | Header font sizes at 600px or less | none | Title 1.25rem, name max 80px, padding 0.75rem (`ChatHeader.razor.css:306-318`) | Same (`shared.css:315-325`) | same |
| S71 | Header crowding on narrow phones | none | Chip + mailbox + hamburger only | Plus connection label and "Updating history…" (D8) | unsure (needs a device check) |
| S72 | Notice strip | none | No strip | In-flow strip, no close (D15) | new-only |
| S73 | Site root `/` in a browser tab | none | Lobby (last route only in PWA) (`Welcome.razor:63-69`) | Last route (`pwa.js:255-260`) | different |
| S74 | Return from background | none | Scroll to bottom, mark read, scene, title reset (`ChatBase.cs:636-674`) | Reconnect if needed, read check, badge reapply, scene, title; no forced scroll (`app.js:1207-1215`) | different |
| S75 | Notification click with app open | touch/mouse | Full load of the URL (`chat.js:872-879`) | In-app navigation for chat routes (`pwa.js:231-236`) | different (faster, same destination) |
| S76 | Sidebar row long-press | touch | Text selection on a `<div>` | Link menu or preview on `<a>` (D25) | unsure (needs a device check) |
