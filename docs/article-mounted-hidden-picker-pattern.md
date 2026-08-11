# The Mounted-Hidden Picker Pattern

### Instant popups in Blazor Server: mount once, hide with CSS, open at tap speed

This is a companion piece to [Your Fingers Never Wait for the Circuit](article-responsiveness-in-blazor-server.md), where we described making our self-hosted Blazor Server chat app, [Yap](https://github.com/urza/Yap), feel instant for users on ~900 ms connections. One of the five patterns there deserves its own write-up, because it sounds like one trick but is actually a small system with three problems to solve: **the always-mounted, CSS-revealed picker.** Our GIF picker exercises all three, so it's the example throughout.

## The old way, and why it was slow twice

The naive Blazor pattern for any popup is the one every tutorial teaches:

```razor
@if (showGifPicker)
{
    <GifPicker ... />
}
```

Tap the GIF button and you pay **twice**. First the round trip: in Blazor Server the tap travels to the server, flips `showGifPicker`, and the new render travels back — ~900 ms on a bad link before anything happens on screen. Then the mount: the picker component initializes *from scratch* — builds its render tree, fetches trending GIFs from the provider, and the browser starts downloading a wall of animated preview images. And because closing destroys the subtree, the next open pays the mount cost all over again. Every single time.

The insight that unlocks the fix: open/close is pure **local UI state**. The server has no business knowing whether your picker is open. So invert the lifecycle — *mount the picker once, keep it alive but invisible, and make "open" a CSS state change that never leaves the browser.*

## Alive but asleep

The picker mounts about a second after the page loads — not at time zero, because the first render should spend its budget on messages, not on a picker the user may never open:

```csharp
/// Background mount of the hidden picker subtrees. The delay is a priority
/// hint, not a contract — a toggle tap that beats it lands in
/// EnsurePickersMounted via chat.js and skips the wait.
private async Task MountPickersAfterIdleAsync()
{
    await Task.Delay(1200);
    await EnsurePickersMounted();
}

[JSInvokable]
public async Task EnsurePickersMounted()
{
    if (pickersMounted) return;
    pickersMounted = true;
    try { await InvokeAsync(StateHasChanged); }
    catch { /* circuit may be dead */ }
}
```

When `pickersMounted` flips, the pane renders into the DOM — wrapped in a render firewall we'll meet in a moment, tagged with a `data-picker-pane` attribute, and immediately swallowed by `display: none`:

```razor
<button class="gif-toggle-button" data-picker-toggle="gif" title="GIF picker">
    <span class="gif-toggle-badge">GIF</span>
</button>
@if (pickersMounted && !isMobileLayout)
{
    <PickerPane>
        <div class="emoji-picker-backdrop" data-picker-pane="gif"></div>
        <div class="emoji-picker-container" data-picker-pane="gif">
            <GifPicker CurrentUserId="@UserId" CurrentUsername="@Username"
                       OnGifSelected="HandleGifSelected" OnUploadError="HandleGifUploadError" />
        </div>
    </PickerPane>
}
```

Notice the toggle button carries **no Blazor handler at all** — no `@onclick`. Its entire behavior is the `data-picker-toggle="gif"` attribute, picked up by one delegated listener in our site-wide `chat.js`:

```js
document.addEventListener('click', (e) => {
    const toggle = e.target.closest('[data-picker-toggle]');
    if (!toggle) return;
    const container = toggle.closest('.message-input-container');
    const which = toggle.dataset.pickerToggle;          // "gif" | "emoji"
    if (container.dataset.picker === which) {
        container.removeAttribute('data-picker');       // close
    } else {
        container.dataset.picker = which;               // open (or swap pickers)
        notifyPickerOpened(container, which);
    }
});
```

And the CSS turns that one attribute into visibility:

```css
.emoji-picker-container[data-picker-pane] { display: none; }

.message-input-container[data-picker="gif"] [data-picker-pane="gif"] {
    display: flex;   /* opening costs one attribute flip, zero round trips */
}
```

That's the whole open path: tap → attribute flip → CSS reveal, in the same frame as the tap. The `data-picker` attribute is *single-valued* on the container, which buys a subtle nicety for free — tapping the emoji button while the GIF picker is open just overwrites the value, so picker→picker swaps are also instant, with no "close one, round trip, open the other" dance.

Simple so far. Now the three problems that naive always-mounting runs into, and how each one resolves.

## Problem 1: a mounted picker gets re-rendered to death

Here's the trap that makes naive always-mounting a performance *regression*. The component hosting our pickers — the message input — re-renders constantly: typing indicators, the reply bar, upload progress. Blazor re-renders cascade down to children, which means every one of those events would re-diff the entire hidden GIF grid. You'd be paying render tax on a picker nobody is looking at.

`PickerPane` is the firewall — nine lines that make the whole architecture viable:

```razor
@* Render firewall for the always-mounted pickers. ShouldRender=false blocks
   parent re-render cascades; the pickers' own interactions still render,
   because Blazor renders from the component that handled the event. *@
@ChildContent

@code {
    [Parameter] public RenderFragment? ChildContent { get; set; }
    protected override bool ShouldRender() => false;
}
```

`ShouldRender() => false` blocks exactly the parent cascades. But the picker isn't frozen — this is the part that surprises people: **Blazor renders from the component that handled the event, not from the root.** Click a tab *inside* the GIF picker and `GifPicker` handles that event and re-renders itself normally; `PickerPane` never gets a vote. Only renders arriving *from above* are stopped.

The firewall has one escape hatch worth knowing about. Because ordinary re-renders can't restructure the pane's contents, a change to the content's *shape* needs a fresh component instance — and `@key` provides exactly that. Our emoji pane swaps between a desktop picker and a combined mobile picker, so it's keyed on the layout:

```razor
@* A mobile↔desktop flip must rebuild this pane with the right picker inside —
   PickerPane blocks ordinary re-renders, so the swap needs the fresh instance
   a changed key provides. *@
<PickerPane @key="isMobileLayout">
    ...
</PickerPane>
```

A changed key tears down the old instance and mounts a new one — and a *first* render always happens regardless of `ShouldRender`. Firewall intact, shape changes still possible.

## Problem 2: a component that initializes once but opens many times

The old picker's `OnInitializedAsync` ran fresh on every open, so "load trending, show current recents, empty search box" happened naturally. The mounted picker initializes **once per page** and then lives across dozens of opens. Two things go wrong immediately: data goes stale (send a GIF, reopen the picker — "Recent" doesn't include it), and any provider fetch in the initializer now fires for every user at page load, whether or not they ever open the picker. So the fetch moved out:

