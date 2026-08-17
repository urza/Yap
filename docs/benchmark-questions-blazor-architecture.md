# LLM benchmark: Blazor Server architecture questions from Yap

Ten questions distilled from real production problems in this repository. Every answer
here was validated the hard way: by shipping the fix and running it with live users.

**How to use:** paste only the *Ask the model* block. The questions are self-contained
and need no access to this repo. Grade against the ground truth and rubric. Each
question lists must-have points (2 points each), bonus points (1 each), and red flags
(subtract 2, they indicate confident wrong mechanisms). A strong model scores high on
Q1–Q5 from C#/Blazor knowledge alone; Q6–Q10 need architectural judgment on top.

Difficulty: ★ = solid framework knowledge, ★★ = deep mechanism, ★★★ = mechanism plus judgment.

---

## Q1 ★★ — The innocent event dispatch

**Ask the model:**

> A Blazor Server chat app has a singleton `ChatService` shared by all connected users.
> Each user's components subscribe to its event. The event and dispatch are:
>
> ```csharp
> public event Func<ChatMessage, Task>? OnMessageReceived;
> // in SendMessageAsync:
> await OnMessageReceived.Invoke(message);
> ```
>
> Each handler updates that user's UI. With 10 users online, describe precisely what
> this `await` does and does not do, and every problem this design causes at runtime.

**Ground truth:** `OnMessageReceived` is a multicast delegate. `Invoke` calls the 10
handlers synchronously, one after another, in subscription order. Each handler runs on
the sender's call path until its first `await`. The returned `Task` is only the LAST
handler's task, so the `await` observes only handler 10. The tasks of handlers 1–9 are
discarded: their async tails run unobserved and their exceptions are lost. Consequences:
the sender's latency grows with subscriber count, one slow subscriber delays all later
subscribers, exceptions from all but the last handler vanish, and one handler that
throws synchronously stops the invocation list so later handlers never run.

**Grade on (must-have):**
- Handlers run sequentially, not concurrently.
- Only the last handler's `Task` is awaited; the others are unobserved.
- Lost exceptions from the unobserved tasks.
- Sender latency scales with subscriber count / slow subscriber blocks the send path.

**Bonus:** a synchronous throw in one handler prevents later handlers from running;
suggests `GetInvocationList()` as the standard way to handle each subscriber's task.

**Red flags:** claims the handlers run in parallel; claims the `await` waits for all
handlers; claims events with async handlers are fine as written.

---

## Q2 ★★ — Parallel was not enough

**Ask the model:**

> The team fixed Q1 by iterating `GetInvocationList()` and running all handlers under
> `Task.WhenAll`, each wrapped in try/catch. Users still reported multi-second sends.
> Logs showed dispatch sometimes took ~60 seconds when a mobile user's connection
> dropped. Each handler performs JS interop calls into its own user's browser.
> Explain why `Task.WhenAll` still causes this, why specifically ~60 seconds, and what
> the failure mode is for a phone that is connected but very slow.

**Ground truth:** `Task.WhenAll` completes only when the slowest handler completes, so
dispatch is still gated by the worst circuit. A circuit whose transport silently died
does not fail fast: pending JS interop calls hang until the JS interop timeout, which
defaults to about 60 seconds in Blazor Server. For a live-but-slow phone there is no
timeout at all: each interop round trip completes, just slowly (each one is a full
client round trip), so the handler takes seconds legitimately and the whole dispatch
waits for it. Per-handler timeouts cap the damage but keep the coupling: the sender
still waits, now up to the timeout, for state that only affects someone else's screen.

**Grade on (must-have):**
- WhenAll = gated by slowest handler.
- Dead circuit: interop calls hang until the ~60 s default interop/circuit timeout.
- Slow-but-alive circuit: no timeout fires; handler is slow because each JS interop is
  a network round trip to that user's browser.
- Timeouts reduce but do not remove the sender↔receiver coupling.

**Bonus:** notes that receivers' rendering does not belong in the sender's call chain
at all (anticipates Q3); mentions `CircuitOptions.JSInteropDefaultCallTimeout`.

**Red flags:** blames the database or server CPU; proposes increasing the timeout;
claims a dropped WebSocket faults pending interop calls immediately.

---

## Q3 ★★★ — The settlement, and defending `async void`

**Ask the model:**

> The final design changed the singleton's events to plain `Action<ChatMessage>` and
> raises them with `OnMessageReceived?.Invoke(message)`, no await. Each Blazor
> component subscribes with an `async void` handler that wraps its work in
> `InvokeAsync(...)` and its own try/catch. `async void` is normally considered a bug
> in C#. Justify why it is the correct tool exactly here, and list the obligations the
> pattern places on every handler for it to stay safe.

