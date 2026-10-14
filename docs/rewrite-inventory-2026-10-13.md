
# Rewrite Inventory

Published copy: https://claude.ai/artifact/Q5NF7gronam8ZSGVPB78g7

Yap · PR #1 "offline chat" · read against HEAD 838a73c6 on 2026-10-13

Every design decision and mechanism the chat rewrite left in the code base, by layer. Not what it does for the user, but what its maintainer inherits. Each item is tagged.

- **LB** load-bearing: needed for correctness
- **Q** questionable: works, but costly or fragile
- **I** incidental: complexity with no clear purpose

Sources. Section 2 is from the code I moved and read today. Sections 3 and 4 are from two read-only audits run tonight against HEAD, files unchanged by the parallel session; line numbers are theirs. Section 5 was not re-audited tonight because the parallel session is rewriting that layer; it repeats the review notes from 2026-10-10 and says so. Nothing in docs/ was used as a source.

## How this was missed

The PR review ran nine rounds and found the blockers: the snapshot fan-out that rebuilt every user's full state under one static lock, the login-link host poisoning, the emoji render race, the no-database mode. It checked what could break. It did not write down the shape of what was being accepted.

The 100 ms presence ticker arrived inside the fan-out fix I had asked for. I verified it on load numbers (50 clients, flat CPU) and accepted it. The review notes list "non-obvious decisions Astra made", and this one is not on the list. The same goes for most of section 2 below. That is the gap this page closes.

## 1. The pattern

The old server is event-driven. ChatService changes state, raises an event, and the subscriber reacts. The rewrite repeats one other pattern on both sides of the wire: accept that an update can be lost, then poll to repair. The picture below shows every clock that runs while nobody does anything.

> Diagram: **The clock map.** Everything on it runs with no user action. The arrows are the loops that cross the network.
>
> Alt text: Every periodic loop in the chat path. In each browser tab: a 500 ms presence tick, a 3 s outbox poll, a 3 s read flush, a 30 s session poll, a 60 s timestamp refresh, a 3 s reconnect with no backoff, and a manifest fetch on every tab visibility change. On the server: a 100 ms presence ticker, a 10 s digest per hub connection, and a 1 min receipt cleanup.
>
> Labels: One browser tab (each tab repeats this) | 500 ms live.js: typing expiry, Report timer | 3 s sender.js: poll the outbox, forever | 3 s reader.js: flush read checkpoints | 3 s app.js: reconnect, no backoff | 30 s app.js: GET /session | 60 s app.js: re-format every timestamp | visible worker-updates: GET manifest.json | 10 s windows.js: retry unloaded windows | Each tab also holds its own hub connection, its own WatchChanges stream, and commits every packet to IndexedDB. Sibling tabs re-merge it. | N tabs = N connections, N commits, N(N-1) merges | Report every 10 s visible, 60 s hidden | GET /session: renews cookie, records IP, mints CSRF + presence tickets | about 20 KB per tab visibility change | presence views on change (after a tick) | ChatUpdate packets, or a Reset after overflow | Server | 100 ms PresenceTicker: rebuild every tab's view | 10 s ChatHub digest, per connection: walk all channels, re-auth cookie | 1 min ChatReceiptCleanup: prune receipts | on overflow queue cleared, full Reset sent | Messages are event-driven: ChatChangeTracker raises, ChatFanout projects once, queues per connection. Presence is not. Repair is periodic. | Two patterns in one server.

## 2. Server pipeline

Endpoints/Chat\*.cs, Hubs/ChatHub.cs, Services/ChatProjection.cs, ChatFanout.cs, ChatChangeTracker.cs, PresenceService.cs, PresenceTicker.cs. Verified today.

### Loops and repair

- **Q** **Presence by polling at 100 ms.** `PresenceTicker` rebuilds the people list, the typing lists and one view per connected tab, then diffs. ChatService raises events for exactly these changes; the rewrite ignores them and reads state instead.
- **Q** **A digest per hub connection every 10 s.** `ChatHub.WatchChanges` walks every channel the user may read, compares version counters, clears expired mutes and re-authenticates the cookie. The README called it "ten-second digests repair dropped revisions". Dropping is designed in; the digest is the repair. The loop allocates a `Task.Delay` per iteration per connection.
- **Q** **Overflow means start over.** Each connection has a queue of 128 updates. When it fills, the queue is cleared and the client receives a full Reset of every conversation.
- **Receipt cleanup every minute.** **LB** for retries, **Q** for its query: in SQLite it sorts the whole receipts table on every run.

