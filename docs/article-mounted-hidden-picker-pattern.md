# The Mounted-Hidden Picker Pattern

### Instant popups in Blazor Server: mount one time, hide with CSS, open at tap speed

This article is a companion to [Your Fingers Never Wait for the Circuit](article-responsiveness-in-blazor-server.md). That article tells how we made our self-hosted Blazor Server chat app, [Yap](https://github.com/urza/Yap), feel immediate for users on ~900 ms connections. One of the five patterns there gets its own article here, because it sounds like one trick but is a small system with three problems to solve: the always-mounted, CSS-revealed picker. Our GIF picker has all three problems, thus it is the example through the article.

## The old pattern, and why it paid two times

The usual Blazor pattern for a popup is in every tutorial:

```razor
@if (showGifPicker)
{
    <GifPicker ... />
}
```

When you tap the GIF button, you pay two times. The first cost is the round trip. In Blazor Server, the tap travels to the server, the server flips `showGifPicker`, and the new render travels back. On a bad link, ~900 ms pass before the screen changes. The second cost is the mount. The picker component initializes from zero: it builds its render tree, gets trending GIFs from the provider, and the browser starts to download many animated preview images. And because a close destroys the subtree, the next open pays the mount cost again, every time.

The key insight: open and close are pure local UI state. The server has no reason to know if your picker is open. Thus invert the lifecycle. Mount the picker one time, keep it alive but invisible, and make "open" a CSS state change that never leaves the browser.

## Mounted and hidden

The picker mounts approximately one second after the page loads. It does not mount at time zero, because the first render must spend its budget on messages, not on a picker that the user possibly never opens:

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

When `pickersMounted` flips, the pane renders into the DOM. It is wrapped in a render firewall (see below), it has a `data-picker-pane` attribute, and `display: none` hides it immediately:

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

Note that the toggle button has no Blazor handler at all. There is no `@onclick`. Its full behavior is the `data-picker-toggle="gif"` attribute, which one delegated listener in our site-wide `chat.js` reads:

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

The CSS turns that one attribute into visibility:

```css
.emoji-picker-container[data-picker-pane] { display: none; }

.message-input-container[data-picker="gif"] [data-picker-pane="gif"] {
    display: flex;   /* opening costs one attribute flip, zero round trips */
}
```

That is the full open path: tap, attribute flip, CSS reveal, in the same frame as the tap. The `data-picker` attribute has a single value on the container, which gives a small extra function at no cost. When the user taps the emoji button while the GIF picker is open, the tap only overwrites the value. Thus a swap from picker to picker is also immediate, without a close, a round trip, and a second open.

That part is simple. Now the three problems that naive always-mounted pickers run into, and the solution for each one.

## Problem 1: parent renders hit the mounted picker constantly

This trap can make a naive always-mounted picker a performance regression. The component that holds our pickers, the message input, renders again constantly: typing indicators, the reply bar, upload progress. Blazor renders cascade down to the children. Thus each of those events would diff the full hidden GIF grid again. You would pay a render cost for a picker that nobody sees.

`PickerPane` is the firewall. These nine lines make the full architecture possible:

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

`ShouldRender() => false` blocks exactly the parent cascades. But the picker is not frozen. This is the part that surprises people: Blazor renders from the component that handled the event, not from the root. Click a tab inside the GIF picker, and `GifPicker` handles that event and renders itself as usual. `PickerPane` gets no vote. The firewall stops only the renders that come from above.

The firewall has one deliberate opening that you must know. Ordinary renders cannot change the structure of the pane contents. Thus a change to the content shape needs a fresh component instance, and `@key` supplies exactly that. Our emoji pane swaps between a desktop picker and a combined mobile picker, thus its key is the layout:

```razor
@* A mobile↔desktop flip must rebuild this pane with the right picker inside —
   PickerPane blocks ordinary re-renders, so the swap needs the fresh instance
   a changed key provides. *@
<PickerPane @key="isMobileLayout">
    ...
</PickerPane>
```

A changed key removes the old instance and mounts a new one, and a first render always occurs, independent of `ShouldRender`. The firewall stays intact, and shape changes stay possible.

## Problem 2: a component that initializes one time but opens many times

The old picker's `OnInitializedAsync` ran fresh on each open. Thus "load trending, show current recents, empty search box" occurred naturally. The mounted picker initializes one time per page and then lives across tens of opens. Two things go wrong immediately. Data becomes stale: send a GIF, open the picker again, and "Recent" does not include it. And each provider fetch in the initializer now fires for every user at page load, also for users who never open the picker. Thus the fetch moved out:

```csharp
// Trending is deliberately NOT fetched here: the picker mounts hidden at page
// load, and the provider call would fire for users who never open it. The
// first OnPickerOpened pays it instead.
```

The replacement for "init runs on open" is an explicit open hook. The picker registers itself with chat.js at the first render:

```csharp
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;
    _dotNetRef = DotNetObjectReference.Create(this);
    await JS.InvokeVoidAsync("registerPickerOpenHook", rootElement, _dotNetRef);
}
```

Then chat.js calls it back each time the pane becomes visible (the `notifyPickerOpened` from the toggle listener). Per-open freshness lives there, not in `OnInitialized`:

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

The timing is the good part. The picker is already open and painted when this hook fires. Trending arrives one round trip later and appears behind the open UI. The round trip still occurs. It only stopped as a gate for the open. Favorites and the server GIF library need nothing here at all, because they are event-driven. Service events (`OnFavoritesChanged` and others) keep them current while the picker is hidden.

The hook must exist for a structural reason, not only for convenience. The `PickerPane` firewall blocks parameter flow into the hidden subtree. That was the goal. Thus no per-open data can travel on the normal Blazor parameter mechanism. The render firewall closes the parameter path, thus each per-open signal must go through the open hook.

## Problem 3: the tap that comes before the mount

The 1.2-second delay contains a race. A fast user can tap the GIF button before the background mount ran. The naive result is a dead button. The correction works from both sides.

The JS side sees that there is nothing to show and asks Blazor to mount now:

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

Note what occurred before this call. The toggle listener already set `container.dataset.picker = "gif"`. The client-owned state says "the GIF picker is open". There is only no pane in the DOM yet. Thus when `EnsurePickersMounted` renders one a round trip later, the CSS selector `[data-picker="gif"] [data-picker-pane="gif"]` matches at the moment the element exists, and the picker appears already open. The user does not tap a second time, and no reconciliation code is necessary. The declarative CSS did the reconciliation.

The Blazor side closes the loop. The late picker missed its `OnPickerOpened` call, because the call fired into an empty DOM. Thus the registration hook checks if the new component arrives in a pane that is already open:

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

Thus the early tap degrades to exactly one round trip of wait, which is what every open cost before. And it applies only to a user who taps in the first second after page load. That is the honest cost of this pattern, and we accepted it.

## What the hidden picker does not cost

One last detail makes "always mounted" almost free. The weight of a GIF picker is not its DOM. It is the images. Every cell renders with `loading="lazy"`, and lazy images and the reveal mechanism work well together. Elements inside `display: none` have no layout. Images with no layout are never "near the viewport". Thus the hidden picker downloads zero images. The browser starts to fetch previews only when the attribute flips and the pane gets geometry. The DOM is ready before the first open, and the bandwidth cost comes only when the user opens the picker.

The always-mounted grid also opens a follow-on win, covered in the companion article. Search does not query the server for a new render. It becomes an in-place client-side filter over the mounted cells, immediate on any connection.

## The division of labor

Step back, and the division of labor is clean:

| Concern | Owner |
|---|---|
| Is the picker open? | Client: `data-picker` attribute + CSS |
| What is in the picker? | Blazor: `GifPicker` renders content, services push updates |
| Protection of the hidden subtree | `PickerPane`: `ShouldRender() => false` |
| Per-open freshness | The `OnPickerOpened` JSInvokable hook |
| The early-tap race | Both sides: `EnsurePickersMounted` + the hook's already-open check |

Blazor never learns that the picker opened, except through the deliberate open-hook ping. JS never learns what a GIF is. Each side owns the thing it does well: the client owns visibility, and the server owns content.

If you keep one checklist, keep this one. An always-mounted Blazor Server popup needs four pieces, and each piece that you skip causes a defect:

1. Delay the mount a moment after page load, with a JSInvokable rush path for taps that come first.
2. Put a firewall around the subtree (a `ShouldRender() => false` wrapper). Without it, the host renders diff your hidden DOM forever. Use `@key` on the wrapper when the content shape must change.
3. Add an open hook for per-open freshness. The firewall blocks parameters, thus refresh-on-open must arrive by an explicit call.
4. Let CSS reconcile state and DOM. A client-owned `data-*` attribute plus an attribute selector means that a pane that mounts late also appears in the correct state at the moment it exists.

With these four pieces, a picker on a 900 ms connection opens at the same cost as on localhost: one attribute, one frame, zero round trips.

---

*Yap is open source. This pattern lives in [`MessageInput.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/MessageInput.razor), [`PickerPane.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/PickerPane.razor), [`GifPicker.razor`](https://github.com/urza/Yap/blob/main/Yap/Components/GifPicker.razor), and [`chat.js`](https://github.com/urza/Yap/blob/main/Yap/wwwroot/js/chat.js).*