**Ground truth:** the goal is that the raiser cannot wait for subscribers. `async void`
methods have fire-and-forget semantics by design: the caller gets control back at the
first await and never observes completion. That is exactly the desired decoupling. The
classic dangers of `async void` (unobservable exceptions, no way to await) are the
point, not an accident, and the pattern compensates with strict handler obligations:
(1) every handler wraps its body in try/catch, because no caller can catch for it and
an unhandled exception in `async void` can crash the process; (2) UI work goes through
`InvokeAsync`, because the event fires on the raiser's thread and Blazor component
state belongs to that circuit's dispatcher/sync context; (3) handlers treat
`ObjectDisposedException` / `InvalidOperationException` as an expected "circuit died
mid-render" case; (4) components unsubscribe on dispose (before any awaits in the
dispose path), or the singleton keeps dead handlers alive forever (memory leak and
ghost work); (5) the service must fully mutate state before raising, since handlers may
read it immediately (see Q4).

**Grade on (must-have):**
- Fire-and-forget is the desired semantic: sender never waits for receivers.
- Mandatory try/catch inside the handler; unhandled `async void` exceptions are fatal.
- `InvokeAsync` needed for circuit/dispatcher affinity.
- Unsubscribe on dispose to avoid leaking handlers on a singleton.

**Bonus:** names the circuit-dead exception types as expected warnings; state-before-
event ordering; notes each subscriber now fails or lags independently.

**Red flags:** recommends `async Task` events plus awaiting them (reintroduces Q1/Q2);
recommends a message queue/Channel as the only correct answer without addressing the
question; says `async void` is always wrong.

---

## Q4 ★★ — Order of mutation and notification

**Ask the model:**

> In the same design (singleton raises `Action` events, scoped per-user components
> re-render in response), the service does this, in order: (1) update its in-memory
> state and unread counters, (2) persist to the database, (3) raise the events. A
> reviewer suggests raising the events first so users see the message sooner. Explain
> concretely what breaks if events fire before the state mutation completes.

**Ground truth:** handlers run and re-render immediately, possibly on other threads,
and they read the shared state, not the event payload alone (unread badges, sidebar
sort order, typing lists). If the event fires before the mutation, the first render
races the mutation: components render stale unread counts or a message list without
the new message, and nothing re-triggers them afterward, so screens sit wrong until an
unrelated event causes the next render. Mutate-then-notify makes every event a
guarantee: "the state you can read is already correct." The latency argument is also
false economy: the in-memory mutation is microseconds; perceived speed comes from the
sender's local echo, not from reordering server-side steps.

**Grade on (must-have):**
- Handlers read shared state, not just the event args.
- Firing first creates a race: first render can see stale state.
- No later trigger corrects it; the UI stays wrong until the next unrelated event.

**Bonus:** notes the mutation cost is negligible so the reorder buys nothing; mentions
optimistic local echo as the real perceived-latency fix.

**Red flags:** says the order does not matter because of the single-threaded UI (the
raiser and other circuits are not on that dispatcher); proposes locks around render.

---

## Q5 ★ — Why no SignalR hub

**Ask the model:**

> A team migrates a chat app from Blazor WebAssembly + a custom SignalR `ChatHub` to
> Blazor Server. Someone proposes keeping the custom hub for chat messages "since
> SignalR is already there." Argue for or against, from the Blazor Server architecture
> itself.

**Ground truth:** against. Blazor Server already maintains one persistent SignalR
connection per user, the circuit, and every UI update already flows over it. A custom
hub adds a second parallel real-time channel: a second connection to manage, its own
reconnection story, its own auth, and a state synchronization problem between hub
messages and circuit renders. The idiomatic Blazor Server shape is: a singleton service
holds shared state and raises events; scoped, per-circuit components subscribe and
re-render over their own circuit. The hub is what you need when the client renders
itself (WASM/JS SPA); under Blazor Server it is redundant transport.

**Grade on (must-have):**
- The circuit is already a persistent SignalR connection carrying all UI updates.
- A custom hub duplicates transport and reconnection/auth handling.
- The idiomatic alternative: singleton state + events + per-circuit subscribers.

**Bonus:** notes the hub becomes appropriate again with WASM or any client-rendered
frontend; mentions two connections drifting out of sync.

**Red flags:** claims Blazor Server components cannot get real-time pushes without a
hub; proposes polling.

---

## Q6 ★★★ — The 904 ms send button

**Ask the model:**

> Production telemetry for a Blazor Server chat app shows server processing ≤71 ms per
> message send, but the full circuit round trip for remote users is ~904 ms. The
> symptom: the send button (disabled while the input is empty) visibly enables about a
> second after the user starts typing. The input uses `@bind-Value` with
> `@bind-Value:event="oninput"` and the button's `disabled` is computed from the bound
> property. Explain the mechanism of the delay, then propose the minimal fix that makes
> the button enablement instant, without giving up server-side validation on send.

