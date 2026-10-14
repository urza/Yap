
# Presence Lock

Published copy: https://claude.ai/artifact/Wt4vmSnxXrC4psrDRapCfV

Yap server · Services/PresenceService.cs · 2026-10-13

One `SemaphoreSlim(1, 1)` guards every presence change on the server. This page shows where it is, what it protects, why it was needed, and how ChatService can take over that job so the lock can go.

## 1. The lock

A `SemaphoreSlim` with one slot is a lock. The difference from a `lock` block is one rule of C#: a `lock` block cannot contain `await`. When the protected code awaits, programmers use the semaphore instead. That is the only reason this one exists.

```csharp
// Services/PresenceService.cs:17
private readonly SemaphoreSlim gate = new(1, 1);

// the pattern every guarded method uses, here Typing (:100-120)
await gate.WaitAsync();
try
{
    var session = Require(connection, user);       // read the per-tab note
    ...                                            // rewrite the note
    await chat.StartTypingAsync(channelId, user.Username);   // call ChatService
}
finally { gate.Release(); }
```

Five methods take the lock: `Join`, `Report`, `Typing`, `Leave` and `Close`. Three do not: `SetStatus`, `Watch` and `Tick`. There is one instance for the whole server. Every tab of every user waits in the same line.

## 2. Where PresenceService sits

PresenceService is the adapter between hub connections and ChatService sessions. Each browser tab is one SignalR connection. ChatService knows that tab as the session `"chat:" + connectionId`. PresenceService translates hub calls into ChatService calls, and every 100 ms it builds the presence view each tab watches.

> Diagram: **Two locks in a row.** PresenceService holds its gate while it calls ChatService, and ChatService takes its own lock inside. The dashed path is the return stream: Tick builds a view per tab and the hub sends only the fields that changed.
>
> Alt text: A browser tab talks to ChatHub over SignalR. ChatHub calls PresenceService, which takes its gate and then calls ChatService, which takes its own lock. PresenceTicker calls PresenceService.Tick every 100 ms and the changed views stream back to the tab.
>
> Labels: Browser tab | live.js | one SignalR connection | send, no reply | ChatHub | Report, Typing, SetStatus | WatchActivity stream | Join, Report, Typing, Leave | PresenceService | gate (1 slot) | sessions: note per tab | listeners, lastViews | reports (250 ms debounce) | AddUserAsync | ConnectionUp | ReportClientState | Start/StopTyping | all return Task.CompletedTask | ChatService | _connectionGate | UserSession per tab | typing per channel | status per user | PresenceTicker | Tick() every 100 ms | changed PresenceView fields, streamed back

ChatHub.cs:28-36 (Join, Report, SetStatus, Typing), :53 (OnDisconnectedAsync → Leave), :101-105 (WatchActivity → Join, Report) · ChatEndpoints.cs:122-126 (POST /presence/leave → Close) · PresenceService.cs:144 (Tick), :131 (Watch) · live.js:13-18 (`send`, not `invoke`)

## 3. The per-tab note

This is the whole state PresenceService keeps about a tab, beyond stream plumbing:

```csharp
// Services/PresenceService.cs:26
private record Session(Guid UserId, string Username, Guid? Channel = null, DateTime TypingUntil = default);
```

Two fields carry information. Both have a twin in ChatService.

| Field in the note | Meaning | Where ChatService already has it |
| --- | --- | --- |
| `Channel` | The conversation this tab has open. | `UserSession.ViewingChannel` (a label, "#lobby" or "DM: alice", :83) and `UserSession.ReadingChannelId` (the id, set only once the window is loaded and validated, :84). PresenceService writes both through `SetSessionViewing` (:713) and then also keeps the id in its own note. |
| `TypingUntil` | The time until which this tab's user counts as typing. | `_channelTypingUsers` (:40): channel id → username → time of the last keystroke. Entries older than `TypingTimeoutMs` (3 s by default) are dropped on read (:1479-1504). Keyed by *user*, not by tab. |

The key difference is the last cell. ChatService knows that *alice* is typing in the lobby. It does not know *which of alice's tabs* is typing. PresenceService keeps `TypingUntil` per tab to answer exactly that question.

## 4. Why the lock is there

Four paths lead into one tab's note. SignalR runs the direct hub calls of one connection one at a time. It does not serialize the other three paths against them.

