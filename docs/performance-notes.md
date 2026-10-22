# Fast load and resume: design notes

Written 2026-10-22. These notes guide the work on load time and resume time for one user on a slow connection. They record the decisions of the design dialogue of that day. They are not the product spec.

## The target

The target user is far from the server and has a bad connection. Production data from 2026-08-11 gave a round trip of about 900 ms for such a user, while the slowest server send cost 71 ms. The wire dominates. Every round trip on the critical path costs that user about a second.

The changes are universal. A change that removes a round trip from the critical path helps every user. Nothing in this plan is specific to one device or one network.

Constraints:

- The app and the build need only dotnet. No node, no bundler. The `{{build}}` stamp is already written into the page by C# at request time (`Endpoints/ShellEndpoints.cs`), and the new generated parts use the same mechanism.
- The existing tests in `tests/browser` use node and Playwright. That stays the only place node appears. The app never depends on it.
- The font stays as it is. Inter covers Latin, Greek and Cyrillic (2,852 code points) and no Arabic, Hebrew or CJK. The browser draws those from a system font. A subset would drop Greek and Cyrillic for no gain, because the font loads once per device and then stays cached.
- Simplicity wins. Build on the mechanisms that exist (the build stamp, the per-build worker cache, the heartbeat telemetry, the admin diagnostics, the connection dot).

## What the code does today

Facts from the code on 2026-10-22, before any change.

**The shell over HTTP.** The page, all scripts and all stylesheets are served with `Cache-Control: no-cache` (`Program.cs`, `ShellEndpoints.cs`). The two font files have no cache header at all. The service worker fetches the page, the scripts, the styles and the fonts network-first with no timeout (`service-worker.js`). So every open goes to the network for about 45 files before it paints. Caddy and Cloudflare answer over HTTP/2 or HTTP/3, so the requests run in parallel, but the paint still waits for the network. Cloudflare does not cache a `no-cache` answer, so no deployment gets the shell from the edge.

**Media.** Uploads, GIF cache, media cache and emoji are already correct: immutable headers and cache-first in the worker. Profile pictures are excluded by design (stable URL). See the memory `static-asset-caching`.

**The cold load.** The order in `js/main.js` is: the page and its scripts, then `await /api/bootstrap` with nothing painted, then the frame, then the chat page paints the stored snapshot from IndexedDB, then the hub connects (negotiate, upgrade, handshake), then `openRoom` returns the newest 50 rows. The hub connect starts only after the bootstrap answer. The bootstrap is fetched a second time after the connect (`js/hub.js`). The stored bootstrap is used only when the fetch throws.

**The resume.** When the installed app returns after an hour in the background, the page is alive and shows the old rows. The socket is dead, but the client state is still `connected`. The visibility handler reconnects only when the state is `Disconnected`, so it does nothing. The client waits for SignalR's 30 s server timeout, then 1 s of retry delay, then negotiate, upgrade, handshake and `openRoom`. The dot hides when the socket connects, before the rows arrive. The user sees the old screen for about 30 s, then a short amber dot, then the new rows. Missed messages are not replayed; the client refetches the tail of 50 rows.

**The loading UI.** The connection dot (hidden when connected, amber while connecting, grey when offline, red when signed out or when a new build is up). The "Loading messages…" line shows only for a conversation with no stored snapshot. The notice strip shows only "Please refresh" for a new build. Nothing marks rows that came from the device and not yet from the server.

**Round trips before live rows**, counted from the code for an installed app with everything cached:

| | round trips |
| --- | --- |
| today | 7 to 8: page, script revalidation, bootstrap, negotiate, upgrade, handshake, openRoom |
| after this plan | 3: upgrade, handshake, openRoom. The paint needs 0. |

## The order of work

Measure first, then the caching, then the resume, then the cold chain. The first numbers after the work are the baseline. Later optimisations target the slowest phase in the admin table.

### 1. The boot report

One report per page load, sent with the first heartbeat, the same way as the waits of AD-26 (`hub.report`, see the memory `latency-telemetry-waits`). Only the first page mount of a load reports, like `start`.