**Ground truth:** with `oninput` binding, every keystroke travels to the server, the
server re-renders, and the new `disabled` state travels back: the button's visual state
is gated by a full ~900 ms round trip. Worse, per-keystroke traffic queues: SignalR
processes circuit messages in order and Blazor stops after a limited number of
unacknowledged render batches, so on a slow link keystroke traffic also delays the send
itself. The fix is to derive the button's visual state on the client. The cleanest
version is pure CSS, driven by the input's own value: with the button as a sibling of
the input, `.message-input:placeholder-shown ~ .send-button { /* dimmed */ }` styles
the empty state with zero JS and zero round trips. (A small JS `input` listener
toggling a class is an acceptable equivalent.) The server keeps validating on the
actual send; the client state is cosmetic and reconciles with the server's decision.
Accept also: switching the bind to change-granularity so typing sends nothing at all.

**Grade on (must-have):**
- Names the per-keystroke round trip as the mechanism (button state rendered
  server-side).
- Fix moves the *visual* enablement to the client (CSS `:placeholder-shown` or
  equivalent client-side state).
- Server remains the authority on the actual send (client state is cosmetic).

**Bonus:** the queueing amplifier (in-order processing, unacked-batch limit ~10);
change-granularity binding; the general principle "feedback the user watches during an
action must be local."

**Red flags:** optimizes the server (it is 71 ms of a 904 ms problem); debounces the
binding but keeps the round trip in the feedback path; moves validation fully to the
client.

---

## Q7 ★★ — Blazor and JS fighting over the DOM

**Ask the model:**

> In a Blazor Server app, a JS scroll handler adds the class `scroll-dismissing` to a
> `<div class="messages">` to hide a toolbar, and JS also appends temporary "pending
> echo" elements into a container inside the message list. Sometimes the class silently
> disappears and the appended elements vanish. The Blazor markup is
> `<div class="messages @extraClasses">`. Explain the mechanism and state the two rules
> that make JS-owned DOM safe inside Blazor-rendered markup.

**Ground truth:** Blazor's renderer diffs against its own last render tree, not the
live DOM. When any state change re-renders the component, Blazor writes the `class`
attribute it computed (`messages @extraClasses`), clobbering classes JS added, and
reconciles child nodes it thinks it owns, removing elements it never rendered. Rules:
(1) any attribute JS mutates must be fully static in the Blazor markup: no
interpolation in that attribute, so the diff never rewrites it; (2) JS-created elements
live only inside containers that Blazor renders but always leaves empty, so the diff
has no children of its own to reconcile there.

**Grade on (must-have):**
- Blazor re-render rewrites the attribute from its render tree; interpolation in that
  attribute means JS changes are clobbered on the next render.
- Rule: JS-toggled attributes stay static on the Blazor side.
- Rule: JS-owned elements go in Blazor-rendered but always-empty containers.

**Bonus:** notes the failure is intermittent because it needs a re-render to trigger;
mentions `@key` or moving the state fully to one owner as alternatives.

**Red flags:** blames browser extensions or CSS; suggests JS should call
`StateHasChanged` to "sync"; suggests MutationObserver to re-add the class (fighting
the renderer).

---

## Q8 ★★★ — Sign out my other device

**Ask the model:**

> A Blazor Server chat app supports multiple sessions per account (phone, laptop). The
> user clicks "sign out other devices." The server removes the other sessions from its
> session store and rotates the auth token. Yet the other devices keep working: they
> stay connected and can still send messages until they reload. Explain why, and
> design the mechanism that actually kicks them immediately.

**Ground truth:** removing server-side session records does not touch the victims'
Blazor circuits. A circuit is an independent live connection with all its component
state in server memory; it authenticated once, at circuit establishment, and nothing
re-checks the session store per interaction. The fix is push, not pull: raise an event
(e.g. `OnSessionKicked(sessionId)`) from the service; every circuit's layout component
subscribes; the circuit whose session was revoked navigates itself to the sign-out
endpoint with a full page load (`forceLoad: true`), which tears down the circuit and
clears the cookie. The initiating device must also receive the newly rotated token
(e.g. a refresh endpoint) or it logs itself out too.

**Grade on (must-have):**
- Circuits are live server-side state; deleting session records does not disconnect
  them, and auth is not re-evaluated per interaction.
- Fix is an event/notification the victim circuit handles by navigating itself out.
- Full navigation (`forceLoad`) needed to actually kill the circuit.

**Bonus:** the current device needs the rotated token delivered (refresh endpoint);
notes per-session targeting so only victims navigate.

