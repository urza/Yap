# Spec: choices waiting for a ruling (2026-10-14)

Every rule in `docs/spec/` was checked against the pre-rewrite page and the collected facts. Where the spec differs from that page without an earlier ruling, the choice is listed here with its rule ID. Each line is a product decision; flip it and the rule changes. Items marked *defect fix* only repair something the old page did wrong.

## Input box
- IB-53 drafts per conversation, kept across reload.
- IB-14 message length 4000 characters.
- IB-32 at most 10 files per message, with a message for a larger pick.
- IB-17 Escape closes the topmost layer: a dialog, else a picker.
- IB-16 room placeholder picked once per visit.
- IB-43 a conversation switch closes an open picker.
- IB-61 the typing strip shows in read-only rooms.
- IB-12 Enter inserts a newline on any device that can touch, touchscreen laptops included.
- IB-27 a send closes the reply bar at once.
- IB-31 a file the server cannot take is refused before the upload (*defect fix*).
- IB-41 the phone sheet remembers its tab until reload.
- IB-54 nothing of a draft reaches the server before Send.
- IB-56 a draft survives a room turning read-only.
- IB-34 "Cancel upload" discards what was already uploaded for that message.
- Drop uploads only on the box; pasted non-media files ignored silently; old upload dialog wording; the pending row replaces the 15 s ghost.
- Typing texts for three or more people: {1} is the number of others; the configured texts need a touch-up.

## Message row
- MR-10 typed markup shows as text.
- MR-50 the first tap on a touch row acts and reveals the bar.
- MR-45 reactions allowed in read-only rooms.
- MR-39 reaction tooltips show display names.
- MR-35 a reply to a deleted message reads "Original message unavailable".
- MR-40 a reaction shows at once, before the server answers.
- MR-46 the action bar shows on keyboard focus.
- MR-72 a pending message offers Edit and Remove only.
- MR-75 "Needs a connection" at the row for two seconds.
- MR-13 an e-mail address and its domain are not links.
- MR-23 a media player downloads nothing until played.
- MR-43, MR-55 Escape closes the reaction picker and the More menu.
- MR-61 an edit holds at most 4000 characters.
- *Defect fixes:* link cards stay once shown; an edit adds or removes its card; brackets inside a link stay in it; emoji-only sizing per message; all images render.

## Pickers
- PK-28 provider results in pages of 30 with "Load more".
- PK-42 the storage quota applies to picker uploads.
- PK-17 a failed trending load reads "Could not load GIFs".
- PK-35 favourites ordered by the reader's own last use.
- PK-05 each emoji in one category.
- PK-20 a focused card sends on Enter or Space.
- PK-49 the offline rule for the GIF picker.
- PK-04 a category jump leaves the header visible (*defect fix*).
- PK-46 the phone sheet remembers its tab until reload.
- Kept from the old page against main: upload-too-large wording, random folder tile previews, the search text of a library send becomes a tag.

## Timeline
- TL-19 a message that arrives while the user reads older ones is not read until they reach the bottom.
- TL-10 the newest message stays in view when the keyboard opens or the input box grows.
- TL-07 a short conversation gets a "Load earlier messages" link.
- TL-09 the restricted-history note shows only when something is hidden.
- TL-29 the viewer closes on a conversation switch and a deletion.
- TL-02 a conversation seen before paints its last known messages on a switch, then refreshes.
- TL-12 the view holds its position for changes above it.
- TL-17 the pill reads a random playful phrase, "New messages" as fallback.
- TL-04 the scroll on open waits only for the newest messages' media.

## Frame
- SH-08, SH-29 invisible people are not counted as online and are listed only as direct message partners.
- SH-20 the notice strip keeps two notices: a new version, a sign-in without a connection.
- SH-18 the connection bubble has five short texts.
- SH-13 the status items are dimmed without a connection.
- SH-24, SH-27 the room gear is always visible on touch; the sidebar scrolls.
- SH-41 the gremlin screen stays for a start failure.
- SH-12 Escape and a second tap on the chip close the status menu.
- SH-28 the people title changes only when the count changes.
- SH-34 a window at 768 px or less gets the info button; Escape closes the card.
- SH-46 the icon badge never asks for permission.
- SH-43 a direct message to yourself and an unknown room go to the lobby (*defect fix*).

## Presence
- PR-02 the chosen status is remembered across reloads and restarts.
- PR-05 invisible people are not counted and get no join or leave line; typing still shows them.
- PR-16 a dropped connection shows the person offline after 5 minutes.
- PR-17 a leave is announced after 30 seconds; a return cancels it.
- PR-18 the bot tells the admin about first joins only.
- PR-21 typing shows display names, in the order people started.
- PR-20 typing ends when the tab goes hidden or the connection drops.

## Notifications
- NT-07 an arrival in the open conversation is read at once only when visible, not away and at the bottom, and then never shows a count anywhere.
- NT-14 one sound per arrival across tabs.
- NT-24 "Sign out all other devices" removes the other devices' subscriptions; a device that turned push off stays off.
- NT-27 notifications name people by display name.
- Restored, not a choice: NT-26 a visible session suppresses a notification only while the person is online (your decision of 2026-07-31).

## Sign-in and sessions
- AU-05 closed registration keeps the form for existing users.
- AU-19 the session list is per device, the current device always listed.
- AU-20 "Sign out all other devices" is always available, also forgets the remembered networks and leaves this device's other tabs alone.
- AU-04 the bot is never the admin.
- AU-09, AD-10 smart login on by default.
- AU-21 a device signed out elsewhere goes to the login page as soon as it is connected.
- AU-22 a confirmation when unsent messages exist; sign-out removes the account's local data.
- The bot's pause message points to Visitors instead of Logs.

## Offline
- Follows `docs/offline-design-notes.md`. The one wording choice is "Needs a connection" wherever a server action is tried offline.

## Settings
- ST-04 a rejected picture says "Choose an image up to 10 MB".
- ST-02 an edit made just before leaving the page is saved; a failed save has a text.
- ST-19 the time zone is an account value detected at sign-up; the controls shift the shown time by an hour.
- ST-20 the time and date cards work with the keyboard.
- ST-18 the mute hint has a proper singular and counts down.
- ST-23 tags edited in My GIFs belong to the GIF (kept from the old page).
- ST-07 a profile change shows to other people at once.

## Administration
- AD-03 the page says when the server runs without persistence.
- AD-11 pending requests update live.
- AD-26 the connections table covers every chat connection with round trip and send-to-appear.
- AD-30 a room name has no spaces.
- AD-01 the back arrow stays on phones.
- AD-12 the preview and caching switches apply at once in every open chat.
- AD-28 the charts use the theme's colours (*defect fix*).
- The empty "Muted" column of the push audit is gone.
