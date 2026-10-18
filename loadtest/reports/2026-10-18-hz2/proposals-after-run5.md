# Three changes the trace round points at

Written 2026-10-19 after run 5 (the traces at 1,500 and 2,000 users; the findings are in `report.md`, section
"Run 5"). None of the three is built. They are independent; the order below is the order of return.

## 1. One sender for all hub writes

**Finding.** Half of the time the broadcast paths spent at 2,000 users was a wait on SignalR's write lock, a
`SemaphoreSlim` per connection. Four writers contend it: the thread of a message POST (which writes the message
broadcast and, through `StopTyping`, the typing event to every watcher, inside `ChatService.Send`), the thread of
a `Typing` hub call, the broadcaster's 250 ms tick (unread counts, presence list), and the transport's own send
loop. A broadcast to 2,000 connections holds its thread for the whole walk, the thread pool grows from 19 to 80
threads, and the POST waits for the writes it caused. `dotnet-stack` at 1,750 shows the stacks (`run05/stacks-1750.txt`).

**Where.** `Yap/Endpoints/ChatHubBroadcaster.cs`: `Fire(Task send)`, the helpers `ToChannel`, `ToReaders`,
`ToViewers`, `ToWatching`, `ToUser`, `ToAll`, and the loop `SendBatchesAsync`.

**What.** The helpers stop calling `SendCoreAsync` themselves. Each one puts a record on a queue, a
`Channel<HubSend>` (unbounded, single reader) with the target and the arguments:

```csharp
private readonly record struct HubSend(IClientProxy Target, string Name, object?[] Args);
private readonly Channel<HubSend> _sends = Channel.CreateUnbounded<HubSend>(new UnboundedChannelOptions { SingleReader = true });
private void Fire(IClientProxy target, string name, object?[] args) => _sends.Writer.TryWrite(new HubSend(target, name, args));
```

One loop, started in `StartAsync` beside the tick, drains it and awaits each send before the next:

```csharp
private async Task SendLoopAsync(CancellationToken ct)
{
    await foreach (var s in _sends.Reader.ReadAllAsync(ct))
    {
        load.HubSend();
        try { await s.Target.SendCoreAsync(s.Name, s.Args, ct); }
        catch (Exception e) { log.LogWarning(e, "A hub send failed"); }
    }
}
```

The tick's own sends (unread per person, the presence list) go through the same queue, so there is one writer
left besides the transport. The order of events is the order of the queue: a message before its reactions, a
`dm` before the first message in it, as today, because the services raise them in that order on one thread.

**What it costs.** A single sender serializes the broadcasts. A send to 2,000 connections takes a few
milliseconds when no lock is contended, so the queue carries some hundreds of broadcasts a second; the test's
peak was about 40 messages, 70 typing events and 8 tick sends a second. If the queue ever falls behind, its
depth belongs on the load card next to the writer's.

**Expected.** The lock waits disappear from the traces, the pool stays near 20 threads at 2,000, and the message
POST returns in the time of its own work. The p95 at 2,000 should come down from 600 ms toward the 1,500 figure.

**Verify.** Run 4's ramp to 2,000 with a trace at 2,000: `Monitor.Enter_Slowpath` gone from the broadcast
paths; the thread count on the load card; the orchestrator's send-to-appear p95.

## 2. Typing through the tick

**Finding.** The typing broadcast was 15 % of the non-idle thread time at 2,000 users, the largest Yap-side item,
even after 279147f1 sent it only to the visible tabs with the conversation open. The composer repeats `typing on`
every 2 s while keys fall, so at 36 messages a second there are tens of typing events a second, each a send to
some 600 watchers.

**Where.** `Yap/Services/Presence/PresenceService.cs` (`SetTyping`, `StopTyping`, `Changes.AddTyping` and
`Raise`); `Yap/Endpoints/ChatHubBroadcaster.cs` (the `OnTypingChanged` subscription and `ToWatching`);
`Yap/wwwroot/js/hub.js` and `store.js` (`setTyping`); `docs/architecture/wire-contract.md`.

**What.** The broadcaster keeps the latest typing state per (conversation, person) that changed in the tick, and
the tick sends one `typings(channelId, [{ username, on }])` per conversation to its watchers, at most four a
second. The client applies the list in order with the existing 3.5 s expiry per typist. With proposal 1 the send
goes through the same queue. On the server, `SetTyping` could also drop a repeat `on` from a person already
typing in that conversation, if `Changes.AddTyping` does not already; then the composer's 2 s repeats cost nothing.

**What it costs.** The strip appears up to 250 ms later. The wire event changes; a tab on an old build shows no
typing until its next page load.

**Expected.** Typing from 15 % to a few per cent of the threads' time; at the client, one `typists` change per
tick instead of one per event.

## 3. Thumbnails off the request, one or two at a time

**Finding.** 8 % of the non-idle thread time at 2,000 and 14 % at 1,500 was ImageSharp encoding WebP thumbnails
of the test's uploads, inside the tus upload request, on a box with no core to spare. The orchestrator counted
344 upload timeouts (30 s) in run 5 and 207 in run 4; the server's request maximum was over 200 s. In run 3 the
user waited "very long" for an image upload with one tab.

**Where.** `Yap/Endpoints/TusEndpoints.cs`, the upload-complete handler around the line
`await imageService.GenerateMediumThumbnailAsync(filePath)`: the medium thumbnail is awaited inside the request,
and the large one runs in an unbounded `Task.Run`. `Yap/Services/Media/ImageService.cs` for the encoder settings.

**What.** A `ThumbnailQueue` hosted service: a `Channel<ThumbnailJob>` and a fixed number of workers, one on a
box with four cores or fewer, two above, each doing the medium and then the large thumbnail of one file. The
upload handler enqueues and returns. The client's existing poll of `/api/tus/info/{id}` keeps answering "still
processing" until the medium thumbnail exists, so the browser flow does not change; the server just never runs
more encoders than it has cores to spare, and never on a request thread. The queue depth goes on the load card.
Two smaller knobs beside it: the WebP encoder's `Method` (lower is faster, the default is slow) and, for the load
tool, a longer wait than 30 s when the queue is known to be long.

**What it costs.** Under a burst the picture appears later instead of the upload failing. Memory: one or two
images decoded at a time instead of as many as arrive.

**Expected.** No upload timeouts at 2,000 from encoding; the encoders' share of the threads' time bounded by the
worker count; the request maximum on the load card back to the network's time.

## Also seen, not proposed yet

- **GC**: 6 % of the threads' time in the collector and the finalizers, 25 to 90 gen 2 collections a minute at
  every level. The next trace with the GC provider on
  (`--providers Microsoft-Windows-DotNETRuntime:0x1:5` beside the sampling profile, or `--profile gc-verbose`)
  names the allocations.
- **The socket sends**, 21 to 24 %, are the bytes: 40 MB/s out over 6,000 connections at 2,000 users through
  the proxy. Run 6 without the proxy shows what the fan-in costs.