### Locks and state

- **Q** **Three locking schemes in one family.** ChatService has `_connectionGate`. The change tracker owns the per-channel locks that every message mutation takes, and ChatService borrows them through `GetChannelLock`. `ChatService.Acceptance` keeps a dictionary of reference-counted semaphores keyed by operation id, message id and DM pair.
- **Q** **A global semaphore around a copy of per-tab state.** `PresenceService` keeps `Channel` and `TypingUntil` per tab behind one `SemaphoreSlim` for the whole server, to keep the copy in step with ChatService. Every awaited call under it completes synchronously. Covered in detail on the Presence Lock page.
- **Q** **Hub sessions are retained 4 h after disconnect**, the same as Blazor circuits, although nothing on the hub can resume. `PresenceOptions` defaults.

### Work that grows with history

- **Q** **A finished link preview rescans everything.** On a Media change the fan-out reads every message of every room and every DM to find the ones with that URL. This runs after every preview fetch and every media download. A settings change touches every channel, so every client invalidates every window.
- **Q** **Reading schedules downloads.** `ChatProjection.Message` calls `GetPreview`, which queues a network fetch when the cache is cold or past its TTL. Building a window for a client starts I/O.
- **LB** **Profile edits invalidate every channel the user ever posted in**, via an author index built at startup. Correct, and O(channels) per profile save.

### Residue

- **I** An ignored `knownState` hub argument and a `StateRevision` field the server never sets. Both are on the wire, so they stayed.
- **I** The hub's `EnsureProtocol` can never fail once the inline middleware passed, because both read the same handshake query.
- **I** The `validatedView` branch for "unversioned hub clients" that only existed during the PR's own development.

## 3. Client

wwwroot/chat-client: 7,666 lines of JS in 33 files, 3,962 of CSS, two service workers. Audit read against HEAD.

### What it is

It is not a chat page. It is an offline-first replicated client: a sequence-stamped merge per record, an IndexedDB shared across tabs behind one global Web Lock, a durable outbox with idempotent operation ids, shell releases installed all-or-nothing with every file hash-checked, and a media cache per user. Most of that is load-bearing *given those goals*. A rewrite that keeps offline mode and the outbox rebuilds most of it.

### Decisions you inherit

- **LB** **Secure context only.** It needs `navigator.locks`, `crypto.randomUUID` and service workers. On a plain-HTTP LAN address the chat does not start; the user sees "Offline chat has not been set up or is locked".
- **Q** **The cached chat is not shown first.** It is painted only after the network bootstrap succeeds or fails, up to 10 s on a bad network. The stated reason is to confirm the cached data belongs to the cookie's account before showing it. That is a privacy choice that costs the speed offline-first is meant to give.
- **Q** **Every tab is a full client.** Each tab holds its own hub connection and its own `WatchChanges` stream. Each packet is merged twice in the receiving tab (IndexedDB copy, then memory) and once more in every sibling tab via BroadcastChannel. N tabs cost N commits and N(N−1) merges per packet.
- **Q** **Every draft keystroke is a locked IndexedDB transaction.** Every render also writes `last-route`.
- **LB** **One tab sends for all tabs**, chosen by a Web Lock; at most 3 conversations in flight, one upload at a time; batches of up to 16 operations or 48 KB.
- **LB** **Retry policy is baked into the client.** Backoff `min(60 s, 3 s·2^(n−1))` with jitter, 8 attempts, persisted next-attempt time. Any 4xx except 408, 429 and `csrf` fails for good. The server must keep idempotent receipts for the window a retry can span: 1 day or 4,096 per user.
- **Q** **A message that failed for good cannot be removed.** Likely bug. Failed rows offer only Retry; a delete on a failed item queues a server delete of a message that does not exist, which the server accepts, and the row stays.
- **LB** **Message id equals the client operation id.** Edits of a queued-but-attempted item are queued with `messageId = operationId`, and the server maps it back through the receipt.
- **Q** **A server restart invalidates every cached window.** The `ServerEpoch` is per process. After every deploy, every client refetches the window of every conversation, one at a time.
- **Q** **Reconnect is a full bootstrap every 2.5 to 3.5 s with no backoff.** SignalR's automatic reconnect is not used.
- **Q** **GIF search results are cached in IndexedDB without limit**, one entry per search term and cursor.
- **Q** **Status travels as an index into `['online','away','invisible']`**, which must match the C# enum order.
- **Q** **Errors are signalled four ways**: message-string sentinels, `ApiError` with status, `{code}` objects, and substring matching on hub error text. Connection state is derived by regex over the English status text in the DOM.
- **Q** **401 and 409 are handled differently for GET and POST.** A GET 409 becomes "Server unavailable (409)" and is caught only by the 30 s session poll.

