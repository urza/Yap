# Yap load test

Three console apps that put synthetic users on a Yap test instance and measure what the server does.
The solution is standalone: it talks to the server over HTTP and SignalR only and needs nothing from
the `Yap` project. The server side of the measurement is `GET /api/admin/load` and the "Server load"
card on the admin Diagnostics tab, both part of Yap itself.

| Project | Runs on | What it does |
|---|---|---|
| `Yap.LoadTest.Orchestrator` | the load machine | Prepares the server (admin, settings, rooms, accounts), starts the workers, shows the dashboard, takes your keys, writes the results. |
| `Yap.LoadTest.Worker` | the load machine | One process, a block of 250 accounts. The orchestrator starts it and tells it over stdin how many of them run and what they do. It prints one JSON line per second. |
| `Yap.LoadTest.HostMonitor` | the VPS | Samples `/proc` once a second (CPU, memory, disk, network, the Yap process) into a CSV. Works when the app is down. |

## Build and publish

```bash
cd loadtest
dotnet build
# or, for a machine without the SDK:
dotnet publish Yap.LoadTest.Orchestrator -c Release -o out/orchestrator   # contains the worker too
dotnet publish Yap.LoadTest.HostMonitor -c Release -o out/hostmonitor
```

The orchestrator starts `dotnet Yap.LoadTest.Worker.dll` from its own folder. Pass `--worker <path>`
when the worker lives elsewhere.

## Run

Run the load from a different machine than the server. A worker uses real CPU for JSON, WebSockets
and image encoding, and on the same box it steals the CPU you want to measure.

```bash
# on the VPS, next to the running Yap container
dotnet Yap.LoadTest.HostMonitor.dll --match Yap.dll --out host.csv

# on the load machine (bash or PowerShell, the same line)
dotnet Yap.LoadTest.Orchestrator.dll --server https://test.example.org
```

The run starts with no users and goes on until you press `q`. You drive it from the keyboard:

| Key | Effect |
|---|---|
| `+` `-` (or the up and down arrows) | 50 users more or fewer |
| `PgUp` `PgDn` | 250 users more or fewer |
| digits, then `Enter` | an exact user count; `Esc` cancels the typing |
| `t` `r` `l` `i` `d` | text, reactions, links, images, DMs on or off |
| `<` `>` | the pace: calm, normal, busy, frantic |
| `q` | stop, disconnect everyone, write the summary |

Every other key does nothing. New users join at `--rate` per second (10 by default); removed users
leave at once. The first time the count reaches into a new block of 250, the orchestrator creates
those accounts, which takes a moment, and the dashboard says so.

The dashboard is six lines: users (running, wanted, connected), kinds, pace, messages per second
and the totals of the current segment, latency over the last 10 s, and the server. A segment is a
stretch of unchanged settings; the header shows how long the current one has been held. Under the
numbers are the last log lines. The row under the title is the admin's login link. Open it in a browser to
watch the Server load card on the Diagnostics tab while the run goes. The link prints again after the summary.

Without a terminal (`--plain`, or stdout is a file or a pipe) the orchestrator prints one line every
5 s instead, and `--users`, `--kinds`, `--pace` and `--for` set the load from the command line:

```bash
dotnet Yap.LoadTest.Orchestrator.dll --server https://test.example.org --users 500 --kinds text,reactions --pace busy --for 20m --plain
```

### PowerShell and Windows Terminal

The dashboard draws on the terminal's alternate screen, from the top-left corner on every frame, the
way `top` does. Minimising, restoring or resizing the window cannot make frames repeat under each
other; the next frame overwrites the screen. When the orchestrator ends, your prompt and its history
come back, and the summary prints there. The legacy console host without ANSI support gets the plain
output automatically.

### Options

| Option | Default | Meaning |
|---|---|---|
| `--server URL` | required | The test instance. http is fine; the tool sends the auth cookie by hand. |
| `--users N` | `0` | The user count at the start. |
| `--kinds LIST` | `all` | The kinds on at the start: `all`, `none`, or a list such as `text,links`. |
| `--pace NAME` | `normal` | `calm`, `normal`, `busy` or `frantic` at the start. |
| `--rooms N` | `1` | Test rooms `lt-room-NN`; each user has a home room and hops now and then. 1 means everyone in the lobby. |
| `--visible 0.3` | `0.3` | The share of users with a visible tab. Hidden users only heartbeat. |
| `--rate 10` | `10` | New users per second. 0 starts them all at once. |
| `--per-worker 250` | `250` | Accounts per worker process. |
| `--admin NAME` | `loadadmin` | The admin account. On a fresh instance the first account becomes the admin. |
| `--admin-code CODE` | | The last part of the admin's login link, when the account already exists. |
| `--prefix lt_u` | `lt_u` | Test user names: `lt_u0001` and so on. |
| `--links FILE` | built-in list | One URL per line; the video links the users share. |
| `--for 30m` | run until `q` | End on its own after this time. Suffixes `s`, `m`, `h`. |
| `--state DIR` | `state` | Where the tokens of the created accounts are kept. |
| `--out DIR` | `results` | Where a run's `timeline.csv`, `log.txt` and `summary.md` go. |
| `--plain` | | One log line every 5 s instead of the dashboard. |

