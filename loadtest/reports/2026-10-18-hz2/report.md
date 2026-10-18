# Load test 2026-10-18, hz.urza.cc (second VPS), run 1

One segment: 800 users, all kinds, pace normal, 3 min 56 s (20:39:56 to 20:43:52 local). The load ran on the
developer's machine over the internet. The server is a fresh VPS under the old name, 15.6 GB of memory and 4 cores
(the ratio of the process's CPU to the app's own percentage gives 3.65). Persistence on, media caching on.
Files: `timeline.csv` (one row per second), `host-01.csv` (HostMonitor on the VPS), `log.txt`, `summary.md`.

## The numbers of the segment

| Measure | Value |
|---|---|
| Messages sent by the users | 12.4 per second, 2,933 in all |
| Send to appear p50 / p95 / p99 | 100 / 150 / 200 ms |
| Heartbeat RTT p95 | 100 ms (the network floor from the load machine) |
| Message POST p95, measured by the users | 250 ms |
| Request p95, measured by the server | 3.3 s mean, 32.5 s max |
| Errors | 7, all `upload timeout` |
| Box CPU (host) | 90 % mean, 99 % max |
| Yap process CPU (host) | 175 % mean, 218 % max, of 400 |
| Load average | 8.2 mean, 12.9 max |
| iowait | 0.15 % |
| Writer queue | 7 at the start, 628,070 at the end, still climbing |
| Gen 2 collections | 207 in 207 s |
| Working set | 468 MB mean, 554 MB max |
| Thread pool queue | median 0, mean 104, max 803 |
| TCP connections on the box | 2,345 mean, 2,581 max (802 are hub connections) |
| Network out of the box | 8.3 MB/s mean, 18.4 MB/s max |

## What it says

**The chat path is fine at 800.** Send to appear sits on the network floor. The server adds tens of milliseconds.

**The writer is the first limit.** A room message gives every connected user who is not looking at the room an
unread count, an `unread` event and a read-state write (`ChatService.IncrementUnread`). 30 % of the users had a
visible tab, so at least 560 of 800 got a write per message: about 7,000 writes per second. The writer drained
3,000 to 5,000 per second and the backlog grew by 120,000 to 235,000 per minute. The batch cap and the tracker
clear from 2026-10-17 kept the writer alive; it did not stall. It cannot keep up. Most of the backlog is the same
560 rows written again on every message. The working set and the gen 2 collections, one per second, are the
backlog's cost.

The fix is a dirty set for read states: the writer keeps the set of changed (user, channel) keys and writes each
row once per pass. At 800 users that is at most 800 rows per pass, whatever the message rate.

**The box was out of CPU, and not because of chat.** Yap used 1.75 cores. The other 1.85 cores went to yt-dlp and
ffmpeg: media caching was on and the users shared video links. The medium thumbnail of an uploaded image runs
inside the tus PATCH request (`TusEndpoints`, `GenerateMediumThumbnailAsync` is awaited before the answer). With the
cores taken, that request took seconds, up to 32 s, and seven uploads passed the 30 s limit of the load tool. That
is the server-side request p95 of 3.3 s; message POSTs stayed at 250 ms. The turn-off for the next run is the `l`
key: links off, and yt-dlp and ffmpeg stop. A lasting fix is to run yt-dlp and ffmpeg children at a low priority
(nice 19) and to cap ffmpeg's threads, so chat and thumbnails win the CPU when a video is being cached.

**The thread pool queue is the fan-out, not starvation.** The samples are 0 for 108 of 207 seconds and jump to
numbers near the connection count (800, 803, 617, 555) for a sample or two, then back to 0. A broadcast to 800
connections queues about 800 continuations at once, and the once-a-second sample catches that moment. The same
number appeared on 2026-10-16 at 800 users (804). It needs no fix. The load card could sample the queue several
times a second and show the mean, so the maximum stops looking like a problem.

