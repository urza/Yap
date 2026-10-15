# Plan of action

This page is the working plan for the rewrite. It says in what order the work is done, when a step is done, how the work survives a context reset or a new session, and how the coding principles are checked. The design is on the other pages. This page is about the process.

## Working across sessions

The work spans many contexts and sessions. Two files carry the state:

- `.claude-memory/refactor-progress.md` is the live pointer. It names the current step, the last commit, the files in progress, what remains in the step, and how to verify the step. It is updated before a step starts, at every commit inside the step, and when the step ends.
- This page is the plan of record. It changes only when the plan changes.

At the start of every session or after a context reset, the order is fixed:

1. Read `.claude-memory/MEMORY.md`, then `refactor-progress.md`.
2. Run `git log --oneline -15` and `git status`. Trust git over memory when they differ.
3. Run `dotnet build`. The tree compiles at every commit, so a failing build means an unfinished edit.
4. Read the section of this page for the current step, and the architecture page for the part in progress.
5. Continue from the first unticked item of the step.

The architecture pages are the design of record. When the code must differ from a page, the page changes in the same commit, with the reason.

## Rules for every step

- The tree compiles at every commit. It does not need to run between steps, but it compiles.
- One commit per coherent unit. The commit body gives the reason: the constraint, the measurement, the spec rule. The trailer is fixed. Nothing is pushed until the whole application works again.
- A spec rule that shapes a line of code is named in a comment at that line, by its ID.
- Code is written by the main agent. Agents on cheaper models do exploration, builds, test runs, browser checks, and mechanical moves such as renames across many files.
- At the end of a step, a review agent reads the diff of the step against the checklist below and reports. The findings are fixed before the step is marked done.

## The principles checklist

Applied at the end of every step, and kept in mind while writing.

| Check | Comes from |
| --- | --- |
| For each new class or module: is it needed, is it the right thing, is it elegant? A part with one caller and ten lines is folded into its caller. | code rule 1 |
| Will the next person understand what it does and why it is here from the file alone? | code rule 2 |
| No scoped service. All services are singletons. | sketch, Program.cs |
| No service references `HttpContext`, `ClaimsPrincipal` or `Hub`. No endpoint contains a spec rule. | auth page, layer rule |
| No request path awaits the database. Every write goes through `PersistenceWriter.Enqueue`. | architecture principle 2 |
| No HTML travels on the wire. No `innerHTML` with server data in the client. | architecture principle 6 |
| Every action in the client changes the store first, then calls the server. Every refusal shows a reason. | architecture principle 4 |
| Every hub method and every route is in the wire contract. Nothing undocumented. | wire contract |
| Comments explain why, at the line where a clean-up would break it. | CLAUDE.md |
| Prose in comments and docs is in Simplified Technical English. | CLAUDE.md |

## The steps

Each step has a definition of done. A step is done when every item is true and the review found nothing open.

### Step 1: remove Blazor

Delete `Components/`, `ChatCircuitHandler`, `CircuitTracker`, `CircuitIdentity`, `UserStateService`, `ChatNavigationState`, `EmojiService.Rendering`, the `.razor.js` files, and every Blazor line in `Program.cs` and `Yap.csproj`. Reduce `Program.cs` to the kept middleware and the kept endpoints: static files, branding, TUS, push, manifest, PWA launch, beacon. Temporarily comment out code that depended on the removed classes, each place marked with `// TODO step 2`.

Done when: the build passes with zero warnings about missing types; `dotnet run` serves `wwwroot` and the kept routes; no C# file references a Blazor type; the `TODO step 2` list is written in the progress memory.

**Done 2026-10-14.** Deviation from the first text of this step: `UserStateService` (attributes stripped) and `CircuitTracker` stay, each marked `TODO step 2`, because deleting them pulls the auth and presence work of step 2 into step 1. Comments that name `.razor` files remain in files that step 2 rewrites. The client files in `wwwroot` still mention Blazor; step 3 replaces them.

### Step 2: the server

In this order, one commit each:

1. Area folders and namespaces. Move the existing services and models into `Services/<Area>` and `Models/<Area>`. A mechanical change, delegated and then checked.
2. `AuthCookie`, `YapCookieAuthHandler`, the admin policy, the `/api` groups. The kept push and TUS routes move onto the principal.
3. `PersistenceWriter`, `Writes` and `Loads`. The account, GIF, notification, push and log services write through the writer. ChatService follows in item 5, so it is rewritten once.
4. `PresenceService`: sessions, chosen status on `User`, effective status, the sweep, typing, latency stats, the bot's permanent session. ChatService calls it for connected users and visibility.
5. ChatService changes: writes through the writer and `ChatPersistenceService` deleted, `clientId` and its dictionary, `Tail`, `Older`, the cutoff, `MarkRead` with the read-state upsert, events before writes, `OnDmStarted`, `OnRoomsChanged`, `OnPreviewsReady`.
6. The DTOs and `MessageMapper`.
7. `ChatHub`, `HubLatencyFilter`, `ChatHubBroadcaster`.
8. The routes: bootstrap, messages, sign-in, account, rooms, emoji catalogue, push additions, GIF picker and library, admin. The shell endpoint. Compression.
9. The test project `Yap.Tests`: integration tests with `WebApplicationFactory` and the .NET SignalR client, one test per path of this plan: sign in, bootstrap in both states, send and receive, open returns the tail, older with the cutoff, mark read, presence sweep timings, sign-out-others closes the other device, the writer drains on stop. Each test names its rule IDs.

Done when: the tests pass; `curl` can sign in, fetch the bootstrap, and post a message; the hub accepts a .NET client and delivers the message; the `TODO step 2` list is empty; no Entity Framework call remains outside `Services/Persistence` and the startup load.

**Done 2026-10-14** (commits aaf705f to b9fc966). Deviations from the text of this step: the shell endpoint and compression moved to step 3, because they serve `index.html`, which did not exist yet. Entity Framework is used outside `Services/Persistence` in two places that the text did not name: the startup loads in each service (allowed above), and the admin reads of the action, media and visitor logs, which are database-only features (sketch, "Admin logs"). Two defects came out of the smoke runs and the tests and were fixed in the same step: the writer's depth and the admin `skip` binding (2.8d), and the writer's double stop and the bot's async void welcome (2.9). The review against the checklist ran after b9fc966; its must-fix findings are fixed in the commit "Step 2 review: fixes" (a presence kind defect, a clientId race, rules moved from endpoints into services, dead code, stale comments, contract corrections). Accepted exception to the layer rule: `YapCookieAuthHandler` lives in `Services/Accounts` and builds the `ClaimsPrincipal`, because it is the authentication handler itself.

### Step 3: the client, chat only

In this order: the CSS flattening and the vendored libraries (done, 953864b); `index.html`, the shell endpoint and compression; `store`, `api`, `hub`, `router`, `render/dom`; the other `render/*`; `ui/frame`; `ui/timeline` and `ui/message`; `ui/composer`; `ui/overlays`; `ui/emoji-picker`; `ui/gif-picker`; `ui/gallery`; `pages/chat`; a minimal `pages/login` with the username box only, enough to sign in. The service worker keeps today's push and media code.

Done when: a person signs in, reads rooms and direct messages, sends text, images, videos and GIFs, reacts, edits, deletes, replies, sees presence, typing and unread counts, and the Playwright screenshots of the frame, the timeline, the message row, the input box and the pickers match the spec's screenshots in the states the spec names.

**Done 2026-10-14** (commits 953864b to 44b56dd, plus f42a17a). Three Playwright rounds in Chromium (two people and a phone-sized window) cover sign-in, the frame, presence, send and receive, grouping, reactions, edit, delete, replies, typing, unread and the mailbox, the status menu, both pickers, link cards, images and the gallery viewer, emoji sizes, pack emoji, older pages, the pill, Shift+Enter, a deep-link reload, the phone sheet, sign-out and the back button. The screenshots were compared with the spec's by eye: the frame, the rows, the pill and the phone sheet match; step 6 regenerates the spec images with the script. Deviations: the notification prompt and the install dialog (SH-38, SH-40) and the push helpers wait for step 5 with the push device routes; the browser scripts live outside the repo until step 6 moves them into `tests/browser/`. GIF sends from the picker and video rows are implemented but not yet exercised in a run (no provider key, no ffmpeg in the sandbox).