**Red flags:** suggests the server can simply "close the circuit" via the session
store; suggests checking auth on every render (misses the mechanism and the cost);
suggests short cookie expiry as the fix (still leaves the live circuit working).

---

## Q9 ★★★ — The installed PWA that kept creating accounts

**Ask the model:**

> A Blazor Server chat app uses a persistent auth cookie with `SameSite=Strict`. Users
> who install it as a PWA on Android report being logged out "sometimes," and the logs
> show a distinctive pattern: from the installed app, every launch produces a
> cookie-less request to `/`, then some users register a fresh account (one user
> created six). The same users, opening the site in the browser tab, are still logged
> in. Explain the mechanism and give the fix.

**Ground truth:** `SameSite=Strict` cookies are withheld on navigations that the
browser does not classify as same-site, and launching an installed PWA from the home
screen/OS is such an entry navigation. So every PWA launch arrives with no cookie: the
app sees an anonymous visitor, shows login/registration, and users who do not remember
their credentials create another account. The browser tab works because in-tab
navigation is same-site. Fix: use `SameSite=Lax` for the auth cookie (Lax attaches on
top-level GET navigations, which is exactly the launch case; Strict buys nothing here
because the app does not need CSRF protection on that cookie for GETs), and add
self-healing: on any authenticated HTML GET, re-issue the cookie, so devices recover.
Defense-in-depth for the account churn: auto-generated passphrases handed to the user
so a "new device" can log into the existing account.

**Grade on (must-have):**
- Strict withholds the cookie on the PWA-launch navigation; every launch is
  cookie-less.
- That is why users re-register (app treats them as new visitors).
- Fix: SameSite=Lax for the auth cookie (top-level GET navigations carry it).

**Bonus:** cookie re-issue on authenticated GETs as self-healing; why Strict added no
real security for this cookie; the browser-tab-vs-PWA discrepancy explained.

**Red flags:** blames the service worker or cache; proposes `SameSite=None` (needless,
weaker); proposes localStorage tokens without addressing the cookie mechanism.

---

## Q10 ★★★ — Smart login under carrier NAT

**Ask the model:**

> The same app has a "smart login" convenience: if a visitor's IP matches the IP of an
> existing account's recent session, the login page pre-fills that account. Logs show
> it works for some mobile users on some days and silently fails on others; one user's
> IP changed from `.213` to `.216` between two requests five seconds apart, and the
> same user has a stable IPv6 address on other days. A developer proposes matching on
> the /24 prefix instead of the exact IP. Evaluate the proposal and give a safer
> design.

**Ground truth:** the intermittent behavior is carrier-grade NAT: mobile IPv4 egress
addresses rotate per-connection from a shared pool, so exact-IP matching fails
whenever the address rotates; the "works sometimes" days are the stable IPv6 path.
Prefix-widening is dangerous precisely because the pool is shared: a /24 of CGNAT
egress addresses covers many unrelated customers, so widening turns a convenience
into offering strangers a pre-filled login for someone else's account. The safer
design widens over *time*, not address space: persist the set of known exact IPs per
account with a TTL (accumulate the rotating addresses as the user logs in), keep
matches exact, and treat the feature as a hint only, never as authentication (anything
sensitive still requires the credential). IPv6 helps because it is per-device and
stable; the risk case is IPv4-only paths.

**Grade on (must-have):**
- Diagnoses CGNAT rotation as the cause of intermittent failure (and stable IPv6 as
  why it sometimes works).
- Rejects prefix-widening: shared pool means cross-customer matches.
- Safer design: accumulate exact known IPs per account over time with expiry, and/or
  keep the feature a non-authenticating hint.

**Bonus:** notes 5-second rotation implies per-connection pooling; mentions device
cookies/passkeys as the fundamentally better identity signal.

**Red flags:** endorses the /24 match without the shared-pool risk; proposes IP-based
auto-login as actual authentication; claims IPs cannot change that fast.

---

## Scoring summary

| | Must-have pts | Bonus pts |
|---|---|---|
| Q1 | 8 | 2 |
| Q2 | 8 | 2 |
| Q3 | 8 | 3 |
| Q4 | 6 | 2 |
| Q5 | 6 | 2 |
| Q6 | 6 | 3 |
| Q7 | 6 | 2 |
| Q8 | 6 | 2 |
| Q9 | 6 | 3 |
| Q10 | 6 | 2 |
| **Total** | **66** | **23** |

Red flags subtract 2 each. Interpretation from our own calibration: Q1's "only the
last Task is awaited" subtlety, Q7's render-tree-diff mechanism, and Q9's
SameSite-on-PWA-launch behavior separate models that know the platforms from models
that pattern-match on keywords. Q3, Q6, Q8, and Q10 additionally test judgment: the
correct answers argue against superficially reasonable alternatives (awaited events,
server optimization, session-store deletion, prefix matching).
