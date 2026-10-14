# Yap

Yap is a small self-hosted chat for a group of friends. One server, one community, rooms and direct messages, run by one of the friends. It is built to feel instant on a phone and to stay out of the way: no accounts beyond a name, no feeds, no bots you did not invite, and a sense of humour in every text the app says. It is not a critical system. A message lost once a month is acceptable; a second of lag on every tap is not.

This page is the map. Each part links to its own page with the rules.

## People and accounts

An account is a username, chosen once, with a display name, an avatar, a short bio and a location the person can change at any time. The first account on a server is its admin. A bot account, "Ping", welcomes new people and nudges phone users to install the app.

People get in by an invite link or, when the admin allows it, by registering. Signing in is meant to be forgotten: a known device signs in with the username alone, a login link signs in a new device, and a secret code is the fallback. One account can be signed in on several devices at once, and each device can be signed out on its own. See [Sign-in and sessions](sessions.md).

## Rooms and direct messages

Every room is visible to everyone. The lobby is the default room and cannot be deleted. An admin can make a room admin-only, which makes it read-only for the others, and can limit how far back non-admins see its history. Rooms have a description and an order set by the admin.

A direct message is a private conversation between two people. It starts when one of them opens the other's name. There are no group direct messages. See [Header, sidebar and overlays](frame.md) for how rooms and people are listed.

## Messages

A message is text, or media. Text keeps its line breaks, shows links with a preview card, and shows emoji as pictures from one set so everyone sees the same thing. There is no formatting syntax and no mentions. Media is up to ten images or videos sent together, or one GIF from the user's library, from the server's library or from the GIF provider. A message can reply to another, carry reactions, be edited by its author while it has text, be deleted by its author, and be copied. See [Message row and actions](message-row.md) and [Input box](input-box.md).

Everything the user does in the input box, the pickers and the message rows answers at once. The server confirms afterwards. See [General rules](general-rules.md).

## Emoji and GIFs

The emoji picker has the user's recent emoji, custom packs from the server, and the standard categories, with search. The GIF picker has the user's recent and favourite GIFs with folders, the server's library, trending GIFs and search from the provider, and an upload. Reactions use the same emoji picker. See [Emoji and GIF pickers](pickers.md).

## The conversation

The timeline shows the newest messages at the bottom and loads older ones as the user scrolls up. It follows new messages while the user is at the bottom and holds still while the user reads older ones, with a pill that announces new messages. Images open in a full-screen viewer. See [Timeline and media](timeline.md).

## Presence

Everyone online is listed, with a status: online, away, or invisible. Away is automatic after a few minutes without input and ends with the next keystroke. Invisible people see everyone and are seen by no one. People typing in the open conversation are shown under the messages. See [Presence](presence.md).

## Notifications

Unread messages are counted per room and per direct message, with a total in the header and on the installed app's icon. A sound and the tab title announce arrivals while the user is elsewhere. Every room and every direct message can be muted; a muted conversation stays quiet in every way except a small dot. The installed app receives notifications when it is closed. See [Notifications and unread](notifications.md).

## Offline and installation

Yap can be installed on a phone like an app. Without a connection it shows what it already has, and a message written offline is kept on the device and sent when the connection returns. This is best effort, not a guarantee. See [Offline and installation](offline.md).

## Settings and administration

Settings holds the profile, the theme and text size, the time format, the notification choices, the security tools and the user's GIF library. Administration holds the people, the invites, the rooms, the sessions, the server switches and the server's GIF library. See [Settings](settings.md) and [Administration](admin.md).

## Character

Many texts the app shows come from lists the admin can edit: the room titles, the placeholder in the input box, the typing lines, the online header, the join and leave lines, the new messages pill. Each list has a plain fallback. Themes change the colours and the pattern behind the messages, never the behaviour.
