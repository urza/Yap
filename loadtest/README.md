# Yap load test

Three console apps that put synthetic users on a Yap test instance and measure what the server does.
The solution is standalone: it talks to the server over HTTP and SignalR only and needs nothing from
the `Yap` project. The server side of the measurement is `GET /api/admin/load` and the "Server load"
card on the admin Diagnostics tab, both part of Yap itself.

| Project | Runs on | What it does |
|---|---|---|
| `Yap.LoadTest.Orchestrator` | the load machine | Prepares the server (admin, settings, users, rooms), starts worker processes step by step, shows the live dashboard, writes the results. |
| `Yap.LoadTest.Worker` | the load machine | One process, up to 250 virtual users. Started by the orchestrator; it prints one JSON line per second. |
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

# on the load machine
dotnet Yap.LoadTest.Orchestrator.dll --server https://test.example.org --preset big-room --steps 50,200,500,1000 --hold 10m
```

Keys in the dashboard: `n` ends the current step now, `q` stops the run and prints the summary.
Pass `--plain` for one log line every 5 s instead of the dashboard (an ssh session without a tty).

### Options

| Option | Default | Meaning |
|---|---|---|
| `--server URL` | required | The test instance. http is fine; the tool sends the auth cookie by hand. |
| `--preset NAME` | `big-room` | See the table below. |
| `--steps 50,200,500,1000` | as shown | User counts, one step each. The run holds at each count. |
| `--hold 10m` | `10m` | Time at the plateau after the ramp of a step. Suffixes `s`, `m`, `h`. |
| `--ramp 60s` | `60s` | New users of a step start spread over this time. |
| `--per-worker 250` | `250` | Users per worker process. |
| `--admin NAME` | `loadadmin` | The admin account. On a fresh instance the first account becomes the admin. |
| `--admin-code CODE` | | The admin's login code when the account already exists. |
| `--prefix lt_u` | `lt_u` | Test user names: `lt_u0001` and so on. |
| `--links FILE` | built-in list | One URL per line; the video links the users share. |
| `--visible 0.3` | the preset's | The share of users with a visible tab. |
| `--state DIR` | `state` | Where the tokens of the created accounts are kept. |
| `--out DIR` | `results` | Where a run's `timeline.csv` and `summary.md` go. |

### Presets

| Preset | Shape |
|---|---|
| `big-room` | Everyone in the lobby. One message reaches every connection. |
| `small-rooms` | Twenty rooms, each user has a home room and hops now and then. |
| `dm-heavy` | Half of the traffic is direct messages. |
| `media-heavy` | Images and video links dominate; every third image is a fresh upload. |
| `burst` | Short think time, many bursts, mostly short texts and emoji. Previews off. |
| `idle-crowd` | Almost everyone hidden and idle. The cost of connections and heartbeats alone. |

## What a virtual user does

It signs in once (the orchestrator keeps the token), connects the hub with a cookie header and
WebSockets, bootstraps, opens its room and sends heartbeats like `hub.js` does: every 10 s when
visible, every 60 s when hidden. It reconnects with the same backoff as the browser.

A visible user thinks (an exponential pause around the preset's mean), types, and sends one message
from the preset's mix: a short text, a long text, emoji only, a custom emoji shortcode, a video link
from the pool, an image, a reply, a reaction, or a direct message. After a send it sometimes fires a
burst of 2 to 5 quick follow-ups, sometimes hops to another room, sometimes steps away for minutes,
sometimes hides its tab. It marks the room read when others write. Images come from a pool of 20
generated JPEGs per worker; a share of image sends upload a fresh one, so thumbnailing stays loaded.

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

The summary table has one row per step. The knee is the step where send-to-appear p95 leaves the
tens of milliseconds, or where the writer depth or the thread pool queue climbs without coming back.

## Before a run on the test instance

- Registration must be open and approval off, or the orchestrator cannot create accounts. The
  orchestrator sets both through the admin API when the admin signs in.
- Link previews follow the preset. With previews on, the server fetches each URL once an hour and
  yt-dlp downloads at most two videos at a time, so the built-in pool of about 25 links keeps the
  traffic to the platforms small. A server without yt-dlp refuses media caching; the run goes on
  without it.
- Persistence is a server setting (`ChatSettings:Persistence:Enabled`). Run the heavy presets twice,
  once on and once off, and compare the writer depth: every room message queues one unread write per
  connected user.
- Every new account makes the bot send the admin a direct message. With a thousand accounts that is a
  thousand messages in the admin's DM, once.
- The first account on a fresh instance is the admin. Start the orchestrator before anyone else signs
  in, or pass `--admin` and `--admin-code` for the real admin.
- File descriptors: a worker holds one WebSocket per user. `ulimit -n 4096` on the load machine
  covers four workers of 250.

## Results

`results/<time>-<preset>/timeline.csv` has one row per second with the window percentiles and the
server sample. `summary.md` is the per-step table. The host CSV lives where HostMonitor ran; join
the two on the time column.
