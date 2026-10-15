# Yap user testing, 2026-10-15

Five additional user-facing problems reproduce in the current version. The most significant problem sends a GIF when the user activates its favorite star with a keyboard.

Tested commit: `5764c4afad389be7e0c7106791ff238a8aa760e9`.

Playwright drove real Chromium through ordinary sign-in, chat, settings, and upload controls. Tests used disposable local accounts and copied application data. No application fixes are included.

| ID | Priority | Problem |
| --- | --- | --- |
| U01 | P2 | Enter or Space on a GIF favorite star sends the GIF to the conversation |
| U02 | P2 | Emoji insertion exceeds the message limit and leaves an uneditable failed message |
| U03 | P2 | Keyboard users cannot reach conversation links or the upload control |
| U04 | P3 | A room rename leaves the open header and browser title stale |
| U05 | P3 | Large text clips notification choices on a narrow phone |

P2 means a functional problem that needs correction. P3 means a lower-priority display problem.

## U01. The GIF favorite star sends a message when activated with a keyboard

1. Open Settings and add a GIF through **My GIFs**.
2. Return to a room with another user present.
3. Open the GIF picker and select Favorites.
4. Focus a GIF card, then press Tab to focus its star. Its tooltip says **Remove from favorites**.
5. Press Enter. Repeat with Space.

**Expected:** only the favorite state changes. No message is sent.

**Actual:** each key sends the GIF and closes the picker. The other account receives it. The GIF remains a favorite.

The repeat recorded the recipient's GIF count as `0 -> 1 -> 2`. Clicking the same star with a mouse leaves the count at two.

This can publish a personal GIF when the user intends to remove it from Favorites.

The card's keydown handler accepts events from its child button. Only the star's click handler stops propagation. See [gif-picker.js](../Yap/wwwroot/js/ui/gif-picker.js), lines 202 and 212.

**Suggested correction:** activate a card only when the card itself receives the keyboard event. Keep the star's native button behavior. Check Enter, Space, and mouse clicks separately.

