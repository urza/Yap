# Yap, direct messages only (branch `dmonly`)

This branch is an experiment, started 2026-10-17. It removes rooms from Yap. Every person talks to the admin
and to nobody else. The admin talks to everyone. The rest of the app is unchanged: the input box, emoji, GIFs,
uploads, reactions, replies, settings, themes, push, presence, the offline outbox.

The branch does not need to be compatible with `main` or with a deployment of `main`. It is deployed on its own.

## What a person sees

- Sign-in is unchanged (the login page, the invite links, the verify page).
- After sign-in the person lands in the conversation with the admin. The URL is `/dm/{admin}`. The home `/`
  opens it.
- The header shows the admin's avatar and name. There is no back arrow: there is no other page to go back to.
- The status chip and its menu are unchanged: status, Settings, notification settings, sign out. Settings has a
  back arrow to the conversation.
- The person's directory holds three accounts: the admin, the bot and the person. The bootstrap sends nothing
  else, and presence and profile events about other people never reach the person.

## What the admin sees

- The home `/` is the people page: every account on the server, one row each, inside the frame. A row shows the
  avatar, the display name, the presence dot and word, the admin and bot badges, the time of the last message,
  the unread badge or the muted dot, and the crossed bell when the conversation is muted. A search field
  filters the rows by name.
- The order: conversations with unread messages first, then people online, then everyone else, the bot last.
  Inside a group the newest conversation comes first, then the name.
- A tap on a row opens the conversation with that person at `/dm/{username}`. The header has a back arrow to
  the people page. The mailbox button in the header opens the people page too.
- The Admin page is unchanged, except the Debug generator: it writes test messages into the admin's conversation
  with one chosen person, and the two of them are the speakers.

## The bot

The bot is a user account. The admin sees it in the people page and can open its conversation; the bot's notices
to the admin (a pending registration, a join, an untrusted proxy, the access-link brake) arrive there as before.

A person has one conversation, so the bot writes to a person inside the person's conversation with the admin:
the welcome message, the new-device notice, the replaced-link notice. The bot is not one of the two participants
there; it sends as a system sender (`SendMessage.System`), and the message counts as unread for both the person
and the admin. The admin reads those notices too, the welcome message's login link included. The admin owns the
server and can create a new link for any person at any time, so this shows the admin nothing the admin could not
get already. It is still a trade-off; it is recorded here.

## Server rules

- `ChatService.GetOrCreateDm` refuses a pair where neither side is the admin. That one rule covers the hub
  (`openDm`), the send route `/api/dm/{username}/messages`, the admin generator and the bot. The route
  `/api/channels/{id}/messages` keeps its participant check.
- The hub has no groups. Every event of a conversation goes to its two people by user id.
- Presence and profile events go to everyone when they are about the admin or the bot, and to the person and
  the admin when they are about anyone else (`ChatHubBroadcaster.ToWatchersOf`). Presence changes travel as the
  `presences` lists of a 250 ms tick, cut the same way (`SendPresences`): the admin gets every person's changes
  in one list.
- No room is seeded. Rooms in a database from the room build are not loaded; their messages and read states are
  skipped. The `Channel` entity keeps its room columns so the schema and the migrations are unchanged.
- The history cutoff (`HistoryLimit`, `SinceJoined`), the write permission, the room events (`rooms`), the room
  push notification and the lobby join and leave lines are gone.
- The push notification for a message opens the conversation the message is in: for a person that is the
  admin's, whoever wrote it.

## Deployment

The branch publishes its own package, `ghcr.io/urza/yap-dmonly`, with its own `latest` tag and a short commit
hash tag. The main package, `ghcr.io/urza/yap`, is untouched: a push to either branch never moves the other's
`latest`. The workflow file on the branch triggers on a push to `dmonly` only (and by hand). It has no `v*`
trigger, so a release tag made on main cannot build with it. GitHub runs the workflow file of the pushed branch,
so the file on main needs no change.