### Service workers and releases

- **Q** **Two workers.** The new one is an ES module so it can import `constants.js`; the old classic file became a bridge that copies the whole shell into a temporary cache and hands over. Module workers rule out offline mode on Firefox before 147 and Safari 14. A classic worker with `importScripts` would have needed neither the bridge nor the probe.
- **I** **The module-worker probe** passes an object with a `type` getter and an invalid URL to `register()` and `new Worker()` so nothing starts, and reads whether the getter was touched.
- **Every release re-downloads about 170 files, about 1.9 MB**, hash-checked, all or nothing. **LB** for integrity, **Q** for cost: any byte change in any shell file invalidates the whole shell.
- **Q** **The manifest is hashed once at startup, including Data/branding.** Editing branding without a restart, which Program.cs advertises as supported, makes every install fail with "Shell asset changed", silently keeping clients on the old version. Several instances with different files fail the same way.
- **Q** **Updates activate immediately and old tabs keep running old JS** while lazy requests are served from the new cache. The "Yap was updated, reload" notice is the only signal.
- **Q** **The media cache evicts first-in-first-out**, 32 MB per user, 4 MB per file, and the worker opens IndexedDB on every media request.
- **Q** **Custom emoji and emoji packs are cache-first** and stay stale until the next release.
- **Auth routes are intercepted by the worker**: sign-out wipes all local data, the others lock the account; a different user on bootstrap erases the previous account's unsent outbox. **LB** for privacy, lossy by design.

### Quality signals

- The element-building helper is copied in 8 files; the SHA-256 snippet 3 times; media path regexes 4 times in slightly different forms; the history cap in two files; the route-to-conversation logic written three ways; the "same account" guard repeated 35 times.
- Dead: `SET_BADGE` handlers with no sender, a write-only session key, `manifest.webmanifest` unused but precached, `contracts.js` downloaded but never run, double `resume()` and double `establish()` at startup.
- Longest functions: the GIF picker about 485 lines, the actions module about 430, `mergeUpdate` 152.
- Per render: two JSON signatures per visible message, a `structuredClone` of the message list, two outbox reads and one reads read from IndexedDB, a new `Intl.Segmenter` per text token.
- The Blazor header, sidebar, profile card and push prompt still exist for the Settings layout and duplicate `app.js`, `profiles.js` and `pwa.js` in a second technology. The 332 lines left in the old `chat.js` duplicate push, install and badge logic that also lives in `pwa.js` and `push.js`.

## 4. Auth, HTTP, proxy

Program.cs, Middleware/, Endpoints/, Hubs/, Services/AuthCookieRenewal.cs, UserService.cs, AccessLinkService.cs, TusEndpoints.cs. Audit read against HEAD.

The core choices are sound: the cookie is checked on every request and hub call, write protection is bound to the signed-in account, each user has their own login-link host, and uploads are tied to the account that made them. The weak points are around them.

### Identity

- **LB** **The cookie is the only credential and no ASP.NET authentication is registered.** No `AddAuthentication`, no `[Authorize]`. Tokens are an in-memory lookup with no server-side expiry.
- **Q** **30 `AuthenticateByToken` call sites.** The chat API filter authenticates, then 15 handlers look the user up again with `!`. A bootstrap does 4 lookups, a send 3, a signed-in GET / 3. If the token rotates between filter and handler ("sign out other devices"), a POST gets a misleading 503 "safe to retry".
- **A fabricated `ClaimsPrincipal` with authentication type "YapCookie".** **LB** for binding antiforgery tokens to the account, **I** as an identity: nothing else reads it, and the hub, Tus, Push and Blazor see an anonymous user.
- **Q** **The two-minute live ticket protects the wrong stream.** It is checked only in `Join`, for the presence stream. `WatchChanges`, the private message stream, never asks for it. It is not single-use and not tied to a connection.
- **Q** **Cookie renewal moved into the chat API.** The PR removed renewal on HTML page loads. Blazor pages now renew by loading `session-renewal.js`, which calls `/api/chat/session` hourly. Cookie lifetime on Settings and Admin depends on the chat API and its protocol gate.
- **Q** **`GET /session` has side effects**: it renews the cookie, records the IP and the login origin, and mints the antiforgery and presence tickets. The client calls it every 30 s per tab.

