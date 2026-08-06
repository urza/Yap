# Item 3: Typing → Change-Granularity Bind + JS Typing Tracker

*Item 3 of the input-locality roadmap (`docs/input-locality-analysis.md`). 2026-08-06. Status: IMPLEMENTED — awaiting build + manual test (checklist at bottom).*

## What changed conceptually

The message box was `@bind:event="oninput"`: every keystroke dispatched an RPC and produced a render batch needing an ACK. On a slow link a fast typist outran the 10-unacked-batch cap, stalling the circuit's renders (incoming messages) and queueing the send click behind the keystroke backlog. Now the box is **plain `@bind` (change granularity)**: typing sends **nothing**. The server's draft copy syncs exactly when it matters — a synthetic `change` dispatched by the send pipeline right before the click's RPC — and the typing indicator is driven by a client-side tracker at ~2 dispatches per burst.

## The moving pieces

### MessageInput.razor
- Textarea: `@bind="messageText"` only; `@bind:after`/`OnInputChangedInternal`, `typingCts`, and `StopTypingAfterDelayAsync` deleted — the 3s timeout lives in JS now.
- New JSInvokables `ReportTypingStarted`/`ReportTypingStopped` delegate to the existing `StartTypingAsync`/`StopTypingAsync`, which keep their `isTyping` idempotence guard for the other callers (HandleSend's stop, DisposeAsync's cleanup, channel-switch stop).
- `setupTypingTracker(TextareaId, _layoutRef)` added to the element-setup batch (re-wired after ReadOnly flips like the rest).
- **Bonus fix (pre-existing hole):** `OnParametersSet` now stops typing in the channel being *left* when `channelId` changes mid-burst — previously the "X is typing" entry lingered in the old channel (the 3s timeout fired against the *new* channelId).

### chat.js
- **`setupTypingTracker`**: on `input` — if box empty → Stop immediately; first keystroke of a burst → Start; every keystroke re-arms a 3s idle timer → Stop. Emoji insertion's synthetic `input` feeds it too (inserting emoji = composing). `textarea._typingLocalReset` clears the local flag without a Stop dispatch — used by the send pipeline, since HandleSend broadcasts its own stop.
- **Send capture listener** — the invariant change:
  - Non-empty send: dispatch a synthetic `change` (Blazor serializes the full value synchronously during the dispatch) **before** the ghost/clear; in-order circuit processing lands it ahead of the click, so `HandleSend`'s guard reads fresh `messageText`. This is the edit-box save pattern applied to send.
  - **Empty-box click: still dispatch `change`.** This guard matters: with change granularity, a stale committed draft can sit in `messageText` (type → blur commits → refocus → delete all → click send: no blur happened, so no change fired — the server still holds the old text and would send it). Committing the empty value ahead of the click makes the guard reject correctly.
- Emoji-insert comment updated: its synthetic `input` now feeds client-side listeners only; the server draft syncs via the pre-send change — emoji insertion costs zero circuit traffic.

## The superseded invariant (updated in memory too)

Old rule: *"no synthetic `input` event after the client-side clear."*
New rule: **"the synthetic `change` carries the FULL value and fires BEFORE the clear — and never dispatch anything after the clear"** (the original reason stands: a post-clear event would sync `messageText=''` ahead of the click and the guard would reject the send).

## Why the clobber hazard doesn't bite

With change granularity the DOM value runs ahead of `messageText` constantly. Safe because Blazor only writes a bound input's value when the *rendered* value changed between renders: re-renders from typing indicators, pickers, reply bar etc. leave `messageText` untouched → no diff edit → the user's uncommitted text is never overwritten. The only server-side writes to `messageText` are the change events themselves (echoing the DOM's own value) and `HandleSend`'s clear (paired with the client-side clear).

## Accepted edges
1. ReadOnly flip mid-composition: the subtree swap loses uncommitted DOM text; the restored draft is the last *committed* value (stale). Rare admin action.
2. Continuing to type across a channel switch doesn't restart the indicator in the new channel until the next burst (parity with the old behavior).
3. An empty-box send click costs one tiny no-op dispatch (the safety commit). The button looks disabled, so this is rare.

## Test checklist (user builds/runs)
1. **The point:** DevTools → Network → WS frames while typing a long sentence — silence per keystroke; exactly one frame at burst start (typing on) and one ~3s after stopping (typing off).
2. Typing indicator (second browser/user): appears on first keystroke, clears after ~3s idle, clears on send, clears when the box is emptied.
3. Send correctness: type → Enter; type → click send; **emoji-only message** (insert via picker, never type, click send — the server first learns the content from the pre-send change); typed + emoji mixed; multiline via Shift+Enter.
4. **Stale-draft guard:** type "hello" → click somewhere else (blur) → refocus → select-all + delete → click send → *nothing* must send. Then the reverse: type, blur, refocus, type more, Enter → the *full current* text sends.
5. Rapid double-click send → exactly one message, one ghost.
6. Draft survives a room→room channel switch (same as before); typing indicator does NOT linger in the channel you left.
7. Mobile: Enter = newline still, button send works, typing indicator works.
8. IME if available: composition commit via Enter doesn't send (existing guard), next Enter sends.
9. Regressions: ghosts (text + GIF), picker open/close (item 2), message edit box (its own change pattern — untouched), file upload, reply flow.