### Step 4: offline

`offline/outbox`, `offline/sender`, `offline/snapshot`; the service worker's shell cache with the build stamp, the emoji cache group, the new-version strip; the connection dot and bubble.

Done when: a reload without a network shows the last tail; a message sent without a network arrives after the network returns; a repeated POST returns the same message; a new build shows the strip and never reloads by itself.

**Done 2026-10-14** (commit 74efaab). All four conditions verified in Chromium with the service worker's network events enabled. Deviations: the sender posts from any tab that queued a row and the lock holder posts the rest, instead of one tab only; the server's clientId dedupe makes the race harmless. Background Sync is not used. The old `pending-message` ghost rules left app.css.

### Step 5: the pages

`pages/login`, `verify` and `invite` in full; `pages/settings` with the account routes; `pages/channel-settings`; `pages/admin` with its API and the diagnostics tables; the notification settings and push device routes.

Done when: every rule on the Settings, Administration and Sign-in pages of the spec has its control, and the Playwright screenshots of those pages match.

**Done 2026-10-15** (commit 66ba604). The pages run of the browser scripts covers the login rules, Settings (themes, text size, profile, link, code, sessions, mutes, time and date), the verification and link pages, the admin's users, invites, registration with approval, bot, debug, logs, media, GIFs, diagnostics with charts, visitors, and Create Channel and Channel Settings; 14 steps pass. The Settings and Admin screenshots match the spec's by eye. Deviations: the kicked notice after "Sign out all other devices" shows in the page at once instead of after a reload with a query; the "Separators" row shows only when the server lists two cultures; the add-to-home-screen guide loads its library from the CDN when the browser offers no install prompt; the notification prompt and the push subscription run only in an installed app and were not exercised in the sandbox. The review of steps 3 and 4 is applied in the same commit; its remaining nits (PascalCase hub method names in the client, the fold candidates, the 409 note) are listed in the progress memory for step 6.

### Step 6: finish

Regenerate every spec screenshot with the Playwright script. Walk every spec page and tick its rules. Update the architecture pages where the code differs. Remove dead code, the `Yap.csproj.user` leftovers and the Blazor remarks in `css_architecture.md` and the README. Final review against the checklist. Push.

**Done 2026-10-15** (commits 509d467 to 7e64975). Two agents walked every spec page against the running client; about 40 findings were fixed, and the differences that stay are the second table of `sketch.md`. The 24 images in `docs/spec/img` come from `tests/browser/screenshots.mjs`, run against the new client; four visual defects it exposed were fixed. The architecture pages, `css_architecture.md`, the README and CONFIG_README describe the code as it is. The final review found two lost features (the admin order by last activity, the Installed badge), two config errors and a list of members without callers; all are fixed or removed. The 15 unit tests and the four browser suites (smoke 17, offline 10, features 9, pages 14 steps) pass on the final tree.

## What a step looks like from inside

1. Re-read the architecture page for the part.
2. Write the list of files to create and change into the progress memory, with the verification command.
3. Write the code. Build after every file that compiles on its own.
4. Ask an agent to build and run the tests, or to drive the browser, and to report pass or fail with the relevant output only.
5. Run the review against the checklist.
6. Commit. Update the progress memory: last commit, what remains.
7. When the step is done, tick it here and move the pointer.

## Risks and the answer to each

| Risk | Answer |
| --- | --- |
| A context reset in the middle of a step | The progress memory names the files in progress and the next action before the step starts. Git shows the partial edits. |
| The code drifts from the design | The page changes in the same commit as the code. The review compares both. |
| A step grows past its definition of done | The definition is written first. Extra work goes into a note for a later step, not into the current one. |
| The tree is broken for a long time | It compiles at every commit from step 1 on. From step 2 on, the tests run at every commit. |
| The look drifts from the spec | Screenshots from Playwright for .NET against the spec's images, from step 3 on. |
| Principles fade under volume | The checklist review at the end of every step, by an agent that did not write the code. |