### Checks that pass when a header is missing

- **Protocol gate.** **LB** for cached clients, **Q** in shape: it tests equality, so an upgrade and a rollback both produce 426; a missing header passes; it is enforced in three places; the client's `leavePresence` only works because missing passes.
- **Q** **Sec-Fetch-Site** rejects only the literal `cross-site`, POST-only on the API, all methods on the hub. `same-site` and absent both pass. No Origin check, by design.
- **Q** **`X-Yap-Chat-User` 409** is optional and is a cache-integrity control, not a security one. The same idea exists as a 401 in Push and as `X-Yap-Upload-User` in Tus.
- **Q** **Antiforgery covers only chat POSTs.** Blazor forms use none, `/auth/signin` and `/auth/invite` disable it, `/auth/signout` is a GET, `/api/push` and `/api/tus` rely on SameSite=Lax. Minimal APIs parse the JSON body before the filter runs; the per-request body limit is the only guard for that.

### Proxy and origin

- **Q** **Forwarded headers are trusted from any peer by default.** If `ReverseProxy:KnownProxies` and `KnownNetworks` are unset, the known lists are cleared. Neither key is in appsettings. A client that can reach Kestrel directly sets its own scheme and IP.
- **LB** **Per-user login origin** replaces the process-wide learned origin, and X-Forwarded-Host is ignored. A user can now poison only their own link. The fix I asked for; it is correct.
- **Q** **`IpHelper` still trusts `CF-Connecting-IP` and the leftmost X-Forwarded-For entry.** That IP feeds smart login, where username plus a known IP means passwordless login, and now also hub presence sessions.
- **Q** **Data protection keys are not persisted anywhere.** CSRF tokens, live tickets and renewal markers use the default key store. A container without a key folder invalidates them on every restart; a second instance breaks them.
- **Q** **Three shell classifiers must agree**: `ChatRoutes.IsShell` (any depth, case-insensitive), the endpoint templates (guid-constrained), the worker regex (case-sensitive). `/room/not-a-guid` is a shell for the middleware and for nobody else.
- **Q** **Routing does a cookie lookup.** `ChatRootPolicy` decides GET / by resolving the cookie, and doubles as its own metadata marker.

### Uploads, logging, diagnostics

- **Q** **Static mutable state** `TusEndpoints._completedFiles`, read only by the Blazor GIF manager through an unauthenticated one-shot route; chat entries age out after a day.
- **Q** **Upload receipts include the absolute server file path**, which now reaches the response header and `GET /api/chat/uploads/{id}`.
- **I** **Tus CORS allows any origin with credentials**; the chat client refuses non-same-origin upload endpoints, so for chat the policy is dead.
- **Q** **All chat traffic is logged** to the CSV and the per-user action log, including the SignalR connection token in the hub URL; action-log trimming runs only at startup.
- **Q** **The new path has no diagnostics.** RTT, slow events and send-to-appear timing exist only for Blazor circuits. The hub has no entry in `/diagnostics`. The only server-side signal is the "ChatWrites" error log.

### A reverse proxy now must

- Pass the public Host unchanged, or set `PublicOrigin`. nginx's default `Host $proxy_host` breaks login links.
- Set X-Forwarded-Proto; overwrite X-Forwarded-For rather than append; strip client-sent `CF-Connecting-IP` unless Cloudflare is in front.
- Allow the WebSocket upgrade on `/hubs/chat`; pass X-CSRF-TOKEN, X-Yap-Chat-Protocol, X-Yap-Chat-User, X-Yap-Upload-User, Sec-Fetch-\*, the response header X-Yap-Upload-Complete and the `?protocol=` query; honour no-store and no-cache.
- Never rely on X-Forwarded-Host, never cache shell HTML or `/chat-client/manifest.json`, and never leave Kestrel reachable without the known-proxy settings.

## 5. Storage (not re-audited tonight)

> The storage audit was stopped because the parallel session is rewriting this layer right now (commit 838a73c6 "Make memory the data layer again; the database is an optional copy", and the store files are already gone from the tree). What follows is from the PR review notes of 2026-10-10 and from today's measurements, not from a fresh read.

