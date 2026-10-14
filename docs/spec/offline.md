# Offline and installation

Yap runs in the browser and can be installed on a phone or a desktop like an app. Without a connection it stays readable and keeps what the user writes. This page decides what the installed app is, what works without a connection, and what happens to messages written offline. The design behind it is in `docs/offline-design-notes.md`; the promise here is deliberately modest. Yap is not a critical system. A message lost once in a long while is acceptable; a second of lag on every tap is not.

![Phone, offline. The grey connection ring in the header, the last messages readable, a faded pending message at the bottom.](img/offline-phone-pending.png)

## Rules

### Installation

**OF-01** Yap can be installed from the bot's welcome message ("install it"), from Settings ("Install Yap"), or from the browser's own install control. The install dialog explains how to replace an older install before it starts the browser's install or shows the add-to-home-screen guide. When Yap is already installed, the dialog says so.

**OF-02** The installed app opens full screen, with the Yap icon, name and colours. It starts on the user's last conversation, else the lobby.

**OF-03** The installed app signs in by itself on its first launch when it was installed from a signed-in browser. Afterwards it keeps its own session like any device.

**OF-04** A new version of Yap is announced in the notice strip with "Reload". Yap never reloads by itself, and a reload keeps drafts and unsent messages.

### Without a connection

**OF-05** The header shows the state: a grey ring while offline, a pulsing orange dot while reconnecting. No banner covers the content.

**OF-06** Every conversation the user has opened on this device shows its last messages and can be read and scrolled. Older messages that were not loaded say "Needs a connection" at the top of the list. Media that was seen before shows; media that was not says so in its place.

**OF-07** Typing, drafts, emoji, the emoji picker, the GIF picker's own lists, switching conversations and reading all work without a connection.

**OF-08** Sending works without a connection. Send accepts the message, shows it as pending in the conversation, and keeps it on the device. It is sent when the connection returns, also after the app was closed and reopened.

**OF-09** A pick of files is accepted without a connection up to 25 MB in total. A larger pick is refused and the input box says "Needs a connection".

**OF-10** Pending messages are sent in the order they were written, as soon as a connection exists. The user sees each one turn into a normal message. No action is needed.

**OF-11** A pending message that the server refuses shows as failed, with the reason, "Retry" and "Remove". A pending message that has waited more than one day shows as failed too. Remove deletes it from the device only.

**OF-12** Reactions, edits, deletions, status changes, GIF search and trending need a connection. Where the user tries them, the answer is "Needs a connection" and nothing changes.

**OF-13** Reconnecting is automatic: when the network returns, when the tab comes back, and when the user taps the connection dot. The newest messages are refreshed and the unread counts corrected; the view keeps its place.

### The promise and its limits

**OF-14** Offline reading shows what the device already had. It does not download conversations ahead of time beyond the ones the user opened.

**OF-15** Sending later is best effort. A message can be lost when the device loses its storage, when the account changes, or when the server has restarted without its accounts. The user is told through the failed state, never silently.

**OF-16** Signing out, or a different account signing in on the device, removes every draft, pending message and cached conversation of the previous account.

## States

| State or trigger | Input | What the user sees |
| --- | --- | --- |
| "install it" or "Install Yap" | touch, mouse | Install dialog; then the browser's install or the guide. |
| Installed app launched | touch | Full screen, last conversation, signed in. |
| New version | all | Notice strip with "Reload"; nothing changes until the tap. |
| Connection lost | all | Grey ring; everything readable. |
| Open a conversation seen before | all | Its last messages at once. |
| Open a conversation never seen | all | Header and empty list with "Needs a connection". |
| Send a text | keyboard, touch | Pending row at the end at once. |
| Files picked, under 25 MB | touch, mouse | Pending row with the files. |
| Files picked, over 25 MB | touch, mouse | The pick is refused; "Needs a connection" in the input box. |
| Reaction, edit or delete | touch, mouse | "Needs a connection" at the row. |
| Connection returns | all | Pending rows become normal rows in order; counts corrected; view unchanged. |
| Server refuses a pending message | all | "Failed", reason, "Retry", "Remove". |
| Pending for more than a day | all | Shown as failed. |
| App closed with pending messages, reopened online | touch | They are sent. |
| Sign out | touch, mouse | Device data gone; confirmation first when pending messages exist. |

## Wording

| Where | Text |
| --- | --- |
| Install dialog | see the [frame page](frame.md#SH-40) |
| Settings, install section | "Install as App"; "Install Yap to your home screen for a full-screen experience with push notifications and quick access."; "Install Yap"; "Yap is installed as an app on this device." |
| Bot welcome, phones | "📱 I noticed you're on a phone. It's a much better experience if you install it to your homescreen — it will look and feel like a normal app. Please also allow notifications when prompted so you don't miss messages. You can change this anytime in Settings." |
| New version | "You are awesome and so is the new version of this app. Please refresh."; "Reload" |
| Connection bubble | "Offline. Messages are sent when the connection returns."; "Reconnecting…" |
| Top of the list without older messages | "Needs a connection" |
| Row actions without a connection | "Needs a connection" |
| Input box, too much to queue | "Needs a connection" |
| Failed message | "Failed"; "Retry"; "Remove" |

## Related

- [Input box](input-box.md) for sending and the pending row.
- [Message row and actions](message-row.md) for the pending and failed rows.
- [Timeline and media](timeline.md) for what the list shows without a connection.
- [Header, sidebar and overlays](frame.md) for the connection state and the install dialog.
- [Notifications and unread](notifications.md) for notifications on the installed app.
- [Sign-in and sessions](sessions.md) for the installed app's session.
