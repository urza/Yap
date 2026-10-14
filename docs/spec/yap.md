# Yap

Yap is a small self-hosted chat for a group of friends. One server, one community, rooms and direct messages, run by one of the friends. It is built to feel instant on a phone and to stay out of the way, with a sense of humour in every text the app says. It is not a critical system.

This page is the map. Each part links to its own page with the rules. The [general rules](general-rules.md) hold for every part.

## People and accounts

An account is a username, chosen once, with a display name, an avatar, a short bio and a location the person can change at any time. The first human account on a server is its admin. A bot account, "Ping" by default, welcomes new people and nudges phone users to install the app. Its username, display name and welcome text are set per server; the display name and the text can be changed on the administration page.

People get in by an invite link or by registering, unless the admin closed registration. Signing in is meant to be forgotten: a device stays signed in, a device on a known network signs in with the username alone, a login link signs in any other device, and a secret code is the fallback. One account can be signed in on several devices at once, and the other devices can be signed out from any one of them. See [Sign-in and sessions](sessions.md).

## Rooms and direct messages

Every room is visible to everyone. The lobby is the default room and cannot be deleted. An admin can make a room admin-only, which makes it read-only for the others, and can limit how far back non-admins see its history. Rooms have a description and an order set by the admin.

A direct message is a private conversation between two people. It starts when one of them taps the other's name in the sidebar. There are no group direct messages. See [Header, sidebar and overlays](frame.md) for how rooms and people are listed.

## Messages

A message is text, or media. Text keeps its line breaks, shows links with a link card, and shows emoji as pictures from one set so everyone sees the same thing. There is no formatting syntax and no mentions. Media is up to ten images or videos sent together, or one GIF from the user's library, from the server's library or from the GIF provider. A message can reply to another, carry reactions, be edited by its author while it has text, be deleted by its author, and be copied. See [Message row and actions](message-row.md) and [Input box](input-box.md).

Everything the user does in the input box, the pickers and the message rows answers at once, except what must come from the network. The server confirms afterwards. See [General rules](general-rules.md).

## Emoji and GIFs

The emoji picker has the user's recent emoji, custom packs, and the standard categories, with search. The GIF picker has the user's recent and favourite GIFs with folders, the server's library, trending GIFs and search from the provider, and an upload. Reactions use the same emoji picker. See [Emoji and GIF pickers](pickers.md).

## The conversation

The timeline shows the newest messages at the bottom and loads older ones as the user scrolls up. It follows new messages while the user is at the bottom and holds still while the user reads older ones, with a pill that announces new messages. Images open in the gallery viewer. See [Timeline and media](timeline.md).

## Presence

Everyone online is listed, with a status: online or away. Away can be chosen, or comes by itself after a few minutes without input and ends with the next input. Invisible people see everyone and appear offline to everyone except the admin. People typing in the open conversation are shown under the messages. See [Presence](presence.md).

## Notifications

Unread messages are counted per room and per direct message, with the mailbox total in the header and on the installed app's icon. The tab title, and a sound for direct messages, announce arrivals while the user is elsewhere. Every room and every direct message can be muted; a muted conversation stays quiet in every way except a small dot. Any browser that supports it, and the installed app, can receive notifications while Yap is closed. See [Notifications and unread](notifications.md).

## Offline and installation

Yap can be installed on a phone like an app. Without a connection it shows what it already has, and a message written offline is kept on the device and sent when the connection returns. This is best effort, not a guarantee. See [Offline and installation](offline.md).

## Settings and administration

Settings holds the profile, the theme and text size, Time & Date, the notification choices, the login link, secret code and devices, and My GIFs. Administration holds the people, the invites, the sessions, the server switches, the logs and the server's GIF library. Rooms are created and changed on their own pages by the admin. See [Settings](settings.md) and [Administration](admin.md).

## Character

Many texts the app shows come from lists in the server's configuration: the room headers, the placeholder in the input box, the typing texts, the people section title, the join and leave lines, the new messages pill. Each list has a plain fallback. Themes change the colours and the pattern behind the messages, never the behaviour.

## How to read the pages

Every page has the same shape: purpose, anatomy with a screenshot, numbered rules, a states table, every text the user can read, and links. A rule is a few short sentences about one thing, in the present tense, with a stable ID such as IB-22. When two pages touch the same thing, the component page says how it looks and reacts, and the feature page says when and why. When this page and a detail page disagree, the detail page wins.
