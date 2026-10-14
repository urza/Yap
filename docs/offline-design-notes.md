# Offline chat: design notes for the next implementation

Written 2026-10-14. These notes guide whoever implements offline behaviour in the new chat client. They are not the product spec. The spec (`docs/spec/`, planned) says what the user gets. These notes say how to get there with the least machinery.

## The principle

Yap is a chat for friends. It is not a critical system and it does not guarantee correctness. A message that is lost once a month is acceptable. A design that adds a subsystem to prevent that loss is not.

The user's words: the chat, when offline, must be readable with what it already has. Queuing messages while offline is nice to have. If a queued message fails to send, that is okay. Discord queues messages while offline and still never delivers them after a long outage. That is the level to aim for.

Design for the common case: a short loss of connection on a phone, a tunnel, a lift, a switch between Wi-Fi and mobile data. Do not add machinery for the rare case.

## Two separate promises

1. **Reading.** The client shows what it already has. History comes from the server. There is no local copy of history that must be kept correct.
2. **Sending.** The client accepts a message while offline, shows it as pending, and sends it when the connection returns. This needs a queue, nothing more.

Keep the two apart in code. The queue never feeds the reading side; the server does.

## The outbox

One IndexedDB store, the outbox. One row per unsent message:

| Field | Meaning |
| --- | --- |
| `clientId` | A UUID the client makes at send time. The server dedupes by it. |
| `conversation` | Room id or DM partner. |
| `text` | The message text. |
| `createdAt` | When the user pressed Send. |
| `state` | `queued` or `failed`. |

Send appends a row and paints the pending message in the timeline. That is the whole write path. The pending row shows faded, in the place where it was typed, until the server accepts it.

## The sender

One loop, driven by events, never by a timer:

1. Take the oldest `queued` row.
2. POST it with its `clientId`.
3. On 2xx, delete the row. The live update from the server paints the real message.
4. On a network error, stop. Do nothing until a wake-up.
5. On a 4xx, set `state = failed` and stop trying that row.

Wake-ups: the `online` event, a SignalR reconnect, the tab becoming visible, a new row appended. No poll, no backoff table, no attempt counter, no persisted next-attempt time.

## Server side

The server dedupes by `clientId`. A bounded dictionary per user in memory, `clientId` to message id, entries expiring after a day. A repeated POST with a known `clientId` returns the existing message with 200. That is the whole idempotency story. The persistence copy of the dictionary follows the normal memory-first rule: the database is a copy, not the source.

## Reading

On connect, fetch the tail of the open conversation and the unread counts in one request. Live updates arrive as SignalR events, as before. On reconnect, fetch the tail again. Nothing to merge, no versions per record, no epoch, no digest, no reset.

If history must stay visible after a reload while offline, store the last fetched tail per conversation as one blob, replaced wholesale on each fetch. A snapshot cache. It has no merge logic because it is never partially updated.

## Tabs

Tabs do not merge each other's state. History comes from the server in every tab. The outbox is shared storage. A Web Lock makes one tab the sender:

```js
navigator.locks.request('yap-outbox-sender', () => runSenderUntilClosed());
```

Other tabs re-read the outbox when a BroadcastChannel message says it changed. That is the complete cross-tab design.

## After the tab was closed

The outbox is on disk. The next open tab sends. Where the browser has Background Sync, the service worker registers one sync tag and its handler reads the outbox and POSTs. That is optional and about thirty lines. The service worker is otherwise only for install and push, as before the rewrite.

## Failed and stale rows

A failed row shows Retry and Remove. Remove deletes the row and nothing else. No server call.

A row older than one day is marked `failed` when a tab opens. One comparison. The user removes it or retries it. This is the Discord behaviour and it is enough.

## What is not queued

Only new text messages are queued. Edits, deletes, reactions and uploads need a connection, and the UI says so when there is none. Queuing every operation is where the previous design grew its acceptance locks, its operation ids and its conflict rules. Not queuing them removes that whole class of code.

Attachments while offline are an open decision. If they are in, the file blob goes into the outbox row, with a size cap, and the sender uploads before it posts. If they are out, the + button says "needs a connection".

## What this design does not have

- No local replica of history, and so no per-record versions and no merge algorithm.
- No change tracker that numbers revisions, no digest, no overflow reset.
- No server epoch; a deploy changes nothing on the client.
- No retry policy; connectivity events are the retry.
- No operation queue; one row type, a new message.
- No second service worker and no feature probe.
- No locks on the server beyond the one ChatService already has.

## Size

The client side is a few hundred lines: the outbox store, the sender, the pending row, the lock. The server side is the dictionary and one POST handler. The previous implementation of the same promise was about 7,600 lines of client code.

## Open decisions

- Attachments in the offline promise, or text only.
- The stale row limit (one day proposed).
- The snapshot cache for reading after a reload, yes or no.