The first push creates the package. A new package can start as private: set the visibility in the package
settings on GitHub before a server pulls it, else the pull fails with `denied`. The volumes and the environment
are the same as for the main image; only the image name changes:

```bash
docker run -d --name yap-dmonly -p 5222:8080 -v ./uploads:/app/wwwroot/uploads -v ./data:/app/Data ghcr.io/urza/yap-dmonly:latest
```

Use separate `uploads` and `data` folders for the two builds. The schema is shared, but the dmonly build skips
rooms at load and would leave a main database with its rooms untouched yet invisible; two apps on one SQLite
file is not a supported setup in either build.

## Decisions taken without the user (the user was asleep)

1. **Branch name.** The request named the branch `dmonly` first and `onlydm` at the end. The branch is `dmonly`.
2. **The bot's messages to a person** go into the person's conversation with the admin, as a system sender.
   The alternatives were a second conversation for the person (against the brief: one conversation) or no
   bot messages to people (loses the login link and the new-device notice). See "The bot" for the trade-off.
3. **The admin's home is a full page, not a side panel.** The brief says no side panel and one screen with the
   users. A persistent two-column layout on wide screens is possible later; it is not built.
4. **The schema is unchanged.** The room columns stay on `Channel`, unused. A migration that drops them is
   work with no benefit for an experiment.
5. **Notification settings keep the server-side room mode.** The Settings page shows the server mute and the
   direct-message mode only; the room row is removed. `NotifRoomMode` stays on the user as a stored column
   that nothing reads.
6. **The texts.** `RoomHeaders`, `OnlineUsersHeader` and the join and leave lines are no longer read from
   `appsettings.json`. The keys may stay in a config file; they do nothing.
7. **The directory is cut for a person** (admin, bot, self). The brief says people do not have other people;
   sending the full user list to every person would contradict that.
8. **A person's presence** is sent to the admin and to the person. The bot and the admin stay visible to all.
9. **The refusal screen.** When the server refuses the admin's conversation to a person (the admin account is
   gone), the person sees the error screen with a retry, not a loop between `/` and `/dm/{admin}`.

## Not adapted on this branch

- `tests/browser/*.mjs` (the Playwright suites) sign in and wait for `/lobby`, tap room rows and open the
  channel pages. They need a pass for this build. `Yap.Tests` is adapted and passes.
- `loadtest/` (a separate solution) opens the lobby with `OpenRoom`. It does not run against this build.
- `docs/spec/*` and `docs/architecture/*` describe the room build. This file lists the differences.
- Main's windowed sidebar list (2026-10-18, "rows for the people in view, padding for the rest") is not ported
  to the people page. The page rebuilds its rows at most four times a second and skips a rebuild when nothing a
  row shows has changed; at a few thousand accounts the rebuild itself would still cost, and a windowed list is
  the next step then.

## Files

Server: `Models/Chat/Channel.cs`, `Conversation.cs`, `SendMessage.cs`, `Services/Chat/ChatService.cs`,
`SystemBotService.cs`, `Endpoints/ChatHub.cs`, `ChatHubBroadcaster.cs`, `BootstrapEndpoints.cs`,
`ShellEndpoints.cs`, `AdminEndpoints.cs`, `Configuration/ServerConfig.cs`,
`Services/Notifications/PushNotificationService.cs`. Removed: `Endpoints/RoomsEndpoints.cs`.

Client: `js/router.js`, `main.js`, `store.js`, `pages/people.js` (new), `pages/chat.js`, `ui/frame.js`, and
small cuts in `hub.js`, `session.js`, `notify.js`, `offline/snapshot.js`, `ui/timeline.js`, `ui/composer.js`,
`pages/settings.js`, `pages/admin.js`, `pages/invite.js`, `service-worker.js`, `index.html`, `app.css`,
`css/frame.css`, `css/pages.css`, `css/composer.css`. Removed: `pages/channel-settings.js`.
