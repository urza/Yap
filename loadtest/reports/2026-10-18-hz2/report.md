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