**TCP connections** were three times the hub connections: the users' HTTP connection pool on the load machine
(message POSTs, uploads, info polls), the orchestrator's load polls, and yt-dlp's and the link previewer's outbound
connections. Not a limit; `proc_fds` peaked at 1,183.

## Before the next run

1. Read-state dirty set in the writer. Then the writer queue should stay flat at 800 users.
2. Low priority for yt-dlp and ffmpeg children, or links off for the chat ceiling.
3. Plan: 800 with links off, hold 3 minutes; then 1,200 and 1,600 to find the chat ceiling; then one segment with
   links on at the ceiling to see the media cost; then the same with persistence off to compare the writer.

## The admin's own tab (found during run 2, 1,000 users, previews off)

The person running the test waited more than a minute for a message in the lobby, with one tab open, while a
message POST from another machine took 200 to 400 ms and the server answered anonymous requests in 250 ms. A
headless Chromium on the same page, signed in as the admin, reproduced it: the page loaded in 3.5 s, then the
main thread stopped answering (a fill and a screenshot both timed out after 30 s). The tab received only 13 hub
frames a second (12 typing, 5 messages, 4 KB/s). A CPU profile over 20 s had the main thread busy for 32 of 34
sampled seconds: DOM construction 41 % (`el`, `append`, `cloneNode`), the people list's sort and `renderPeople`
11 %, the forced layouts of `scrollToBottom` and the scroll handler 13 %.

The people list is every account, 1,002 on this server, and it was rebuilt on every presence, unread, DM, mute
or open change. After the run stopped, the tab kept showing typing and new messages for minutes: the queued
frames, worked through one rebuild at a time. The load tool cannot see this; its users have no DOM.

Fix in bc0d7d53: the people list renders at most once per frame and at most every 250 ms, and skips the
rebuild when a signature of everything a row shows is unchanged; scroll-to-bottom does one layout per frame.
Next step if the rebuild itself still costs too much: rows keyed by username with in-place updates.

## Run 2 (21:14 to 21:38 local, previews off, 1,000 users for 18 minutes)

The client-side files of this run are lost: the orchestrator's `results\` folder and the `state\` file were
deleted by a sandbox cleanup of `loadtest/*/bin` after the run (Wren's mistake; the folder was the user's
Windows build output, which the orchestrator ran from). What remains: `run2/server-load.csv`, the server's own
one-second samples pulled from `/api/admin/load` before its one-hour buffer aged out (19:10 to 19:48 UTC), the
host CSV on the VPS, and the host-side summary the user made (CPU 99 %, Yap at 304 % of 400, RSS 1,650 MB,
network out 27 MB/s, five writes dropped with "FOREIGN KEY constraint failed").

| At 1,000 users, 1,068 s | Value |
|---|---|
| Yap process CPU | 68 % of 400 (2.7 cores) |
| Load average | 11.9 |
| Messages per second | 19.9 |
| Hub sends per second | 9,126 |
| Thread pool queue | mean 227, median 17, max 1,346 |
| Thread pool threads | 20 |
| Server request p95 | 15 s mean, 211 s max |
| Writer queue | 1 to 6,343,346 |
| Working set, heap | 1,597 MB, 1,089 MB max |
| Gen 2 collections | 17 per minute |
| Presence sweep | 41 ms |

**The chat ceiling of the 4-core box is about 1,000 users in one room at normal pace.** With previews off,
nothing but chat ran, and the box was still at 99 %: fan-out of 9,000 hub sends a second, the read-state writes,
and the garbage collector over a growing backlog. The pool queue's median moved from 0 (run 1, 800 users) to 17.

**The slow requests are most likely the image uploads from the load machine.** Request p95 of 15 s and a maximum
of 211 s at a message POST cost of 200 to 400 ms (measured from a third machine during the run) point at the tus
PATCH requests, whose duration is the client's upload time plus the inline medium thumbnail on a saturated box.
The seconds with a request p95 above 5 s had a lower message rate (10.9 against 19.9), which fits a saturated
uplink on the load machine. The VPS request log (`data/RequestLogs/2026-10-18.csv`, column 5 is the duration)
can confirm the paths.

