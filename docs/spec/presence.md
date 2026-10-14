# Presence

Presence is what Yap tells people about each other's current state: who is online, whether they are away or invisible, when they come and go, and who is typing in the open conversation. It is shown in the sidebar's people list, in the header's status chip and count, in the profile card, in the typing strip above the input box, and in the lobby's join and leave lines. The rules here decide what those places show.

![People list, desktop. A green dot for an online person, orange for an away person, grey for an invisible or offline one; the admin crown; the bot; the user's own row with "(you)".](img/presence-desktop-list.png)

## Rules

### Status

**PR-01** An account has one status: online, away or invisible. "Offline" is not a status; it means the person has no connection. The status belongs to the account, not to a device: a pick on one device applies everywhere at once.

**PR-02** The user chooses the status in the status menu. A new account is online. The chosen status is remembered until the user changes it, also across a reload, a new app start and a server restart.

**PR-03** Colours are fixed: green for online, orange for away, grey for invisible and offline. Themes never change them.

**PR-04** Away has two sources: chosen, or automatic. Others see the same orange for both. An automatic away keeps the chosen status underneath: the chip shows orange, the menu still marks "Online". A chosen away ends only with a new pick.

**PR-05** Invisible people appear offline to everyone else: a grey dot, "Offline" on the profile card, a place among the offline people, no join or leave line, no line when they turn invisible or back, and no count. They keep full use of the chat and see everyone else's real status; their own chip, row and card show them as grey and "Offline". Admins see "Invisible" on the administration page. An invisible person who types in a conversation is shown as typing there.

### Who is online

**PR-06** A person is online while at least one of their devices has Yap open and connected. All tabs and devices of one person make one entry in the list and count once.

**PR-07** The online count is the number of people online or away, the bot included. Invisible people are not counted, also not by themselves. Everyone else counts themself.

**PR-08** Changes reach everyone at once: a person who joins, leaves or changes status appears, disappears or changes colour in every open list within a second.

**PR-09** The bot is always online.

### Automatic away

**PR-10** A person becomes away when none of their devices has had any input for 5 minutes. Input is a key, a tap, a pointer move, a wheel movement, or a return to the tab. Incoming messages are not input. The change shows to others within 10 seconds of the 5 minutes, or up to a minute later when every tab of the person is hidden.

**PR-11** A hidden tab does not make a person away by itself; the 5 minutes count. Watching a video for 5 minutes without input shows the person as away. Both are accepted.

**PR-12** Automatic away applies only to a person whose chosen status is online. A chosen away or invisible never changes by itself.

**PR-13** Any input on any device ends an automatic away within 10 seconds. Returning to the tab, or a pick in the status menu, ends it at once.

### Leaving

**PR-14** Closing the last tab or signing out of the last device shows the person offline at once: they leave the list, or turn grey when they share a direct message with the viewer. "Sign out all other devices" does the same for the other devices.

**PR-15** When a connection drops without the tab closing (network loss, phone sleep, a closed laptop), the person shows as away after 30 seconds, unless another of their devices is visible, with or without input. A visible device blocks the change so that a locked phone does not turn an attended desktop orange.

**PR-16** A person whose every connection has been gone for 5 minutes is shown offline: they leave the list, or turn grey as a direct message partner. A connection that returns before that keeps the chosen status and announces nothing.

**PR-17** A join is a person's first connection; a leave is the moment they are shown offline: 30 seconds after their last tab closed or signed out, or 5 minutes after their last connection dropped. A return before that cancels it, so a reload shows nothing. The lobby shows a join or leave line to the people who have the lobby open at that moment. Line texts are on the [message row page](message-row.md#MR-73).

**PR-18** The bot tells the admin in a direct message when a new account joins for the first time: "👋 {display name} just joined the chat!".

### Typing

**PR-19** "X is typing" is shown only in the typing strip of the open conversation, to the people who have that conversation open. It never appears in the sidebar, the header, the tab title or a notification. In a direct message only the other person sees it.

**PR-20** The indicator appears within about a second of the first keystroke and disappears 3 seconds after the last one, at once on send, when the draft becomes blank, when the typist leaves the conversation, and when the typist's tab goes hidden. A dropped connection ends it like a last keystroke.

**PR-21** Several people typing are shown by one of the configured texts for two, or for three or more, with display names in the order they started. The viewer is left out first, and the text is chosen for the people who remain. The text changes only when the set of typists changes.

**PR-22** Typing is shown in a read-only room too, since people with permission can write there. Editing a message does not count as typing.

### What presence is not

**PR-23** No last-seen time is shown to other people. An offline person's card says "Offline". Admins see last-action times on the administration page.

**PR-24** Status changes nothing about delivery, sending, reactions or access. Away has no automatic reply. A visible session of a person who is online suppresses notifications to their other devices; away, chosen or automatic, and invisible do not, so an idle desktop never eats the phone's buzz. Away also stops the automatic read in the open conversation. See [Notifications and unread](notifications.md).

**PR-25** A person's device type is shown only to themself in Settings and to admins, never to other people.

## States

| State or trigger | Input | What the user sees |
| --- | --- | --- |
| Status pick | mouse, touch | Chip and own dot change at once on every device; the menu marks the pick. |
| 5 min without input on every device | all | Others see orange; the own chip turns orange; the menu still marks Online. |
| Any input while automatically away | keyboard, mouse, touch | Green again within a few seconds; at once on tab return. |
| Chosen Away, input | all | Stays orange. |
| Invisible | all | Grey to others and to themselves, "Offline" on the card, not counted. |
| Second device joins | all | Same status everywhere; the count does not change. |
| Last tab closed or sign out | all | Gone from the list, or grey as a direct message partner; leave line after 30 s unless back. |
| Connection drops, tab open | all | Orange after 30 s unless another device is visible; offline and the leave line after 5 min. |
| Connection returns within 5 min | all | Chosen status again; no join line. |
| First keystroke in a conversation | keyboard, touch | Others in it see the typing text within a second. |
| 3 s without typing, send, empty draft, leave, hidden tab | all | Typing text gone. |
| Two or more typing | all | The viewer is left out first; the text for the people who remain. |
| New account's first join | all | Join line in the lobby; bot message to the admin. |

## Wording

| Where | Text |
| --- | --- |
| Status menu | "Online"; "Away"; "Invisible" |
| Profile card status | "Online"; "Away"; "Offline" |
| People section title (configured; defaults) | "the gang ({0})", "squad check ({0})", "main characters ({0})", "{0} legends present", "chat fam ({0})"; fallback "Online Users ({0})" |
| Typing, direct message | "{name} is typing..." |
| Typing, room (configured lists for one, two, three or more) | see the [input box page](input-box.md#wording) |
| Bot to the admin | "👋 {display name} just joined the chat!" |
| Settings, sessions | "Active Sessions"; "This device"; "Active {time}" with "just now", "{n}s ago", "{n}m ago", "{n}h ago", "{n}d ago" |
| Administration, status pills | "Online"; "Away"; "Invisible"; "Offline" |

## Related

- [Header, sidebar and overlays](frame.md) for the chip, the menu, the count and the people list.
- [Input box](input-box.md) for the typing strip and what starts typing.
- [Message row and actions](message-row.md) for the lobby join and leave lines.
- [Notifications and unread](notifications.md) for what a visible session suppresses.
- [Settings](settings.md) for the list of the user's own sessions.
- [Administration](admin.md) for the sessions table admins see.