### Kinds and pace

A visible user acts, pauses, acts again. The pause is an exponential draw around the pace's mean:
calm 60 s, normal 20 s, busy 6 s, frantic 2 s. Busy and frantic start a burst of 2 to 5 quick
follow-ups after half of the sends; normal after 15 %.

Each action is one kind, drawn by weight: text 70 (short, long, emoji, a custom emoji shortcode, a
reply), reactions 12, links 7, DMs 6, images 5. A kind that is off drops out and the kinds that are
on share its actions, so the number of actions per second does not change with the switches. To see
what one kind costs, turn it off and watch the server line; to see it alone, turn the others off.
With every kind off the users only read and heartbeat: the cost of the connections alone.

Kinds and pace reach the workers within one think time. Users that are mid-pause pick up the change
on their next action.

## What a virtual user does

It signs in once (the orchestrator keeps the token), connects the hub with a cookie header and
WebSockets, bootstraps, opens its room and sends heartbeats like `hub.js` does: every 10 s when
visible, every 60 s when hidden. It reconnects with the same backoff as the browser.

A visible user thinks, types, and sends one message of the kinds that are on. After a send it
sometimes hops to another room (with `--rooms`), sometimes steps away for minutes, sometimes hides
its tab. It marks the room read when others write. Images come from a pool of 20 generated JPEGs per
worker; one in ten image sends uploads a fresh one, so thumbnailing stays loaded.

## What is measured

| Metric | Source |
|---|---|
| Send to appear | POST start to the own `message` hub event with the same clientId. |
| Fan-out | The clientId carries the send time, so any receiver on the same machine measures sender to receiver without clock skew. |
| HTTP p95 | The message POST round trip. |
| Heartbeat RTT | The `Heartbeat` invoke round trip. |
| Errors, reconnects | By reason: `http 409`, `hub closed`, `upload timeout`, `no echo in 60 s`. |
| Server | `/api/admin/load`: CPU, working set, heap, GC, thread pool queue, hub connections, messages/s, hub sends/s, requests/s and p95, writer queue depth, presence sweep time, host load and free memory. |
| Host | HostMonitor CSV: whole-box CPU and iowait, memory, swap, disk MB/s, network MB/s, established TCP, the Yap process's CPU, RSS, threads and open files. |

Percentiles come from log-spaced buckets and are the upper bound of the bucket, so a reported
p95 of 20 ms means "at most 20 ms". Max and mean are exact.

The summary table has one row per segment. The knee is the segment where send-to-appear p95 leaves
the tens of milliseconds, or where the writer queue or the thread pool queue climbs without coming back.

## Before a run on the test instance

- Registration must be open and approval off, or the orchestrator cannot create accounts. The
  orchestrator sets both through the admin API when the admin signs in.
- The orchestrator turns link previews and media caching on. With links on, the server fetches each
  URL once an hour and yt-dlp downloads at most two videos at a time, so the built-in pool of about
  25 links keeps the traffic to the platforms small. A server without yt-dlp refuses media caching;
  the run goes on without it. Turn links off (`l`) and that traffic stops.
- Persistence is a server setting (`ChatSettings:Persistence:Enabled`). Run the same load twice,
  once on and once off, and compare the writer queue.
- Every new account makes the bot send the admin a direct message. Accounts are created in blocks of
  250, so the admin's DM gets 250 messages per block, once.
- The first account on a fresh instance is the admin. Start the orchestrator before anyone else signs
  in, or pass `--admin` and `--admin-code` for the real admin.
- A new server with the host and port of an old one: delete `state/<host>_<port>.json` on the load machine
  first. The orchestrator would reuse the old tokens, and the new server does not know them.
- File descriptors on Linux: a worker holds one WebSocket per user. `ulimit -n 4096` on the load
  machine covers four workers of 250. Windows needs nothing.

## Results

`results/<time>/timeline.csv` has one row per second: the settings of that second, the window
percentiles and the server sample. `log.txt` is the log. `summary.md` has one row per segment that
lasted 30 s or more; the shorter ones are the moments between two key presses. The host CSV lives
where HostMonitor ran; join the two on the time column.