**Five dropped writes.** `Send` broadcast the message and sent the unread events before it queued the message's
own write; a reaction from a nearby client landed in the queue ahead of the insert and failed its foreign key.
Fixed in ea61d31e: the write is queued first.

**The next server change is the read-state dirty set.** 6.3 million queued writes after 24 minutes, 1.6 GB of
memory and 17 gen 2 collections a minute are all that backlog. After it, the fan-out is the cost that remains.

**Host side of run 2** (`host-02.csv`, the plateau 19:15:36 to 19:33:34 UTC): box CPU 92 % mean and 99 % max,
Yap 271 % of 400 mean and 304 % max, so about one core went to everything outside the process (the kernel's
work for 16 MB/s out, 27 MB/s at peak, and 3,200 TCP connections). Load average 11.8, iowait 0.1 %, disk write
2.5 MB/s: the writer is bound by CPU in EF and SQLite, not by the disk. RSS 1,000 MB mean and 1,650 MB max, 56
threads at most. For five minutes after the users left, Yap still used 1.5 cores and the disk wrote 2.5 MB/s:
the backlog draining.

## Run 4 (23:21 to 23:28 local, previews off, main through 481663a8: all fan-out cuts, the coalesced writer, the presence fixes)

Files in `run4/`: the orchestrator's `summary.md`, `timeline.csv` and `log.txt` (run from `loadtest/runs/`), and
`server-load.csv`, the server's own one-second samples. The host CSV of this run is not in the folder yet.
The run went 1,000 → 1,250 → 1,500 → 1,750 → 2,000 with short holds; the 1,750 hold was 2.5 minutes.

| Level | Send to appear p50 / p95 / p99 | Yap CPU | Threads | Sweep | Hub sends/s | Writer max | Load | Errors |
|---|---|---|---|---|---|---|---|---|
| 1,000 | 78 / 122 / 138 ms | 43 % | 12 | 1.4 ms | 1,300 | 705 | 4 | 0 |
| 1,250 | 97 / 160 / 676 ms | 59 % | 12 | 14 ms | 2,800 | 2,185 | 8 | 0 |
| 1,500 | about 100 / 200 / 500 ms | 66 % | 19 | 48 ms | 3,300 | 2,570 | 12 | 0 |
| 1,750 | 153 / 328 / 627 ms | 68 % | 27 | 28 ms, 189 max | 3,500 | 1,434 | 17 | 125 upload timeouts |
| 2,000 | 277 / 614 / 1,000 ms | 71 % | 52 | 52 ms, 203 max | 2,400 | 1,239 | 21 | 82 upload timeouts |

**Against run 3 at the same levels.** At 1,000 the sweep went from 13 ms to 1.4 ms and the hub sends from 2,500
to 1,300 a second. At 1,500 the thread count is 19 instead of 63 and the sweep 48 ms instead of 70 to 486: the
presence lock is no longer what the threads wait for. The writer stays under 2,600 rows at every level, and the
working set reaches 709 MB at 2,000 instead of 1.6 GB at 1,000 in run 2.

**The ceiling of the 4-core box is between 1,500 and 2,000 users in one room at normal pace.** 1,500 runs with a
p95 of 200 ms. At 2,000 the p95 is 614 ms and the p99 one second, the load average is 21 on four cores, and the
threads climb again, this time from CPU starvation rather than a lock: the pool's queue median is 157 and the
sweep's 50 to 200 ms are wall time of a thread that waits for a core. The process takes 2.8 cores; the rest of
the box is the kernel's. Where the 2.8 cores go is the question for the trace round: the message fan-out of
30 messages a second to 2,000 connections, the 2,400 per-person unread calls, serialization, TLS, and a garbage
collector that runs 25 to 90 gen 2 collections a minute with 16 to 23 ms of pause per second.