> Diagram: **What the gate orders.** Without it, the stream body's Join can overlap a direct Report, a Leave can overlap a Typing, and a Close from the HTTP side can overlap anything.
>
> Alt text: Four entry paths, the WatchActivity stream body, direct hub calls, OnDisconnectedAsync and the HTTP leave request, all pass through the one gate before they rewrite the per-tab note and call ChatService.
>
> Labels: WatchActivity stream body | Join, then the first Report, in its own task | Direct hub calls | Report, Typing: one at a time per connection | OnDisconnectedAsync | Leave: runs when the socket drops | POST /api/chat/presence/leave | Close: an ordinary HTTP request, any thread | gate · one at a time · all tabs | rewrite | Note for tab T | Channel ← Report | TypingUntil ← Typing, Stop | read by Stop for the user's other tabs | call | ChatService | AddUserAsync, ConnectionUp/Down, ReportClientStateAsync, Start/StopTypingAsync | own lock inside; calls complete at once

Each of the four reasons maps to a line in the code.

1. **Join before the first Report.** `Join` (:30) writes the note and then registers the session in ChatService. `Require` (:51) throws when the ChatService session is missing. A Report that arrives between the two lines fails. The gate makes it wait.
2. **Atomic rewrites.** Each change is a read, a `with`, and a write of the same record. `Report` sets `Channel` (:66), `Typing` sets `TypingUntil` (:100), `Stop` clears it (:57). Two interleaved rewrites lose one.
3. **The other tabs of the same user.** `Stop` reads the notes of the user's other tabs before it clears ChatService's typing entry. `Close` does the reverse in `RestoreSiblingTyping` (:215). The gate gives both a stable view.
4. **Syntax.** The methods await ChatService, so a `lock` block does not compile around them.

> Diagram: **The one case the copy exists for.** ChatService keys typing by username, so tab A's stop would erase tab B's typing. PresenceService avoids that by keeping a time per tab and asking the other tabs first. That question is what the gate protects.
>
> Alt text: Alice has two tabs in the lobby. Tab A stops typing. Stop reads tab B's note, sees B still typing, and keeps alice's single typing entry in ChatService, because that entry is keyed by username and shared by both tabs.
>
> Labels: alice · tab A | in #lobby, stops typing | sends Typing(lobby, false) | Typing(false) | alice · tab B | in #lobby, still typing | TypingUntil in 2 s | Typing(true) | PresenceService notes | A: Channel=lobby TypingUntil=cleared | B: Channel=lobby TypingUntil=+2 s | Stop: "is another tab of alice still typing in #lobby?" → yes | StopTypingAsync not called | ChatService typing | #lobby → { alice: 12:00:01 } | one entry for both tabs

## 5. What the lock does not buy

The reasons above are real. The shape of the lock does not match them.

- **Nothing under the gate waits.** Every ChatService method awaited inside returns `Task.CompletedTask`: `AddUserAsync` (:516), `ConnectionUp` and `ConnectionDown` (Connections.cs:77, :90), `ReportClientStateAsync` (:637), `StartTypingAsync` and `StopTypingAsync` (:1458, :1468), `SetUserStatusAsync` (:560). Each takes `_connectionGate` inside and returns. The semaphore protects code that never suspends. It is a second lock over the first.
- **It is one lock for the server.** The data it protects is per tab and per user. Two users' notes never touch, yet every Report (one per tab every 10 s, 60 s when hidden) and every Typing tick (every 1.5 s while typing) from every user waits in one line.
- **It is held across notifications.** ChatService runs the notifications it collected after it releases its own lock. Our gate is still held then. A slow handler stalls presence for everyone.
- **Its scope is accidental.** `SetStatus` (:91) reads the note and calls ChatService without the gate. If the gate guarded an invariant, it would be inside.
- **The failures it prevents heal on their own.** An early Report is a `send` with no reply; the stream body sends the same values a moment later and the client repeats within 10 s. A wrong typing decision lasts until the 3 s expiry.

> The lock is a patch over a design choice. Two services keep per-tab presence state, so a second lock was needed to keep the copy in step with the original. Remove the copy, and the lock has nothing left to protect.

## 6. How to remove it