```csharp
// Trending is deliberately NOT fetched here: the picker mounts hidden at page
// load, and the provider call would fire for users who never open it. The
// first OnPickerOpened pays it instead.
```

The replacement for "init runs on open" is an explicit **open hook**. The picker registers itself with chat.js on first render:

```csharp
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;
    _dotNetRef = DotNetObjectReference.Create(this);
    await JS.InvokeVoidAsync("registerPickerOpenHook", rootElement, _dotNetRef);
}
```

…and chat.js calls it back each time the pane becomes visible (the `notifyPickerOpened` from the toggle listener). Per-open freshness lives there instead of in `OnInitialized`:

```csharp
[JSInvokable]
public async Task OnPickerOpened()
{
    recentEntries = GifService.GetRecents(CurrentUsername);   // recents move with usage
    ClearSearch();                                            // leftover query would greet the next open
    if (trendingResults.Count == 0 && GifService.IsConfigured)
        _ = LoadTrendingAsync();                              // first open pays the deferred fetch
    try { await InvokeAsync(StateHasChanged); }
    catch { /* circuit may be dead */ }
}
```

The timing is the elegant part: the picker is *already open and painted* when this fires. Trending arrives one round trip later and pops in behind the open UI — the round trip still happens, it just stopped gating anything. Favorites and the server GIF library need nothing here at all, because they're event-driven — service events (`OnFavoritesChanged` and friends) keep them live even while the picker sleeps.

There's a structural reason the hook must exist, not just a convenience. The `PickerPane` firewall blocks parameter flow into the sleeping subtree — that was the whole point — which means nothing per-open can ride the normal Blazor parameter mechanism. **The render firewall closes the parameter door, so the open hook becomes the doorbell.** Everything that needs to reach the picker per-open goes through it.

## Problem 3: the tap that beats the mount

There's a race baked into the 1.2-second delay: a fast user can tap the GIF button before the background mount ever ran. The naive outcome is a dead button. The fix handles it from both sides.

The JS side notices there's nothing to show and asks Blazor to mount *now*:

```js
const notifyPickerOpened = (container, which) => {
    const pane = container.querySelector(
        `.emoji-picker-container[data-picker-pane="${which}"]`);
    if (!pane) {
        // The tap beat the ~1.2s background mount. Ask Blazor to mount now —
        // the pane arrives already-open (the attribute is set) and
        // registerPickerOpenHook fires the open hook once the components exist.
        container._pickerHostRef?.invokeMethodAsync('EnsurePickersMounted');
        return;
    }
    pane.querySelectorAll('.emoji-picker, .gif-picker').forEach(p =>
        p._openRef?.invokeMethodAsync('OnPickerOpened'));
    // ...per-open resets (search clear, scroll sync)
};
```

Note what happened *before* this call: the toggle listener already set `container.dataset.picker = "gif"`. The client-owned state says "the GIF picker is open" — there's just no pane in the DOM yet. So when `EnsurePickersMounted` renders one a round trip later, the CSS selector `[data-picker="gif"] [data-picker-pane="gif"]` matches **the instant the element exists**, and the picker appears already open. No second tap, no reconciliation code — the declarative CSS did the reconciling.

The Blazor side closes the loop. That late-arriving picker missed its `OnPickerOpened` call (it fired into an empty DOM), so the registration hook checks whether it's being born into an already-open pane:

```js
window.registerPickerOpenHook = (el, dotNetRef) => {
    el._openRef = dotNetRef;
    // If this pane is already open (the user's tap beat the background mount),
    // the toggle-time notification found no components — fire the hook now.
    const pane = el.closest('[data-picker-pane]');
    const container = el.closest('.message-input-container');
    if (pane && container && container.dataset.picker === pane.dataset.pickerPane) {
        dotNetRef.invokeMethodAsync('OnPickerOpened');
    }
};
```

So the early tap degrades to exactly one round trip of wait — what *every* open used to cost — and only for a user who taps within the first second of page load. That's the honest cost of this pattern, and we accepted it.

## What the hidden picker doesn't cost

One last detail makes "always mounted" cheap enough to be free. A GIF picker's weight isn't its DOM — it's the images. Every cell renders with `loading="lazy"`, and lazy-loading composes beautifully with the reveal mechanism: elements inside `display: none` have no layout, and images with no layout are never "near the viewport," so **the hidden picker downloads zero images**. The browser starts fetching previews only when the attribute flips and the pane gets geometry. The DOM sits warm; the bandwidth waits for intent.

The always-mounted grid also unlocks a follow-on win covered in the companion article: search stops being "query the server, re-render results" and becomes an in-place client-side filter over the mounted cells — instant on any connection.

## The shape of it

Step back and the division of labor is clean:

| Concern | Owner |
|---|---|
| Is the picker open? | Client — `data-picker` attribute + CSS |
| What's in the picker? | Blazor — `GifPicker` renders content, services push updates |
| Protecting the sleeping subtree | `PickerPane` — `ShouldRender() => false` |
| Per-open freshness | The `OnPickerOpened` JSInvokable hook |
| The early-tap race | Both sides — `EnsurePickersMounted` + the hook's already-open check |

Blazor never learns the picker opened (except the deliberate open-hook ping), and JS never learns what a GIF is. Each side owns the thing it's structurally good at: the client owns *visibility*, the server owns *content*.

If you take one checklist away, it's this — always-mounting a Blazor Server popup takes four pieces, and skipping any of them bites:

1. **Defer the mount** a beat past page load, with a JSInvokable rush path for taps that beat it.
2. **Firewall the subtree** (`ShouldRender() => false` wrapper) or the host's re-renders will diff your hidden DOM forever; use `@key` on the wrapper when the content's shape must change.
3. **Add an open hook** for per-open freshness — the firewall blocks parameters, so refresh-on-open must arrive by explicit call.
4. **Let CSS reconcile state and DOM** — client-owned `data-*` attribute plus an attribute selector means even a pane that mounts late appears in the right state the moment it exists.

Behind those four pieces, opening a picker on a 900 ms connection costs the same as on localhost: one attribute, one frame, zero round trips.

---

*Yap is open source — this pattern lives in [`MessageInput.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/MessageInput.razor), [`PickerPane.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/PickerPane.razor), [`GifPicker.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/GifPicker.razor), and [`chat.js`](https://github.com/urza/Yap/blob/main/Yap/wwwroot/js/chat.js).*