- **Five migrations are applied in production**: DurableTextSends, ObservedReadCheckpoints, UserLoginOrigin, ReceiptCountCap, ReceiptIndexByUser. Any code that goes back behind them must be checked column by column for NOT NULL columns without defaults.
- **Send receipts** (`TextSendReceipts`) give idempotency by operation id; the message id is the client's operation id. Capped per user, pruned after a day.
- **Observed read checkpoints** (`Received`, `ReadThrough`, `Source`) replaced mark-as-read; the review found the badge flashing on other devices and the audit `source` lost.
- **Memory mode** was removed by the PR (SQLite required to send), rejected, and restored before merge. The parallel session is now making memory the primary layer again.
- **The per-channel lock registry lives in the change tracker**, not in the domain service that uses it.
- **Acceptance locks**: keyed, reference-counted semaphores per operation, message and DM pair, so that a retry and its original cannot both commit.

## 6. What a rewrite must keep, and what it can drop

### Must keep, or migrate on purpose

- **A path to reach clients running cached old JS.** Old shells run until reload and call `registerWorker` on every load and visibility change. The server must keep serving `/chat-client/manifest.json` with `{version, assets[]}`, `/service-worker-module.js` at scope `/`, and `/service-worker.js` for Blazor-era registrations. Protocol 3 must stay accepted, or every old call must get 426 `update_required` so old clients fetch the new worker and reload. If those URLs return 404, the old worker stays in control and keeps serving the old shell.
- **Web manifest `id: "/"`, `scope: "/"`, `/pwa-launch?lt=`** and its endpoint. Changing `id` produces duplicate installs on Android.
- **Push**: the subscription is tied to the registration at scope `/`. Whichever worker controls `/` must handle `push` and `notificationclick` with the same payload.
- **Unsent work in IndexedDB `yap-chat-v1`** (`outbox`, `drafts`): migrate it or warn before dropping it. Opening the database at version 5 closes old tabs and breaks old JS. Old queued items rely on 1-day receipt idempotency on the server.
- **Cache cleanup**: delete `yap-chat-shell-*`, `yap-chat-media-*`, `yap-chat-emoji-*` and the bridge cache when the new worker activates.
- **localStorage keys the Blazor pages read**: `yap-appearance`, `yap-last-route`, `push-prompt-dismiss-count`; and the chat-client modules Blazor imports: `session-renewal.js`, `worker-updates.js`, `pwa.js`, `account-actions.js`, `shared.css`, or update `App.razor` and `chat.js`.
- **The privacy rules**: wipe or lock local data on the auth routes; a different account erases.

### Can change freely once a protocol bump forces old clients to reload

- Hub method names and argument shapes, API paths, the packet format and the merge algorithm.
- IndexedDB layout for caches only (`history`, `catalog`, `gif-*`), Web Lock and BroadcastChannel names, cache names and the versioning scheme.
- The shell DOM and CSS, CustomEvent names, every timer.
- The classic/module worker split and the probe.
- On the server: the presence design, the digest, the fan-out's queue and reset, the lock layout, the auth lookups, the inline middleware.

Not changeable by a server-only rewrite: anything under "Must keep". Installed phones decide when they reload.

## 7. Thirteen headline items, with an example and a fix

Added 2026-10-14 on request. One sentence of detail, one example, one proposed fix per item.

**Cached chat is not shown first.**
More: the bootstrap first confirms that the cached data belongs to the cookie's account, and paints only after that answer or a 10 s timeout.
Example: you open the app on a train with one bar of signal and look at an empty shell for ten seconds, although yesterday's messages are on the phone.
Fix: sign-in already runs through the worker, which wipes or locks local data on an account change, so paint the cache at once and let the bootstrap correct it.

**Every tab is a full client.**
More: each tab opens its own hub connection, merges each packet into IndexedDB and memory, then rebroadcasts it, and every sibling tab merges it again.
Example: with four tabs open, one new message causes four hub deliveries, four IndexedDB commits and twelve BroadcastChannel merges.
Fix: elect one leader tab with the Web Lock the outbox already uses, let only the leader connect and write, and let the other tabs read from BroadcastChannel.

**Secure context only.**
More: service workers, Web Locks and `crypto.randomUUID` exist only on HTTPS or localhost, and the client refuses to start without them.
Example: you test on your phone over Wi-Fi at `http://192.168.1.20:5000` and get "Offline chat has not been set up or is locked".
Fix: treat offline mode as an extra, and when the APIs are missing run online-only with a UUID from `getRandomValues` and a tab-local lock.