The phases, in ms from the navigation start (Navigation Timing and `performance.mark`):

| Phase | Meaning |
| --- | --- |
| `html` | The page has arrived (`responseEnd`). |
| `scripts` | The modules are loaded (`page-mount` mark). |
| `bootstrap` | The bootstrap answer is applied. |
| `paint` | The first rows are on screen, from the device or from the server. |
| `socket` | The hub reports connected. |
| `rows` | The server's rows are on screen. |

The facts, once per load:

| Field | Source |
| --- | --- |
| protocol | `nextHopProtocol` of the navigation entry: `h2`, `h3` or `http/1.1`. |
| requests | The count of resource entries. |
| cached | The count of entries with `transferSize === 0`, answered by a cache. |
| bytes | The sum of `transferSize`. |

The device facts come from the server at connect, as they already do: the PWA flag from the hub URL (`pwa=1`), mobile or desktop from the User-Agent header (`UserAgent.IsMobile`). The browser name and the OS are new: a small regex table next to `IsMobile`, no library. Both go on the `Session`.

Storage: in memory since restart, as the existing latency stats. The last report per connection, and an average per person.

Admin: a "Load time" table in the Diagnostics tab. One row per load: person, browser and OS, mobile or desktop, PWA or tab, protocol, the six phases, requests, cached, bytes. The slowest `rows` first.

Rules: numbers outside 0..600,000 ms are dropped, as for the waits. A load in a hidden tab still reports the phases, because Navigation Timing is not throttled, but the row is marked hidden.

### 2. The dev-loop check

A Playwright script in `tests/browser`, `npm run load`. It drives a throwaway server, like the other scripts there. The browser's network runs through a CDP throttle: 900 ms round trip, 1 Mbit/s each way, as a start. The numbers from the boot reports in production replace them later.

The script reports, for comparison before and after a change:

1. Load 1, cold: the six phases, requests, cached.
2. Load 2, warm: the same. Cached must be close to requests.
3. The resume: the page goes hidden, the server drops the connection, the page returns. The time from the return to the server's rows on screen.

It prints the numbers. It does not assert, because the throttle makes the numbers noisy. The reader compares two runs.

### 3. The shell caching

**The worker goes cache-first for the shell.** A navigation, a script, a stylesheet, a font, an icon and the notification sound are answered from the per-build cache (`yap-shell-{build}`) when present, and from the network otherwise. The per-build cache, the precache at install, `skipWaiting`, `clients.claim` and the cleanup at activate stay as they are. This changes rule OF-06 from "a page online always runs the build the server has" to "a page runs the build its worker installed, and the new-version strip asks for a refresh when the server's build differs". The check that drives the strip stays: the bootstrap's build stamp against the page's (`js/main.js`, SH-20, OF-04). The spec page `docs/spec/offline.md` gets the new OF-06 wording when this step lands.

An open page of the old build that lazy-loads a page module after a deploy gets the current file from the network, as today. See the memory `mixed-build-document`. The strip asks for a refresh, as today.

**The shell list is generated.** Today `SHELL_URLS` in the worker is 60 URLs kept by hand. The server serves `service-worker.js` through an endpoint that substitutes `{{build}}` and `{{shell}}`, as the shell endpoint does for the page. The list is computed once at startup from `wwwroot`: `js/**/*.js`, the stylesheets, `lib`, `fonts`, `images`, the icons, the sound. The query `?v={build}` on the registration stays, so the worker URL still changes per build.

**Every module gets a versioned URL, with an import map.** The page gets `{{importmap}}` in its head, generated at startup from `wwwroot/js/**/*.js`:

```json
{ "imports": { "/js/main.js": "/js/main.js?v=20261022…", "/js/store.js": "/js/store.js?v=20261022…" } }
```

A relative import such as `./store.js` resolves to `/js/store.js` first, and then the map rewrites it. No import in any script changes. The `modulepreload` links and the two classic scripts (`lib/signalr.min.js`, `lib/tus.min.js`) get `?v={{build}}` in the page, as the stylesheets already have. Import maps work in every browser since Safari 16.4 (2023).

**The HTTP headers.**