Evidence: `U01` in [results.json](2026-10-15-user-testing-evidence/results.json), [focused star](2026-10-15-user-testing-evidence/gif-star-before-enter.png), and [recipient's messages](2026-10-15-user-testing-evidence/gif-star-recipient.png). Related rule: PK-21.

## U02. Emoji insertion bypasses the 4,000-character limit

1. Paste 4,000 characters into the message box.
2. Open the emoji picker and select `:666:`.
3. Send the message.

**Expected:** the composer enforces the same limit for typed text and emoji picks. It preserves text that needs correction.

**Actual:** the draft grows to 4,005 characters. Send clears the composer and creates a failed row. Its reason says **A message has at most 4000 characters.**

The failed row has Retry and Remove, but no Edit action. Retry produces the same error. The text remains in the failed row, so recovery through copying is possible.

The input handler enforces the limit, but `insertEmoji()` assigns the expanded value directly. Failed rows also lose their Edit action. See [composer.js](../Yap/wwwroot/js/ui/composer.js), lines 111 and 303, and [message.js](../Yap/wwwroot/js/ui/message.js), line 306.

**Suggested correction:** check length before emoji insertion and before queuing a message. Let users correct failed text without rebuilding their draft.

Evidence: `U02` in [results.json](2026-10-15-user-testing-evidence/results.json) and [failed message](2026-10-15-user-testing-evidence/emoji-limit-failed.png). Related rule: IB-14.

## U03. Tab skips conversation navigation and uploads

1. Sign in as an ordinary user on desktop.
2. Open a room. Ensure another room and another user appear in the sidebar.
3. Focus the message box and press Tab through a full focus cycle.

**Expected:** users can reach rooms, direct messages, and Upload files with a keyboard.

**Actual:** focus cycles through the composer buttons and header controls. It never reaches the sidebar conversations or upload control. The Add Room control also lacks keyboard focus for administrators.

Room and user rows are clickable `div` elements with `tabIndex=-1` and no role. Upload uses a non-focusable label around a hidden file input. No corresponding keyboard activation handlers provide another route.

See [index.html](../Yap/wwwroot/index.html), lines 242, 255, 263, and 533, and [frame.js](../Yap/wwwroot/js/ui/frame.js), lines 227 and 298.

**Suggested correction:** use links or buttons for navigation and a keyboard-operable upload button. Keep the settings/profile controls distinct from their parent navigation links.

Evidence: `U03` in [results.json](2026-10-15-user-testing-evidence/results.json) contains the complete focus cycle and control attributes. This finding concerns keyboard operation; it is not a full screen-reader audit.

## U04. Renaming an open room leaves two different names on screen

1. Create `review-room` and open it in two accounts.
2. As administrator, open Channel Settings, rename it to `renamed-room`, and save.
3. Observe the other account without reloading.

**Expected:** the sidebar, conversation header, and browser title show the new name.

**Actual:** the sidebar and beginning-of-history note update. The header still shows `review-room`, and the browser title remains `Yap | #review-room`.

The repeat waited another 1.2 seconds after the sidebar update. Reloading corrects both names and preserves the draft. The exploratory run also showed the stale header in the administrator's tab.

Room updates redraw the sidebar, while the conversation title is set during page mounting. See [frame.js](../Yap/wwwroot/js/ui/frame.js), lines 39 and 60, and [chat.js](../Yap/wwwroot/js/pages/chat.js).

**Suggested correction:** refresh the open room's header and document title when its room metadata changes.

Evidence: `U04` in [results.json](2026-10-15-user-testing-evidence/results.json) and [conflicting room names](2026-10-15-user-testing-evidence/room-rename-stale.png).

## U05. Large text hides the phone's Mute all labels

1. Use a 320 x 568 phone viewport.
2. Open Settings and select the supported **20px** text size.
3. Scroll to **Allow / Mute Notifications**.

**Expected:** all choices remain readable at the selected text size.

**Actual:** both Direct messages and Rooms clip the final **Mute all** option. Only a narrow portion remains visible. A selected option can therefore lose its label.

Each control has 315 pixels of content inside a 238-pixel container. The final option extends to x=355.69 on a 320-pixel viewport. The screenshot uses Solarized Light; the overflow comes from layout sizing.

See the segmented control rules in [pages.css](../Yap/wwwroot/css/pages.css), lines 3489 and 3617.

**Suggested correction:** let the options wrap or stack when the selected text size exceeds the available width.

Evidence: `U05` in [results.json](2026-10-15-user-testing-evidence/results.json) and [clipped notification choices](2026-10-15-user-testing-evidence/phone-mute-clipped.png).

## Checks that passed

The build completed with zero warnings and zero errors. The existing browser suites reported:

| Suite | Result |
| --- | --- |
| Smoke | 16 passed, 1 timing-related failure; same result on repeat |
| Features | 11 passed |
| Pages | 15 passed |
| Offline | 14 passed |

The smoke failure is step 17. It reads the conversation type immediately after the URL changes, before the destination finishes rendering. Its later Back checks succeed. The separate replay waits for the destination to render and confirms that Back reopens the DM. This is not counted as a product finding.

Passing flows include sign-in, messages, replies, reactions, edits, deletion, direct messages, and live presence. They also include multiple GIF uploads, image galleries, profile and theme changes, account switching, and cross-tab sign-out.

Offline checks cover pending sends, reload without a network, reconnect, history gaps, and queued file uploads. Exploratory checks also confirmed draft preservation across navigation, reload, and room permission changes. Unsupported file selection shows an error and preserves the draft.

## Environment and limits

Tests used .NET SDK 10.0.401, Playwright 1.47.2, and installed Chromium 156.0.8078.4 on Linux. Desktop was 1280 x 800. Phone emulation used 390 x 844 and 320 x 568 with touch enabled.

The workspace drive stalled on .NET diagnostic socket operations. The successful runs used a temporary memory-backed filesystem and disabled .NET diagnostics. Source came from an archive of the tested commit. The web root and runtime data were copied, so test uploads did not modify repository data.

The existing suites used their supplied isolated configuration. The focused replay also copied the shipped `appsettings.json`. Both reproduced the reported user interactions. All synthetic servers used in successful runs were stopped afterward.

Physical mobile keyboards, Safari, Firefox, real Web Push delivery, configured Klipy search, and external video extraction were not tested. This run does not repeat the previous security review or verify every earlier finding.

The [evidence directory](2026-10-15-user-testing-evidence/README.md) contains the replay script, results, screenshots, and baseline logs.