**Upload timeouts at 1,750 and 2,000.** 207 image uploads passed the tool's 30 s limit; the server's request
maximum was 95 to 136 s. The medium thumbnail runs inside the upload request and waits for a core on a saturated
box. A queue for thumbnails with a concurrency limit, and the request answering before the thumbnail, would turn
the timeouts into a slower appearance of the picture.

**Host side of run 4** (`run4/host-04.csv`): the box reached 91 % at 1,500 and 98 % at 2,000. Yap took 1.4 cores at
1,000, 2.6 at 1,500 and 2.8 at 2,000; the share outside the process grew from 0.7 to 1.1 cores, which is the
kernel moving 39 MB/s out (310 Mbit/s) over 6,224 established connections at 2,000 users, about three per user
through the proxy. iowait stayed under 1 %, RSS 611 MB, 2,424 open files. The proxy-off round shows what the
same levels cost without that fan-in.

## Run 5, the trace round (2026-10-19 00:50 to 00:58 local, main through bc08f2fb)

Files in `run05/`: the orchestrator's results and `server-load.csv` as before, `host-05.csv`, two 30 s CPU traces
(`plateau-1500.nettrace`, `plateau-2000.nettrace`, dotnet-trace cpu-sampling) and `stacks-1750.txt` (dotnet-stack).
The levels were 1,500 (2 min 48 s), 1,750 (3 min 28 s) and 2,000 (1 min 19 s). The numbers match run 4: 1,500 at
a p95 of 186 ms, 2,000 at 600 ms with a load average of 24; the box at 92 %, 97 % and 99 %, Yap at 2.5 to 2.9
cores. 344 upload timeouts, most at 1,750 and 2,000.

**How to read the traces.** The sample profiler walks every managed thread each millisecond, running or not, and
the export marks a sample as CPU unless the top frame is a known idle wait. So the 377 s of "CPU" in a 30 s trace
on four cores are thread time, not core time: a thread blocked in `Monitor.Enter_Slowpath` behind a lock counts
as busy. The split below is where the non-idle threads were. The lock column is the share of a path that sat in
such a wait; the rest of the path is close to real work.

| Where the non-idle threads were | 1,500 | 2,000 | of it in a lock wait |
|---|---|---|---|
| Kestrel socket send, the bytes out | 24 % | 21 % | 5 to 7 % |
| Thread pool dispatch, between work items | 14 % | 15 % | 1 % |
| Typing broadcast (`ToWatching`) | 9 % | 15 % | 58 to 62 % |
| WebSocket transport, its own send loop | 9 % | 11 % | 76 % |
| Message broadcast (`ToChannel`) | 10 % | 10 % | 44 % |
| Thumbnails (ImageSharp, WebP) | 14 % | 8 % | 0 % |
| GC and finalizers | 6 % | 6 % | 0 % |
| Persistence writer | 6 % | 5 % | 0 % |
| The message POST itself | 4 % | 4 % | 21 % |
| Unread and presence tick | 2 % | 2 % | 96 % |
| Hub methods: markRead, open, heartbeat | 1 % | 2 % | 66 % |

**The lock is SignalR's write lock per connection.** `dotnet-stack` at 1,750 caught it in the act: the thread of a
message POST, inside `ChatService.Send`, goes through `StopTyping`, raises the typing change, and writes the typing
event to the watchers' connections itself; another thread writes the message broadcast to the same connections;
the tick thread writes the unread counts and the presence list; the transport's own loop writes too. Every write
takes the connection's `SemaphoreSlim`, and with four writers the semaphore's monitor is contended on every
connection. A broadcast to 2,000 connections then holds its thread for the whole walk, the thread pool grows from
19 to 80 threads at 2,000, and the message POST waits in its own `Send` for the writes it caused. That is the
mechanism behind the p95 climbing from 186 ms to 600 ms while the process stays under three cores.

**The three fixes this points at.**