| File | Header |
| --- | --- |
| The page | `no-cache`, as today. It carries the build stamp. |
| `.js`, `.css` with a `v` query | `public, max-age=31536000, immutable` |
| `.js`, `.css` without a `v` query | `no-cache`, as today, for a bare URL from an old page |
| `.woff2` | `public, max-age=31536000, immutable`. The file name changes when the font changes. |
| `service-worker.js` | `no-cache`. The browser checks it on every registration anyway. |

With these headers the Cloudflare deployments serve the versioned files from a nearby edge. The page still comes from the origin. The Caddy deployments need no change.

**The build stamp invalidates every shell file on each deploy.** That is about 150 KB compressed, fetched by the new worker in the background while the old page runs. A per-file hash would invalidate only the changed files. It adds machinery, so the stamp stays until deploys become frequent enough to matter.

### 4. The resume

**The client drops a dead socket at once.** On `visibilitychange` to visible, when the page was hidden longer than the server's client timeout (30 s), the client calls `stop()` and then `connect()` without waiting for the 30 s client timeout and without the 1 s retry delay. The server has dropped the connection already. The `online` event does the same. The chat page re-opens the conversation on `connected`, as today. The `reconnect` wait keeps its 35 s rule, measured from the return.

When the page was hidden for less than 30 s, nothing changes: the socket is alive, or the existing client timeout handles it.

**The "Catching up…" pill.** A small pill floats over the top of the message list. It takes no vertical space, so no row moves when it appears or goes away (the rule from the pinned-messages work). It holds a spinner and the text "Catching up…". With `prefers-reduced-motion`, the spinner is still.

The pill shows from the moment the rows on screen came from the device and the server has not answered yet:

- the visible event after a long background, until `openRoom` returns and the rows are on screen, and
- the cold open with a stored snapshot, from the cached paint until the server's rows are on screen.

It ends when the server's rows render, not when the socket connects. When the reconnect fails, the pill does not spin forever: it turns into a static "No connection. Showing older messages." and the dot shows the attempts, as today. The dot stays as it is: it reports the socket, the pill reports the content.

Wording: "Catching up…" describes the check, and stays true when no new message arrived. "Updating…" reads as an app update next to the "Please refresh" strip. "Loading new messages…" promises a result that most catch-ups do not have.

The "Loading messages…" line for a conversation with no snapshot stays.

### 5. The shorter cold chain

1. **Paint from the stored bootstrap at once.** Today the stored bootstrap is used only when the fetch throws. With it, the frame and the snapshot paint before any network answer. The fetched bootstrap replaces it when it arrives.
2. **Start the hub connect in parallel with the bootstrap fetch.** When the stored bootstrap says signed in, `hub.connect()` starts at once. Today it waits for the bootstrap answer.
3. **One bootstrap request per load.** Today there are two (`main.js` and `hub.js`). The connection-bound request mints the beacon nonce, so the implementation decides which of the two stays and where the nonce goes.
4. **Skip the negotiate round trip.** `skipNegotiation: true` with the WebSockets transport saves one round trip per connect. SignalR then has no fallback transport, so the client tries the direct WebSocket first and, when that fails, builds a connection with negotiation as today. Caddy and Cloudflare both pass WebSockets. The admin diagnostics already show the transport per session (AD-26), which tells whether any deployment needs the fallback.

## After the work

Deploy. Read the Load time table after a few days. The slowest phase per device class is the next target. Likely candidates, to confirm with numbers and not before: the 50-row tail with media thumbnails, the size of the page (90 KB raw), the emoji catalog fetch at boot.

## Open points

- The resume design treats a hidden page as alive. iOS often evicts the installed app instead, and a relaunch follows the cold path. Both paths end in the same pill, so the user sees the same thing.
- Whether a browser's timer clock runs during suspension decides whether the 30 s client timeout fires at once on return or runs its full 30 s. The fix above makes this moot for a long background, and the question stays only for a short one.
- `Microsoft.Playwright` (NuGet) could replace the node scripts in `tests/browser` one day, so that node leaves the repository entirely. Not part of this work.