> Diagram: **What changes.** The per-tab note moves into the record ChatService already keeps per session, and typing becomes a per-session entry that `GetTypingUsers` collapses to distinct usernames. The semaphore has nothing left to guard.
>
> Alt text: Before: PresenceService keeps a per-tab note with Channel and TypingUntil behind its gate and ChatService keeps viewing fields and typing by username behind its own lock. After: ChatService keeps the viewing channel id and typing per session behind its one lock; PresenceService keeps only tickets, listeners and the debounce, with no gate.
>
> Labels: BEFORE | PresenceService | gate | Session per tab | Channel | TypingUntil | tickets, listeners, lastViews, reports | asks other tabs before Stop | copy in step | ChatService | _connectionGate | UserSession per tab | ViewingChannel | ReadingChannelId | typing: channel → username → time | Two owners of the same facts, one lock each, and a cross-tab question in between. | AFTER | PresenceService | no gate | ~~Session per tab~~ | tickets, listeners, lastViews, reports | Stop removes one session's entry; | one owner | ChatService | _connectionGate | UserSession per tab | ViewingChannelId | ReadingChannelId | typing: channel → session → (user, time) | GetTypingUsers: distinct users | One owner. The cross-tab question disappears because the data is keyed per session.

### Option A **recommended**

**ChatService owns per-tab presence state.** Three changes, in this order.

1. Add `ViewingChannelId` to `UserSession`. `SetSessionViewing` sets it always; `ReadingChannelId` keeps its narrower rule.
2. Key typing by session. `StartTypingAsync(channelId, sessionId, username)` and `StopTypingAsync(channelId, sessionId)` touch one entry. `GetTypingUsers` drops expired entries and returns distinct usernames. Sending a message clears every entry of that username in that channel, as today.
3. Delete the note. `Require` asks ChatService for the session and checks its `UserId`. `Stop` and `RestoreSiblingTyping` go. `Join` stays idempotent through `chat.HasSession`. The gate goes.

One behavior stays open: a Report that arrives before Join has finished. `Require` returns instead of throwing for the `send` calls, and the stream body's own Report carries the same values a moment later.

### Option B **small**

**Keep the copy, drop the gate.**

1. `Join` uses `sessions.TryAdd`.
2. Field updates use `sessions.AddOrUpdate(connection, …, (_, s) => s with { TypingUntil = … })`, so each rewrite is atomic.
3. `Stop` reads a snapshot of the other tabs. A wrong answer leaves a typing indicator for at most 3 s.

Cheaper, and it keeps the duplicate state and the cross-tab question. Choose it only if ChatService must not change now.

### Option A, sketched

```csharp
// ChatService: typing per session, read per user
private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, (string Username, DateTime At)>> _channelTypingUsers = new();

public Task StartTypingAsync(Guid channelId, string sessionId, string username)
{
    if (_channelTypingUsers.TryGetValue(channelId, out var typing))
        typing[sessionId] = (username, DateTime.UtcNow);
    return Task.CompletedTask;
}
public Task StopTypingAsync(Guid channelId, string sessionId)
{
    if (_channelTypingUsers.TryGetValue(channelId, out var typing))
        typing.TryRemove(sessionId, out _);          // only this tab; a sibling tab keeps the user typing
    return Task.CompletedTask;
}
public List<string> GetTypingUsers(Guid channelId)
    => /* drop entries older than TypingTimeoutMs, then */ .Select(e => e.Username).Distinct().ToList();

// PresenceService.Typing: no note, no gate
public Task Typing(string connection, User user, Guid channelId, bool active)
{
    var session = Require(connection, user);         // ChatService.UserSession, or return for a send call
    var channel = chat.GetChannel(channelId);
    if (session.ViewingChannelId != channelId || channel?.CanAccess(user.Id) != true || !channel.CanWrite(user.Id, chat.IsAdmin(user.Id)))
        throw new InvalidOperationException("Conversation unavailable for typing");
    return active && chat.IsSessionPageVisible(Key(connection))
        ? chat.StartTypingAsync(channelId, Key(connection), user.Username)
        : chat.StopTypingAsync(channelId, Key(connection));
}
```

Sketch, not a diff. `PublishMessageAsync` (ChatService.cs:960) and `Tick` (PresenceService.cs:144) read typing by channel and keep working with the new shape.

## 7. How to check the change

- **Contract harness**, both modes: `npm run test:contracts`. `PresenceChecks`, `ConnectionChecks` and `DesignChecks` drive `PresenceService` directly and through the hub.
- **Browser suites**: `presence-lifecycle`, `presence-unread`, `tab-notifications`. These open several tabs and accounts and watch typing and read checkpoints.
- **By hand**: one account, two tabs in the same room. Type in both, stop in one. The indicator must stay for the other tab and must vanish 3 s after the last keystroke.

> **Verdict.** The semaphore is correct code written against the wrong boundary. It serializes the whole server to protect two fields that belong in a record ChatService already keeps per session.
>
> Option A removes the fields, the cross-tab question and the lock together. The methods keep their `Task` signatures, so the hub does not change.
