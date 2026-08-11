# Your Fingers Never Wait for the Circuit

### Making a Blazor Server chat app feel instant on a 900 ms connection

Blazor Server has a dirty secret that every tutorial glosses over: **every UI interaction is a network round trip.** Click a button — round trip. Type a character into a bound input — round trip. Open a dropdown — round trip. On localhost this is invisible. On a real user's phone, on carrier NAT, three countries away from your server, it is the whole user experience.

We run [Yap](https://github.com/urza/Yap), a small self-hosted chat app built on Blazor Server (.NET 10) — rooms, DMs, reactions, GIFs, emoji, push notifications. It worked great in testing and felt sluggish in production for exactly the users a chat app exists for: people far from the server on imperfect connections. Typing lagged. The send button enabled a beat after you started typing. Sent messages appeared after a pause long enough to make you tap again. The emoji picker took a second to open.

This is the story of how we fixed it without abandoning Blazor Server — and the small set of patterns that emerged, now validated by real users in production. The punchline up front:

> **The user's fingers never wait for the circuit.** Feedback the user watches while acting derives from local state (CSS/JS) and reconciles with the server afterward. The server remains the source of truth.

## Measure before you optimize: the wire is the problem

Our first instinct was the classic one — profile the server. We instrumented the send pipeline end to end and added a client-side RTT probe that times a no-op circuit call every 10 seconds:

```js
const ping = () => {
    const t0 = performance.now();
    telemetryRef.invokeMethodAsync('ProbePing', lastRtt, visible, idleSeconds)
        .then(() => { lastRtt = Math.round(performance.now() - t0); });
};
```

The probe deliberately measures the *full experienced* round trip — transport plus Blazor's dispatcher queue — because that's the latency the user feels, not the ping ICMP would report.

The numbers settled the argument immediately:

| | Worst case measured in prod |
|---|---|
| Server work per message send (incl. SQLite persist) | **≤ 71 ms** |
| Circuit round trip for our remote users | **~904 ms** |

The server was cheap. The wire was 13× the cost, and it's the one thing we don't control. Every server-side optimization we could dream up was optimizing 7% of the problem. The fix had to be architectural: **take the round trip off the perceived-feedback path entirely.**

One more amplifier made it worse: SignalR processes circuit messages in order, and Blazor caps unacknowledged batches. On a slow link, chatty per-keystroke traffic doesn't just lag — it queues, and everything behind it (including your message send) waits in line. Every dispatch you eliminate helps every dispatch that remains.

## The doctrine

The trap here is overcorrecting — ripping out Blazor's model and rebuilding half the app in JavaScript, at which point why are you running Blazor Server at all? We wanted a narrower rule, and after shipping it across the whole main chat loop, it holds up:

- **Feedback the user watches while acting is local, by default.** Button enablement, keystroke effects, picker open/close, search-as-you-type, your own sent message appearing — all client-side, instantly, reconciled with the server afterward.
- **The server remains the source of truth.** State, validation, guards, persistence, and fan-out to other users stay server-side. Optimistic UI reconciles to whatever the server decides. Local state never lies about permissions or outcomes.
- **Don't fight Blazor.** The client layer is thin and surgical — CSS selectors, `data-*` attributes, a few event listeners. Components, binding, services, and events stay idiomatic Blazor. If the local version grows into a state machine wrestling the framework, reconsider.

What follows are the five patterns that implement this, with the actual shipped code.

## Pattern 1: Value-driven CSS state

The send button should look enabled exactly when the textarea has content. The obvious Blazor way — bind the input, compute `disabled` from the bound value — re-renders the button one round trip after each keystroke. On our slow link, the button visibly lagged the user's typing.

The fix is zero lines of C# and zero lines of JS. The DOM already knows whether the textarea is empty — CSS can react to *the value itself* via `:placeholder-shown`:

```css
/* Client-side enablement: dim purely on "is the box empty" —
   reacts to the value itself, no circuit round trip. */
.message-input:placeholder-shown ~ .send-button {
    background: var(--bg-muted);
    opacity: 0.5;
    cursor: not-allowed;
}
```

The button dims when the box is empty and lights up on the first character, at monitor refresh rate. The only requirement is that the placeholder is never an empty string (no placeholder means `:placeholder-shown` never matches).

The same trick appears in our emoji picker's search box, where the ✕ clear button shows only while a query exists — again `:placeholder-shown`, again no round trip.

This is the cheapest pattern in the set, and the mental shift it teaches generalizes: before reaching for component state, ask whether the DOM already knows.

## Pattern 2: Client-routed events

Enter-to-send is timing-critical: `@onkeydown:preventDefault` in Blazor evaluates *on the server*, which means the decision to suppress a newline takes a full round trip — the newline is long since in the textarea by the time the answer arrives. And subscribing `@onkeydown` at all means every keystroke ships an event to the server.

Instead, the key handling lives client-side, and the send is *routed through the existing button*:

```js
textarea._enterKeyHandler = (e) => {
    // Desktop: Enter sends, Shift+Enter newline. Touch: Enter is newline.
    if (e.key !== 'Enter' || e.shiftKey || isTouch) return;
    // An IME commit also arrives as Enter — sending would eat the composed text.
    if (e.isComposing || e.keyCode === 229) return;
    e.preventDefault();
    if (!textarea.value.trim()) return;
    textarea.closest('.message-input-container')
        ?.querySelector('.send-button')?.click();
};
```

The important line is the last one. We don't invoke a .NET method from JS; we *click the send button*. The button's `@onclick="HandleSend"` stays the **single server entry point**, whether the user clicks it or presses Enter. There's exactly one code path into the server, it's idiomatic Blazor, and all the client-side instant feedback (next two patterns) hangs off that one physical click.

## Pattern 3: Change-granularity binding, with a commit-before-click handshake

The default Blazor pattern for an input — `@bind-Value` with `@oninput` granularity, or an `@onkeydown` handler — costs one circuit dispatch per keystroke. On a 900 ms link, typing a 20-character message generates a queue of 20 server events that everything else waits behind.

So the message box is bound at **change granularity** — plain `@bind`, which syncs on the `change` event, i.e. normally on blur:

```razor
<textarea @bind="messageText" placeholder="@placeholder" class="message-input"
          rows="1" id="@TextareaId"></textarea>
```

Typing now costs *zero* circuit dispatches. The typing indicator — the one thing that genuinely needs the server mid-burst, since other users see it — becomes a small JS tracker that calls the server about twice per burst (start, and stop after 3 s idle) instead of on every key:

```js
textarea._typingInputHandler = () => {
    if (!textarea.value.trim()) { stop(); return; }
    if (!typing) {
        typing = true;
        dotNetRef.invokeMethodAsync('ReportTypingStarted');
    }
    clearTimeout(timer);
    timer = setTimeout(stop, 3000);   // stop() reports ReportTypingStopped
};
```

But change granularity creates a puzzle: when the user hits send, the server's `messageText` may be stale — no blur has happened, so the draft was never committed. The solution leans on a guarantee Blazor gives you for free: **circuit messages process in order.** A capture-phase listener on the send button dispatches a synthetic `change` (committing the full draft) immediately before the click's RPC:

```js
// Capture phase: runs before Blazor's delegated @onclick.
document.addEventListener('click', (e) => {
    const btn = e.target.closest('.send-button');
    if (!btn) return;
    const textarea = btn.closest('.message-input-container')
        ?.querySelector('.message-input');
    if (!textarea?.value.trim()) return;

    // Commit the draft BEFORE the click's RPC. In-order circuit processing
    // lands the change ahead of the click, so HandleSend reads fresh text.
    textarea.dispatchEvent(new Event('change', { bubbles: true }));

    showPendingEcho(textarea.value);   // Pattern 4

    // Instant clear — the draft above was already committed.
    textarea.value = '';
}, true);
```

Two dispatches per send (change, then click), zero per keystroke, and `HandleSend` keeps its authoritative guard:

```csharp
private async Task HandleSend()
{
    if (string.IsNullOrWhiteSpace(messageText) || !channelId.HasValue) return;
    // ...validation, persistence, broadcast — all server-side, all authoritative
}
```

Note what the pattern is *not*: we didn't take message state away from Blazor. `messageText` is still the bound field, `HandleSend` still validates it. We only changed *when* the value syncs — and used event ordering to make "just in time" correct.

## Pattern 4: Optimistic echo with reconciliation

The heaviest perceived latency was the send itself: tap send, then watch nothing happen for a full round trip until the message renders. Users double-sent because the app gave no acknowledgment their tap worked.

The fix is a **ghost**: the instant the send button is clicked (same capture listener as above), JS appends a dimmed, plain-text copy of the message to the bottom of the list. The real message replaces it one round trip later:

```js
const showPendingEcho = (text) => {
    const ghost = document.createElement('div');
    ghost.className = 'pending-message';
    ghost.textContent = text;          // textContent only — never parsed as HTML
    appendPendingEcho(ghost);
};

const appendPendingEcho = (ghost) => {
    document.querySelector('.pending-echoes')?.appendChild(ghost);
    setTimeout(() => ghost.remove(), 15000);  // a send that never echoes fades out
    watchForEchoConfirm();
};
```

Reconciliation is a `MutationObserver` watching the message list. When one of *the sender's own* messages renders, the oldest ghost is removed — FIFO, so rapid-fire sends inside a single round trip all drain correctly. `MutationObserver` callbacks run before paint, so the ghost-to-real swap is flicker-free:

```js
echoObserver = new MutationObserver((mutations) => {
    for (const mutation of mutations)
        for (const node of mutation.addedNodes) {
            if (node.nodeType !== 1 || !node.matches?.('.message-group')) continue;
            if (node.dataset.author !== currentUser) continue;
            document.querySelector('.pending-echoes .pending-message')?.remove();
        }
});
echoObserver.observe(messages, { childList: true });
```

The failure mode is honest by construction: if the send never echoes (circuit dropped, server rejected it), the ghost quietly disappears after 15 seconds and the input still holds nothing — the vanished ghost *is* the "didn't send" signal, and nothing false ever entered the authoritative message list.

Where does the ghost live? That's the load-bearing detail — see the coexistence rules below:

```razor
@* RoomChat.razor / DmChat.razor — Blazor renders this container, always empty *@
<div class="messages">
    @foreach (var message in messages) { ... }
    <div class="pending-echoes"></div>
</div>
```

The same machinery handles GIFs. Tapping a card in the GIF picker closes the picker instantly (Pattern 5) and ghosts the very preview the user was just looking at — it's already in the browser cache, so it paints in the same frame, with the card's inline `aspect-ratio` copied over so the list doesn't jump when the real message swaps in. Selecting a GIF went from "tap… wait… wait… there it is" to feeling native.

## Pattern 5: Client-owned UI state in `data-*` attributes

Opening the emoji picker used to be: click → round trip → server flips `showEmojiPicker` → re-render mounts a large component tree → picker appears. About a second on the bad link, for pure *local* UI state — the server has no business knowing whether your picker is open.

Now the pickers are **mounted once, hidden**, and open/close is an attribute flip that never leaves the browser:

```js
document.addEventListener('click', (e) => {
    const toggle = e.target.closest('[data-picker-toggle]');
    if (!toggle) return;
    const container = toggle.closest('.message-input-container');
    const which = toggle.dataset.pickerToggle;          // "emoji" | "gif"
    if (container.dataset.picker === which)
        container.removeAttribute('data-picker');
    else
        container.dataset.picker = which;               // also swaps picker→picker
});
```

```css
.emoji-picker-container[data-picker-pane] { display: none; }

.message-input-container[data-picker="gif"]   [data-picker-pane="gif"],
.message-input-container[data-picker="emoji"] [data-picker-pane="emoji"] {
    display: flex;   /* opening costs one attribute flip, zero round trips */
}
```

Blazor renders the picker *content*; the client owns its *visibility*. The same idea covers "is the user composing" (`data-composing`, flipped on focusin/focusout — mobile CSS trades the upload button for textarea width) and the mobile picker's active tab (`data-active-tab`).

Two supporting pieces make always-mounted viable:

**A render firewall.** `MessageInput` re-renders constantly (typing indicators, reply bar, upload progress), and each re-render would re-diff the large hidden picker subtrees. A trivial wrapper component blocks exactly those parent-cascade renders:

```razor
@* PickerPane.razor — ShouldRender=false blocks parent cascades; the pickers'
   own interactions still render, because Blazor renders from the component
   that handled the event, not from the root. *@
@ChildContent

@code {
    [Parameter] public RenderFragment? ChildContent { get; set; }
    protected override bool ShouldRender() => false;
}
```

**Client-side search.** With the grid always mounted, emoji search stops being "send query to server, re-render results" and becomes an in-place filter. Each emoji cell carries a prerendered lowercase `data-kw` keyword string, and JS toggles attributes Blazor never renders — so a Blazor re-render can't clobber the filter state:

```js
const filterEmojiPicker = (picker, rawQuery) => {
    const q = (rawQuery || '').trim().toLowerCase();
    picker.querySelectorAll('.emoji-section').forEach(section => {
        let hits = 0;
        section.querySelectorAll('.emoji-btn[data-emoji]').forEach(btn => {
            const hit = !q || (btn.dataset.kw || '').includes(q);
            btn.toggleAttribute('data-search-miss', !hit);
            if (hit) hits++;
        });
        section.toggleAttribute('data-search-empty', hits === 0);
    });
};
```

Search-as-you-type over ~900 emoji, at keystroke speed, on any connection. One more detail earns its keep: picker cells render with `loading="lazy"`, and `display: none` + lazy images means the hidden pickers fetch **zero** images until first opened — always-mounted doesn't mean always-paid.

Blazor still does what Blazor is good at here: recent-emoji lists refresh behind the already-open picker through a small `[JSInvokable]` open-hook, arriving one round trip *after* the picker opened rather than gating it.

This pattern has enough moving parts — the render firewall, the open hook, the tap-beats-mount race — that we gave it a dedicated deep dive: [The Mounted-Hidden Picker Pattern](article-mounted-hidden-picker-pattern.md).

## The rules of coexistence

JS and Blazor sharing a DOM is where this approach either stays elegant or rots. Every bug we hit traced back to violating one of four rules:

1. **JS-owned DOM lives in containers Blazor renders empty.** The ghosts go in `<div class="pending-echoes"></div>` — Blazor diffs it as "empty div, unchanged" every render, so it never touches the JS-appended children. JS elements inserted *between* Blazor-rendered siblings get destroyed or duplicated by the next diff.
2. **If JS toggles an attribute, Blazor's side of that attribute must be static.** `class="messages @someFlag"` re-interpolates on every render and clobbers whatever JS set. Our rule: Blazor owns `class`, JS owns `data-*` state attributes (plus attributes on elements Blazor created but never updates).
3. **Capture phase runs before Blazor.** Blazor's event handlers are delegated at the root and run in the bubble phase. A capture-phase listener is guaranteed to run first — that's what lets the send listener paint the ghost and clear the input while the click's RPC is still being serialized. And the Blazor handler still fires afterward; you're prepending behavior, not replacing it.
4. **In-order circuit processing is an API you can lean on.** Dispatch a `change` before a `click`, and the server processes them in that order, always. The commit-before-click handshake in Pattern 3 is only correct because of this guarantee.

And one meta-rule: **keep measuring the truth.** The send→appear telemetry (a `MutationObserver` timing the real message's render against the click) kept reporting the honest ~900 ms round trip throughout — perceived latency improved because feedback went local, and the telemetry ensures the good feeling never masks an actual link problem.

## What stayed on the server

Deliberately untouched, because a visible round trip there is *honest*:

- **Settings saves, admin actions, channel management** — the user expects a save to take a moment; optimistic UI would add risk without adding felt speed.
- **Anything where optimistic state could lie in a way that matters** — permissions, moderation, account state.
- **All validation and guards.** `HandleSend` still checks emptiness, channel membership, write permissions. If a hand-crafted request bypasses the client niceties, the server verdict is the only one that counts.

## Results

Every interaction in the main chat loop — typing, send feedback, message echo, picker open/close, tab switch, emoji search, category jump, GIF select — now happens at local speed, on a connection where each round trip costs ~900 ms. Server traffic per message went from dozens of dispatches (keystrokes, picker state, key handling) to roughly three: typing-started, the draft commit, the send click.

After running in production with real users on genuinely bad connections — the first round of fixes for weeks, the full set for days — the verdict from the field is what you'd hope: the app stopped feeling like a remote terminal and started feeling like a chat app.

The takeaways, if you're running Blazor Server with users beyond your LAN:

1. **Measure the split first.** Our server work was ≤ 71 ms; the wire was ~904 ms. Know your numbers before optimizing the wrong side.
2. **The user's fingers never wait for the circuit** — feedback the user watches while acting derives from local state and reconciles afterward.
3. **Escalate cheaply**: CSS that reacts to values (`:placeholder-shown`) → client-routed events into one Blazor entry point → change-granularity binding with a pre-click commit → optimistic ghosts with observer reconciliation → `data-*` attributes for client-owned UI state.
4. **Follow the coexistence rules** — empty containers for JS-owned DOM, static Blazor attributes where JS toggles, capture phase to get ahead of Blazor, in-order processing to stay correct.
5. **Don't fight Blazor.** State, truth, validation, and fan-out stay server-side. The moment your JS grows a state machine, you've gone too far.

Blazor Server's programming model is a genuine joy — one language, no API layer, real-time for free. It just needs you to respect the one thing it can't abstract away: the speed of light, plus queuing. Keep the wire off the feedback path, and the model earns its keep even at 900 ms.

---

*Yap is open source — the patterns above live mostly in [`chat.js`](https://github.com/urza/Yap/blob/main/Yap/wwwroot/js/chat.js), [`MessageInput.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/MessageInput.razor), and the components' scoped CSS.*
