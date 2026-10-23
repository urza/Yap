# Spec versus code, 2026-10-23

A check of docs/spec against the code at b4a71e6f, after the commits of 2026-10-16 to 2026-10-23.
The spec sentences that were stale after a deliberate code change are fixed in the same commit as this file.
This file lists the places where the code does not follow the spec. The spec is right until a decision says
otherwise. Nothing in the code was changed.

Each item: the rule, what the spec says, what the code does, where, and the commit that made the difference.

## The list replaced on a reconnect

Since d50b3908 (2026-10-22) a return to the app after more than 30 s in the background makes a new
connection, and every new connection replaces the message list. Three rules break on that path.

- **TL-15, TL-18.** The spec: on a return, a view that did not follow stays, and the New messages pill shows
  when messages arrived. The code: the list is replaced, the pill is hidden on the reset and nothing shows it
  again. The down arrow shows instead. `hub.js:185-187`, `chat.js:59,121`, `timeline.js:49,62`.
- **TL-29, TL-20.** The spec: the viewer closes on a conversation switch and when its message is deleted.
  The code: the viewer closes on every list reset too. That includes a reconnect, a return after 30 s, and
  the server's answer that replaces the stored copy at an open (about 3 s on a slow link). `gallery.js:167-170`.
- **MR-62.** The spec: the row with the editor continues to receive live changes. The code: a list reset
  removes the row with the editor, and the draft is lost. `timeline.js:45`, `message.js:401-432`.

## Presence and unread

- **PR-24, NT-26.** The spec: a visible session of an online person suppresses notifications to the other
  devices. The code reads only the visible flag. It does not apply the 35 s heartbeat limit that Looking got
  in 09aa0b4b. A laptop that sleeps with Yap on screen blocks phone notifications until the connection drops.
  `PresenceService.cs:353-357`.
- **NT-07.** The spec: a message in the open conversation never counts for a person who reads it. The code
  skips readers for rooms only (26424384). A direct message still counts +1 for the reader, and the client
  then clears it. Another device of the reader can show the count for a moment. `ChatService.cs:953-962`.

## GIF picker

- **PK-16.** The spec: a GIF uploaded from the picker is not added to Recent. The code: the picker sends the
  upload by its entry id, and the server adds it to Recent like any pick. `gif-picker.js:281`,
  `MessagesEndpoints.cs:45`, `GifService.cs:413`.
- **ST-01.** The spec: "Manage your GIFs in Settings" scrolls to the My GIFs section. The code: the link
  opens plain /settings, the top of the page. Lost in the move off Blazor. `gif-picker.js:153`.

## New version and sign-out

- **OF-04, SH-20.** The spec: the strip announces a new version, and Yap never reloads an open page by
  itself. The code: when the root address serves the new page while the stored data still carries the old
  version number, the strip shows at once, although the page is already the new build, and the page then
  reloads once by itself (the SH-50 fresh load). On a slow link the strip can stay until Reload. Traced by
  hand, not run. `main.js:83-85,161-168`, `service-worker.js:24,56`.
- **SH-20, OF-18.** The spec expects one Reload to give the new version. The code never asks the browser to
  check for a new version. In a tab open before the deploy, the first Reload can open the old version again.
  `main.js:90-94`.
- **GEN-05, OF-16.** The spec: a sign-out removes all data of the account from the device. The code keeps the
  saved pictures and videos. Only Settings clears them. `session.js:61-72`, `settings.js:449`.

## Texts in the code that are stale (not spec items)

- The "Older" header bubble on the admin latency table says "Scroll to the top until the older messages are
  in place." The request starts two screens early since fb5a7fa0, and the wait for a still view does not
  count since 61ce3ca1. `admin.js:570`.
- The Load time hint says "One row per page load since the restart". The table shows the live connections and
  the last 50 closed. `admin.js:620`.
- The comment "The people list is every account" is wrong. The list is the people online or away, the direct
  message partners and the user. `frame.js:327`.
- The loading pill and the catching-up pill share one spot. They overlap when the user scrolls up on stored
  rows after the connection is up and before the server answers the open. `timeline.css:378`.

## Not verified

No browser run. The iOS paths (the still view hold, the share sheet), the new-version strip timing and the
HTTP column for a page from the device cache need a device or a browser.