**Every deploy invalidates every cached window.**
More: the server stamps every window with an epoch created at process start, and a client that sees a new epoch drops all cached windows and refetches them one by one.
Example: you restart the container for a config change, each phone runs a full bootstrap every 3 s against the dead server, then pulls 30 conversation windows when it comes back.
Fix: persist the epoch next to the data and bump it only when history changes shape, and use SignalR's own reconnect with backoff.

**A release re-downloads the whole shell, and the manifest is hashed once.**
More: the server hashes every shell file at startup, and the worker installs a release only when every file matches, so one changed byte re-fetches all 170 files.
Example: a one-line CSS fix costs every phone 1.9 MB, and a branding edit without a restart makes every install fail with "Shell asset changed".
Fix: let the worker compare hashes per file and fetch only the changed ones, and recompute the manifest when a file changes instead of once at startup.

**Two service workers plus a probe.**
More: the new worker is an ES module so it can import constants.js, browsers without module workers keep the old classic worker, and a probe with a type getter decides which case applies.
Example: a user on Firefox ESR 128 never gets offline mode, and every browser that upgrades from the Blazor-era worker runs the bridge that copies the whole shell into a temporary cache first.
Fix: one classic worker that loads the shared constants with `importScripts`, no probe and no bridge.

**A failed message cannot be removed.**
More: after 8 attempts or any final 4xx the outbox row is marked failed, the UI offers only Retry, and delete queues a server delete for an id the server never stored.
Example: you post in a room, an admin removes you before the send lands, the server answers 403, and the red row stays in your view forever.
Fix: make delete on a failed row a local operation that removes the outbox row and the optimistic message without queuing anything.

**Identity is looked up 30 times.**
More: the chat API filter authenticates the cookie, then each handler calls `AuthenticateByToken` again, and the hub, Tus, Push and Blazor see an anonymous principal because no authentication scheme is registered.
Example: one bootstrap authenticates four times, and if the user signs out other devices between filter and handler, a POST gets a misleading 503 "safe to retry".
Fix: register one cookie authentication handler so `HttpContext.User` carries the account once per request, and read it from there everywhere, including the hub and antiforgery.

**The live ticket guards the wrong stream.**
More: the 2-minute ticket is checked only in Join, which registers presence, while WatchChanges, the stream that carries private messages, relies on the cookie alone.
Example: anyone with the cookie opens WatchChanges and reads every DM, and the ticket only stops them from appearing in the online list.
Fix: drop the ticket, because the cookie check that both streams already run is the real guard, and a ticket that is not single-use adds no second factor.

**Cookie renewal runs through the chat API.**
More: the PR removed renewal on HTML page loads, so Settings and Admin include a script that calls GET /api/chat/session hourly, behind the chat protocol gate and the chat API filter.
Example: you bump the chat protocol, an admin with an old Settings tab gets 426 on every renewal call, and the cookie ages out without warning.
Fix: renew the cookie in the auth middleware on any authenticated request, as before the PR, so no page depends on the chat API.

**Forwarded headers are trusted from any peer.**
More: when the known-proxy keys are unset the known lists are cleared, so any peer that reaches Kestrel can set its own scheme and IP, and IpHelper separately trusts CF-Connecting-IP.
Example: someone on the LAN who can reach port 5000 sends CF-Connecting-IP with your home IP and your username, and smart login lets them in without a password.
Fix: default the known proxies to loopback, make IpHelper read only `RemoteIpAddress` as set by the forwarded-headers middleware, and honour CF-Connecting-IP only behind an explicit Cloudflare setting.

**Data protection keys are not persisted.**
More: antiforgery tokens, live tickets and renewal markers are encrypted with ASP.NET data protection, whose default key ring in a container does not survive the container.
Example: run two instances behind a load balancer and half of all sends fail the CSRF check, because each instance has its own keys.
Fix: one line in Program.cs, `PersistKeysToFileSystem` on the data folder next to the SQLite file, plus `SetApplicationName`.

**No diagnostics for the new path.**
More: the circuit path measured RTT, slow events and send-to-appear timing on /diagnostics, while the hub and the chat API have no timing, no counters and no entry there.
Example: a user says messages are slow today, and the only server-side evidence for the JS client is the CSV request log.
Fix: time the chat API handlers, the hub methods and the fan-out lag from tracker stamp to client delivery, and show them on /diagnostics beside the circuit numbers.