1. **One sender.** All hub sends go through one queue drained by one loop, like the unread counts already do. The
   per-connection locks are never contended, the sending threads are freed, and `ChatService.Send` returns as
   soon as it has queued. The tick becomes the sender for everything; a message leaves within a few milliseconds.
2. **Typing through the tick.** Typing on and off per conversation batched like presence, at most four lists a
   second. At 2,000 users typing was 15 % of the non-idle thread time and the biggest Yap-side item.
3. **Thumbnails off the request, one or two at a time.** 8 to 14 % of the threads' time was WebP encoding of the
   test's uploads, inline in the upload request, on a box that had no core to spare. A bounded queue answers the
   upload at once and makes the picture appear when it is ready; the 344 timeouts become a short wait.

The socket sends and the GC are what the load costs: 40 MB/s out over 6,000 connections, and allocations the
next trace can name with the GC provider on.

## Run 6, the proxy off (2026-10-19 01:15 to 01:27 local, main at bc08f2fb, Caddy in front of Kestrel, no Cloudflare)

Files in `run06/`: the orchestrator's results, `host-06.csv`, `server-load.csv`. The ramp went 1,000 → 1,500 →
1,750 → 2,000 → 2,500 → 2,750 → 3,500 → 4,000 with holds of 30 to 60 s. The user typed and sent from a tab
at 3,000 users and called it the most responsive run; the virtual users tell a different story above 2,000.

| Level | Send to appear p50 / p95 | Box CPU | Yap | Outside Yap | TCP | Net out | Errors |
|---|---|---|---|---|---|---|---|
| 1,000 | 75 / 100 ms | 53 % | 1.6 cores | 0.6 cores | 2,457 | 19 MB/s | 0 |
| 1,500 | 100 / 200 ms | 95 % | 2.7 | 1.1 | 5,349 | 24 MB/s | 0 |
| 2,000 | 200 / 750 ms | 98 % | 2.9 | 1.0 | 6,555 | 42 MB/s | 52 |
| 2,500 | 1,500 / 3,000 ms | 94 % | 3.4 | 0.4 | 7,613 | 50 MB/s | 49 |
| 2,750 | 750 / 5,000 ms | 97 % | 3.3 | 0.6 | 8,336 | 65 MB/s | 69 |
| 4,000 asked | collapse: p95 48 s, 27,935 reconnects, 10,079 hub 502s | | | | | | |

**The proxy was not the cost.** Through Cloudflare the box saw about three TCP connections per user; through
Caddy it sees the same, because Caddy holds one connection to the client and one to Kestrel per WebSocket. The
kernel's share stayed about one core. Up to 2,000 the levels match runs 4 and 5 within the noise. The
responsiveness the user felt is the sidebar window from bc08f2fb, with one hop less on the user's own path.

**The orchestrator could not connect more than about 2,760 users**, and from 2,500 on the hub gave 502s. The
server's own samples show why, and it is the writer bug of this evening, not the proxy. At 01:19 (23:19 UTC) the
coalesced read-state flush began to fail its foreign keys by the hundred: a direct message's channel insert was
still in the queue when the read state that points at it was flushed, and a failing slice of 500 was redone as
500 single commits, the slow path on SQLite. From there the writer's depth climbed to 18,508, the thread pool sat
at 91 to 93 threads with a queue median of 2,000 to 4,500, the presence sweep took up to 7 s, the working set
1.6 GB, and the collector ran up to 900 gen 2 collections a minute. Kestrel could not answer in time, Caddy
answered 502 for it, and 2,201 read states were dropped in an hour. The status report from the VPS named the
cause; the fix is in the commit after this report: the flush waits for an empty queue, and a failed slice stays
whole for the next flush instead of being taken apart.

**What this run cannot say** is the ceiling without the proxy above 2,000, because the writer took the box down
first. Run 4 at 2,000 with the proxy on, p95 614 ms, stands as the comparison until a run with the fix.
