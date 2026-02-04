# Message Send Flow Analysis

## Complete Flow: Alice uploads an image to #lobby

**Scenario**: 3 users online — Alice (sender), Bob, Charlie. All viewing #lobby.

Each user has their own **Blazor circuit** — a persistent SignalR WebSocket connection between their browser and the server. Each circuit has its own **synchronization context** (think: a single-threaded queue of work for that circuit). All UI rendering and event handling for a user runs on their circuit's sync context.

`ChatService` is a **singleton** — one instance shared across all circuits. Its events (`OnMessageReceived`, etc.) have handlers from all circuits attached.

**At this point, the event subscriber lists look like:**
- `OnMessageReceived`: 3 handlers (Alice's RoomChat, Bob's RoomChat, Charlie's RoomChat)
- `OnTypingUsersChanged`: 3 handlers (Alice's MessageInput, Bob's MessageInput, Charlie's MessageInput)
- `OnUnreadChanged`: 6 handlers (each user's ChatHeader + ChatSidebar)

---

### STEP 1 — Alice selects files (browser → server)

Alice picks images. The browser fires `InputFile.OnChange`, which travels over Alice's SignalR connection to the server. Blazor dispatches it onto **Alice's sync context**, calling:

**`MessageInput.razor:383` — `HandleFileSelected`**
```csharp
private async Task HandleFileSelected(InputFileChangeEventArgs e)
```

Everything from here runs on **Alice's circuit**. Alice's UI is locked to this async chain — her circuit can't process other events (clicks, typing) until this method yields back.

---

### STEP 2 — Show upload indicator (server → Alice's browser)

**`MessageInput.razor:394-396`**
```csharp
isUploading = true;
uploadingCount = fileCount;
StateHasChanged();
```

`StateHasChanged()` marks Alice's component tree dirty. The render happens when we next `await` (Blazor batches renders). **Only Alice's circuit** — Bob and Charlie are unaffected.

---

### STEP 3 — Upload files (Alice's browser → server)

**`MessageInput.razor:401`**
```csharp
var result = await JS.InvokeAsync<UploadResult>("uploadFilesParallel", FileInputId);
```

This is a **JS interop call**: the server sends a message over Alice's SignalR connection asking her browser to run `uploadFilesParallel`. Her browser uploads the files via HTTP POST to the server, then returns the result back over SignalR.

During this `await`, Alice's sync context is free — Blazor renders the upload indicator to Alice's browser (the dirty state from step 2). Bob and Charlie are doing their own thing on their own circuits.

---

### STEP 4 — Generate medium thumbnails (server only)

**`MessageInput.razor:415-420`**
```csharp
await Parallel.ForEachAsync(filePaths, new ParallelOptions { MaxDegreeOfParallelism = 4 },
    async (filePath, _) =>
    {
        await ImageService.GenerateMediumThumbnailAsync(filePath);
    });
```

Pure server-side work. `Parallel.ForEachAsync` uses **thread pool threads** (not Alice's sync context). Alice's circuit awaits the result. No other circuits are involved.

---

### STEP 5 — Hide upload indicator (server → Alice's browser)

**`MessageInput.razor:426-428`** *(the fix — previously this was in the `finally` block after steps 6-10)*
```csharp
uploadingCount = 0;
isUploading = false;
StateHasChanged();
```

Marks Alice's component dirty. The actual render to Alice's browser happens at the next `await`. Previously this was in the `finally` block, meaning it was blocked behind all the event dispatch in steps 6-10.

---

### STEP 6 — Enter `SendMessageAsync` (server, singleton ChatService)

**`MessageInput.razor:433`**
```csharp
await ChatService.SendMessageAsync(channelId.Value, UserId, Username, "", imageUrls);
```

Alice's circuit calls into the singleton. We're still on **Alice's async chain** — her circuit is awaiting this.

**`ChatService.cs:556-564`** — Create message and add to in-memory store:
```csharp
var message = new ChatMessage(channelId, userId, username, content, DateTime.UtcNow, imageUrls);

lock (GetChannelLock(channelId))
{
    messages.Add(message);
}
```

The `lock` is brief — just adding to a `List<T>`. This runs on a thread pool thread (after the `await` in step 5 returned us here). The message is now in memory — any new `GetMessages()` call from any circuit would include it.

---

### STEP 7 — Persist to database (server)

**`ChatService.cs:567`**
```csharp
await _persistence.PersistNewMessageAsync(message);
```

SQLite/Postgres write. Server-side I/O. Alice's chain waits. No circuits are affected.

---

### STEP 8 — Clear typing indicator (server → all circuits)

**`ChatService.cs:570-571`**
```csharp
if (_channelTypingUsers.TryGetValue(channelId, out var typingUsers) && typingUsers.TryRemove(username, out _))
    await InvokeParallelAsync(OnTypingUsersChanged, channelId);
```

If Alice was typing, this fires `OnTypingUsersChanged`. `InvokeParallelAsync` does the following:

**`ChatService.cs:86`** — snapshot the handler list:
```csharp
var handlers = eventDelegate.GetInvocationList().Cast<Func<T, Task>>().ToList();
```

Gets 3 handlers: Alice's `MessageInput.HandleTypingUsersChanged`, Bob's, Charlie's.

**`ChatService.cs:91-113`** — run ALL handlers in parallel:
```csharp
var tasks = handlers.Select(async handler =>
{
    var handlerTask = handler(arg);
    var completedTask = await Task.WhenAny(handlerTask, Task.Delay(HandlerTimeout)); // 5s timeout
    ...
});
```

Each `handler(arg)` call dispatches work to that circuit's sync context. For example, Bob's handler:

**`MessageInput.razor:249-258`**
```csharp
private async Task HandleTypingUsersChanged(Guid changedChannelId)
{
    await InvokeAsync(() =>       // ← marshals to Bob's sync context
    {
        UpdateTypingIndicator();
        StateHasChanged();         // ← marks Bob's component dirty
    });
}
```

`InvokeAsync` queues a work item onto **Bob's sync context**. Once Bob's circuit processes it, the `Task` returned by `InvokeAsync` completes. The handler returns, and the parallel task for Bob is done.

**`ChatService.cs:115`** — wait for all:
```csharp
await Task.WhenAll(tasks);
```

Alice's chain is paused here until all 3 handlers complete (or timeout at 5s). The wall-clock time is `max(Alice's handler, Bob's handler, Charlie's handler)`.

**What could make a handler slow?** If Bob's circuit is disconnected, `InvokeAsync` dispatches to a dead sync context. In modern .NET, this generally completes quickly or throws. But if Bob's circuit is alive but his SignalR connection is slow (bad mobile network), the sync context might have a backlog — the work item sits in the queue until Bob's circuit gets around to it.

---

### STEP 9 — Notify all circuits of new message (server → all circuits)

**`ChatService.cs:573`**
```csharp
await InvokeParallelAsync(OnMessageReceived, message);
```

Same pattern. Gets 3 handlers. Runs them all in parallel. **This is the heaviest step.** Let's trace one handler in detail — Bob's:

**`RoomChat.razor:126-144`** — Bob's `HandleMessageReceived`:
```csharp
private async Task HandleMessageReceived(ChatMessage message)
{
    await InvokeAsync(async () =>                    // ← queue onto Bob's sync context
    {
        if (message.ChannelId == channelId)          // Bob is viewing #lobby, so true
        {
            messages.Add(message);                   // (a) add to Bob's local message list

            await ChatService.MarkChannelAsReadAsync(UserId, channelId);  // (b) NESTED call back into singleton!

            StateHasChanged();                       // (c) mark Bob's component dirty

            await ScrollToBottomAsync();             // (d) JS interop → Bob's browser
        }

        if (message.Username != Username && ...)     // Alice != Bob, so true
        {
            await HandleNewMessageNotificationAsync(message.Username, isDM: false);  // (e) more JS interop
        }
    });
}
```

Let's trace each sub-step inside Bob's handler:

**(a)** `messages.Add(message)` — Pure memory, instant. This is Bob's local `List<ChatMessage>`, separate from the singleton's.

**(b)** `ChatService.MarkChannelAsReadAsync(UserId, channelId)` — **This is a nested call back into the singleton from Bob's handler:**

**`ChatService.cs:758-789`**:
```csharp
public async Task MarkChannelAsReadAsync(Guid userId, Guid channelId)
{
    // ... update in-memory read state ...
    await _persistence.PersistReadStateAsync(state);    // DB write

    if (hadUnread)
    {
        await InvokeParallelAsync(OnUnreadChanged, userId, channelId);  // ← NESTED event dispatch!
    }
}
```

So Bob's handler fires **another** event dispatch. `OnUnreadChanged` has 6 handlers (3 ChatHeaders + 3 ChatSidebars). All 6 run in parallel. Each one does:

**`ChatHeader.razor:328-339`** — e.g., Bob's ChatHeader:
```csharp
private async Task HandleUnreadChanged(Guid userId, Guid channelId)
{
    if (userId == UserState.UserId.Value)     // only if this is for Bob
    {
        await InvokeAsync(async () =>         // ← Bob's sync context (already on it!)
        {
            await RefreshUnreadCountAsync();  // reads from memory + JS interop
            StateHasChanged();
        });
    }
}
```

`RefreshUnreadCountAsync` at **`ChatHeader.razor:274-303`** does:
```csharp
unreadCount = ChatService.GetTotalUnreadDMCount(...);  // memory read, fast
await UpdateBadgeAsync();                               // JS interop → Bob's browser
```

**`ChatHeader.razor:306-316`**:
```csharp
private async Task UpdateBadgeAsync()
{
    await JS.InvokeVoidAsync("setAppBadge", unreadCount);  // SignalR → Bob's browser → PWA badge
}
```

And **`ChatSidebar.razor:215-222`** — Bob's ChatSidebar:
```csharp
private async Task HandleUnreadChanged(Guid userId, Guid channelId)
{
    if (userId == UserId)
    {
        await InvokeAsync(StateHasChanged);  // ← just re-render sidebar
    }
}
```

The `OnUnreadChanged` for Bob's userId fires for all 6 handlers (Alice's header, Alice's sidebar, Bob's header, Bob's sidebar, Charlie's header, Charlie's sidebar). But 4 of them check `userId != myUserId` and bail immediately. Only Bob's 2 handlers (header + sidebar) do real work.

**Back to Bob's `HandleMessageReceived`:**

**(c)** `StateHasChanged()` — marks Bob's RoomChat dirty. Render queued.

**(d)** `ScrollToBottomAsync()` at **`ChatBase.cs:133-137`**:
```csharp
protected async Task ScrollToBottomAsync()
{
    try { await JS.InvokeVoidAsync("scrollToBottom"); }
    catch (Exception ex) { ... }
}
```

JS interop call: server sends `scrollToBottom` over **Bob's SignalR connection** to Bob's browser. The `await` waits for Bob's browser to execute it and send back the acknowledgment. If Bob's connection is slow, this wait is slow.

**(e)** `HandleNewMessageNotificationAsync` at **`ChatBase.cs:412-433`**:
```csharp
protected async Task HandleNewMessageNotificationAsync(...)
{
    var isVisible = await JS.InvokeAsync<bool>("isPageVisible");  // JS interop → Bob's browser
    if (!isVisible)
    {
        await JS.InvokeVoidAsync("notifyNewMessage", title);      // another JS interop
    }
}
```

One or two more JS interop round-trips to Bob's browser.

**Summary of Bob's single `HandleMessageReceived` execution:**
1. Add message to list (instant)
2. `MarkChannelAsReadAsync` → DB write + `InvokeParallelAsync(OnUnreadChanged)` with 6 handlers → Bob's ChatHeader does JS interop (`setAppBadge`) + Bob's ChatSidebar re-renders
3. `StateHasChanged` (instant)
4. `scrollToBottom` — JS interop round-trip to Bob's browser
5. `isPageVisible` — JS interop round-trip to Bob's browser
6. Possibly `notifyNewMessage` — another JS interop round-trip

That's **3-4 JS interop round-trips** to Bob's browser, nested inside Bob's handler.

**Charlie's handler is identical.** It runs **in parallel** with Bob's, on Charlie's sync context with JS interop to Charlie's browser.

**Alice's handler also runs in parallel**, but since `message.Username == Username`, step (e) is skipped. She still does steps (a)-(d).

**`ChatService.cs:115`** — `await Task.WhenAll(tasks)` waits for the slowest of the three:
```
Alice's handler: ~2-3 JS interop round-trips
Bob's handler:   ~3-4 JS interop round-trips
Charlie's handler: ~3-4 JS interop round-trips
```

Wall-clock time = max of the three. On a healthy connection, each JS interop is maybe 1-5ms (local network) to 50-100ms (mobile). So this step could be 5ms to 400ms depending on the slowest client.

**Who is blocked?** Alice's circuit is awaiting `SendMessageAsync`, which is awaiting `InvokeParallelAsync`, which is awaiting `Task.WhenAll`. **Alice can't click anything, type, or interact** until the slowest handler finishes. Bob and Charlie are NOT blocked — their handlers run on their own circuits, and once done, they're free.

---

### STEP 10 — Increment unread counts (server → all circuits, SEQUENTIALLY)

**`ChatService.cs:576`**
```csharp
await IncrementUnreadAsync(channelId, userId);
```

**`ChatService.cs:795-847`**:
```csharp
private async Task IncrementUnreadAsync(Guid channelId, Guid senderUserId)
{
    // For rooms: get all other users
    userIdsToIncrement = _users.Values
        .Where(s => s.UserId != senderUserId)   // Bob and Charlie
        .Select(s => s.UserId)
        .ToList();

    // Update in-memory (fast loop)
    foreach (var userId in userIdsToIncrement)
    {
        // state.UnreadCount++
    }

    // Single DB call
    await _persistence.IncrementUnreadForUsersAsync(channelId, userIdsToIncrement);

    // Notify SEQUENTIALLY - one user at a time!
    foreach (var userId in userIdsToIncrement)           // ← Bob, then Charlie
    {
        await InvokeParallelAsync(OnUnreadChanged, userId, channelId);  // ← awaited each iteration!
    }
}
```

**Iteration 1: Bob's userId.** `InvokeParallelAsync(OnUnreadChanged, bobId, channelId)` fires all 6 handlers. Most bail early (`userId != myUserId`). Bob's ChatHeader and ChatSidebar do work (JS interop for badge, re-render). Alice waits for all 6 to complete.

**Iteration 2: Charlie's userId.** Same thing. Alice waits again.

This is **sequential per user**. With N users, it's N iterations, each awaiting all `OnUnreadChanged` handlers. Even though most handlers bail early, the overhead of dispatching to 6 sync contexts and awaiting them adds up.

---

### STEP 11 — Push notification (fire-and-forget)

**`ChatService.cs:579-586`**:
```csharp
if (channel.IsDirectMessage)
{
    _ = _pushService.SendDmNotificationAsync(...);  // fire and forget
}
```

This is a DM-only step. For room messages, it's skipped. The `_ =` means it's fire-and-forget — Alice doesn't await it.

---

### STEP 12 — `SendMessageAsync` returns

Control returns to **`MessageInput.razor:433`**. Alice's circuit is now free. The render from step 5 (hiding the upload indicator) was already sent to Alice's browser when we hit the first await in step 6. The render from Alice's own `HandleMessageReceived` (step 9, showing the message) gets flushed now.

---

## Visual timeline

```
Alice's Circuit         Server (ChatService)          Bob's Circuit          Charlie's Circuit
──────────────         ─────────────────────          ─────────────          ────────────────
HandleFileSelected
  │
  ├─ isUploading=true
  ├─ StateHasChanged ──────────────────────────────── (idle)                (idle)
  │
  ├─ await JS upload ──► browser uploads via HTTP
  │   (Alice UI free, indicator renders)
  │◄── upload result ─┘
  │
  ├─ await thumbnails (thread pool)
  │
  ├─ isUploading=false
  ├─ StateHasChanged (queued)
  │
  ├─ await SendMessageAsync ──► lock { messages.Add }
  │  (indicator render             │
  │   flushes here)                ├─ await DB persist
  │                                │
  │                                ├─ await InvokeParallel(OnTypingUsersChanged)
  │                                │   ├──────────────────► HandleTyping         HandleTyping
  │                                │   │                    InvokeAsync           InvokeAsync
  │                                │   │◄───────────────── done                  done
  │                                │
  │                                ├─ await InvokeParallel(OnMessageReceived)
  │                                │   │
  │                                │   ├─ Alice's handler   Bob's handler         Charlie's handler
  │                                │   │  (own circuit)     InvokeAsync(          InvokeAsync(
  │                                │   │  messages.Add        messages.Add          messages.Add
  │                                │   │  MarkAsRead           MarkAsRead            MarkAsRead
  │                                │   │    └─DB write           └─DB write            └─DB write
  │                                │   │    └─OnUnread(6h)       └─OnUnread(6h)        └─OnUnread(6h)
  │                                │   │  StateHasChanged      StateHasChanged       StateHasChanged
  │                                │   │  scrollToBottom──►    scrollToBottom──►     scrollToBottom──►
  │                                │   │  ◄── ack              ◄── ack               ◄── ack
  │                                │   │  isPageVisible──►     isPageVisible──►      isPageVisible──►
  │                                │   │  ◄── ack              ◄── ack               ◄── ack
  │                                │   │  done)                done)                  done)
  │                                │   │
  │                                │   ├─ Task.WhenAll ─── waits for SLOWEST of 3
  │                                │   │◄─ all done
  │                                │
  │                                ├─ await IncrementUnreadAsync
  │                                │   ├─ memory updates (fast)
  │                                │   ├─ await DB increment
  │                                │   │
  │                                │   ├─ foreach Bob:
  │                                │   │   await InvokeParallel(OnUnreadChanged, bob)
  │                                │   │     6 handlers, 4 bail, Bob's Header+Sidebar work
  │                                │   │     Bob's Header: setAppBadge JS interop ──►◄──
  │                                │   │     done
  │                                │   │
  │                                │   ├─ foreach Charlie:        (SEQUENTIAL - waits for Bob first!)
  │                                │   │   await InvokeParallel(OnUnreadChanged, charlie)
  │                                │   │     6 handlers, 4 bail, Charlie's Header+Sidebar work
  │                                │   │     Charlie's Header: setAppBadge JS interop ──►◄──
  │                                │   │     done
  │                                │   │
  │                                │   └─ return
  │                                │
  │                                └─ return
  │◄── SendMessageAsync done ──────┘
  │
  ├─ fire-and-forget large thumbnails
  └─ method complete (Alice's circuit is free)
```

---

## Key takeaways

**Who is blocked and when:**
- **Alice's circuit** is blocked from step 6 through step 12. She can't interact with the UI during this entire chain. The wall-clock time is: DB persist + typing event dispatch + message event dispatch (bounded by slowest circuit's 3-4 JS interop round-trips) + N sequential unread dispatches.
- **Bob and Charlie's circuits** are NOT blocked. Their handlers are invoked asynchronously on their own sync contexts. Once their handler finishes, they're free. They don't wait for each other or for Alice.

**What makes it slow:**
- Each handler involves **JS interop round-trips** to that user's browser. Each round-trip is a SignalR message to the browser + execution + response back. Healthy connection: 1-5ms each. Slow mobile: 50-100ms each.
- `IncrementUnreadAsync` loops through users **sequentially**. With 20 users, that's 20 awaited event dispatches, one after the other.
- Inside `HandleMessageReceived`, `MarkChannelAsReadAsync` does a **nested event dispatch** (`OnUnreadChanged`), adding more JS interop round-trips nested inside the already-awaited outer dispatch.
- If a circuit is truly disconnected, JS interop throws `JSDisconnectedException` quickly. But if it's *alive but slow* (poor connection, phone in background), the interop completes but slowly — and the 5-second timeout at `ChatService.cs:96` is the worst case per handler.
