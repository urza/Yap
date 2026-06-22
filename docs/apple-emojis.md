# Apple-style Emoji Rendering

Documentation for the Apple emoji feature. Audience: future maintainers (you and Claude).

## TL;DR

Yap renders **Apple-design emoji images for everyone** by reusing the existing unicode → `<img>`
pipeline but pointing at Apple-style PNGs instead of Twitter's Twemoji. The old Twemoji code is
**kept intact in the codebase as a backup** (just not wired into the UI).

Per-emoji resolution order:

1. **Self-hosted override** — `wwwroot/emoji-fallback/{codepoint}.png` (for emoji missing or
   outdated in the CDN, e.g. new Unicode releases). Auto-discovered by a folder scan.
2. **emoji-datasource-apple CDN** — `cdn.jsdelivr.net/npm/emoji-datasource-apple@16.0.0` (primary
   source for ~everything).
3. **Twemoji CDN** — `cdn.jsdelivr.net/gh/jdecked/twemoji@latest` (final `onerror` fallback for
   anything the CDN is missing).

> **Why images at all?** We deliberately keep the "swap unicode for an image" model (rather than
> rendering native OS emoji) so every platform sees a consistent Apple design. This was an explicit
> product choice.

## Why this approach (background)

- Apple does **not** distribute its emoji as downloadable image assets — Apple Color Emoji is a
  proprietary OS font. The only way to show Apple art on *non-Apple* devices is to host Apple-style
  PNGs. We use the community `emoji-datasource-apple` image set (Apple-style art) served from jsDelivr.
- Licensing was explicitly ruled out of scope for this project (non-commercial app); that's the
  maintainer's call, not encoded here.
- We considered native-unicode rendering (real Apple emoji only on Apple devices, Twemoji elsewhere)
  but rejected it: it gives up cross-platform consistency. See the planning doc
  `~/.claude/plans/per-apple-guidelines-users-dynamic-metcalfe.md` for the full option analysis.

## Code changes

### New: `Yap/Services/EmojiService.Apple.cs`

A **second partial** of `EmojiService` (the class was already `partial`). It contains *all* Apple
logic and reuses the existing private helpers from `EmojiService.cs` (`EmojiRegex()`,
`CustomEmojiShortcodeRegex()`, `ShouldMergeEmoji`, `IsEmojiOnlyMessage`, `_customEmojiService`,
and crucially `GetCodePoint()` for the Twemoji-fallback URL).

Public methods (the "Apple twins" wired into the UI):

| Apple method (active) | Twemoji twin (backup, untouched) |
|---|---|
| `ConvertEmojisToApple(text, forceSmall, inline, withFallback=true)` | `ConvertEmojisToTwemoji(text, forceSmall, inline)` |
| `ProcessMessageContentApple(text)` | `ProcessMessageContent(text)` |
| `GetAppleEmojiHtml(emoji)` | `GetPickerEmojiHtml(emoji)` |

Key internals:

- **`ResolveAppleCodePoint(emoji)`** — turns an emoji sequence into the emoji-datasource filename
  (no extension). Uses `AppleMap` for bare/legacy/keycap emoji; otherwise a direct computation.
- **`NormalizeCodePoints(emoji, keepFe0f)`** — hyphen-joined lowercase hex, each codepoint padded
  to ≥4 digits (`ToString("x4")`), optionally dropping `FE0F`.
- **`AppleMap`** (lazy, from embedded `emoji.json`) — maps a bare (FE0F-stripped) codepoint key to
  the fully-qualified filename. Only entries whose qualified form contains `FE0F` are stored (the
  rest match the direct computation), so the map is small.
- **`LocalOverrides`** (lazy folder scan of `wwwroot/emoji-fallback/`) — the set of codepoints that
  have a self-hosted PNG.
- **`BuildAppleImg(...)`** — emits the `<img>`: local override if present, else CDN, plus the
  `onerror` → Twemoji fallback when `withFallback` is true.
- **`GetAppleEmojiHtml`** passes `withFallback: false` — the picker's ~1400 cells are all curated
  emoji present in the CDN, so the fallback markup would only bloat them.

### New: `Yap/Data/emoji.json` (embedded resource)

The `emoji-datasource` metadata file (the `unified` codepoints used to build `AppleMap`). Embedded
via `Yap.csproj`:

```xml
<ItemGroup>
  <None Remove="Data\emoji.json" />
  <EmbeddedResource Include="Data\emoji.json" />
</ItemGroup>
```

Read at runtime with `Assembly.GetManifestResourceStream` (no constructor/DI change needed). It is
**not** web-served (it's inside the DLL).

### New: `Yap/wwwroot/emoji-fallback/`

Self-hosted Apple PNGs for emoji the CDN lacks/outdates. Served at `/emoji-fallback/*` by the
existing `app.UseStaticFiles()`. Current contents:

| File | Emoji | Codepoint | Why |
|---|---|---|---|
| `1faea.png` | 🫪 distorted face | U+1FAEA | Unicode 17 — missing from @16 |
| `1faef.png` | 🫯 fight cloud | U+1FAEF | Unicode 17 — missing from @16 *and* Twemoji |
| `1facd.png` | orca | U+1FACD | Unicode 17 — missing from @16 |
| `1f3f4-200d-2620-fe0f.png` | 🏴‍☠️ pirate flag | ZWJ seq | override @16's older art |

### Wired active (call-site swaps only)

The unicode-emoji calls in 6 components were pointed at the Apple twins
(`ConvertEmojisToTwemoji`→`ConvertEmojisToApple`, `ProcessMessageContent`→`ProcessMessageContentApple`,
`GetPickerEmojiHtml`→`GetAppleEmojiHtml`). **`RenderCustomEmoji` was left alone** — custom
`:shortcode:` emoji are local images, identical under both sets.

Components: `MessageItem.razor`, `EmojiPicker.razor`, `ChatSidebar.razor`, `ChatHeader.razor`,
`UserProfileCard.razor`, `Pages/ChannelSettings.razor`.

### Untouched (backup)

`Yap/Services/EmojiService.cs` (all Twemoji methods, caches, `GetCodePoint`) and
`Yap/Services/EmojiData.cs`. No model / migration / settings / DI changes.

## The filename convention (the important detail)

`emoji-datasource` names files by the **fully-qualified `unified` codepoint, lowercased**:
`FE0F` is **kept**, and each codepoint is **zero-padded to ≥4 hex digits**.

| Emoji | emoji-datasource file | Twemoji file (backup) |
|---|---|---|
| 😀 U+1F600 | `1f600.png` | `1f600.svg` |
| ❤️ U+2764 FE0F | `2764-fe0f.png` | `2764.svg` (FE0F stripped) |
| ©️ U+00A9 FE0F | `00a9-fe0f.png` | `a9.svg` (minimal width) |
| #️⃣ U+0023 FE0F 20E3 | `0023-fe0f-20e3.png` | `23-20e3.svg` |
| 🏳️‍🌈 …FE0F‍🌈 | `1f3f4-fe0f-200d-1f308.png` | same |

This is the **inverse** of Twemoji's `GetCodePoint` (strips FE0F, minimal width). Because
`EmojiData.cs` stores its picker emoji **bare** (no FE0F), bare legacy/keycap emoji wouldn't match
the qualified filenames — that's exactly what `AppleMap` bridges. Astral emoji, skin-tone sequences,
and FE0F-qualified keyboard input resolve directly without the map.

URLs:

- CDN: `https://cdn.jsdelivr.net/npm/emoji-datasource-apple@16.0.0/img/apple/64/{codepoint}.png`
  (64px is the largest individual size the package ships).
- Local: `/emoji-fallback/{codepoint}.png`
- Twemoji fallback: `https://cdn.jsdelivr.net/gh/jdecked/twemoji@latest/assets/svg/{twemojiCodepoint}.svg`

---

## How to add / fix an emoji (the part you'll come back for)

You'll need this in two situations: **(A)** an emoji renders broken, or **(B)** a new Unicode set
drops and you want the new art.

### The foolproof recipe

1. **Find the codepoint the app is looking for.** Send the emoji in the app, open DevTools →
   **Network**, filter for `emoji-datasource`. The failing (404) request is
   `…/img/apple/64/XXXX.png` — **`XXXX` is exactly the name your file must have.** (This sidesteps
   all the FE0F/padding rules — the app tells you the name.)
2. **Get an Apple PNG** for that emoji (e.g. from [emojipedia](https://emojipedia.org) — the page's
   image; its filename usually contains the codepoint too).
3. **Save it as `wwwroot/emoji-fallback/XXXX.png`** (same `XXXX` from step 1).
4. **Restart the app.** The override folder is scanned once at startup. (`UseStaticFiles` serves the
   file at runtime; the scan is what registers it for the lookup — so a restart is required.)

That's it. Local overrides win over the CDN, so this also works to **replace** an emoji whose CDN
art is outdated (e.g. the pirate flag).

### Naming without DevTools (manual codepoint rules)

If you'd rather derive the name: take the emoji's Unicode codepoints (emojipedia lists them under
"Codepoints"), lowercase the hex, drop the `U+`, and join multiple with `-`:

- **Single new emoji** (most common): just the hex — `U+1FAEF` → `1faef.png`.
- **Sequences** (ZWJ flags/professions, keycaps): include every codepoint *including* `fe0f` and
  `200d`, in order — 🏴‍☠️ `U+1F3F4 U+200D U+2620 U+FE0F` → `1f3f4-200d-2620-fe0f.png`.
- Pad any codepoint shorter than 4 hex digits to 4 (`U+0023` → `0023`). Astral emoji (U+1Fxxx) are
  already ≥4.

If a file is mis-named it simply won't match (the emoji falls through to CDN/Twemoji). When in
doubt, use the DevTools recipe.

### When a newer `emoji-datasource-apple` ships (e.g. v17)

The cleaner long-term fix for "new Unicode set" is to bump the package instead of self-hosting each
emoji:

1. Update the version in **two** places to keep the map and images in sync:
   - `AppleCdnBase` in `Services/EmojiService.Apple.cs` (the CDN URL).
   - `Yap/Data/emoji.json` — re-download from the matching version:
     `curl -sSL https://cdn.jsdelivr.net/npm/emoji-datasource-apple@<NEW_VER>/emoji.json -o Yap/Data/emoji.json`
2. Rebuild (the embedded resource is baked at build time).
3. Remove any now-redundant files from `wwwroot/emoji-fallback/` (the CDN now has them). Leftover
   overrides aren't harmful — local always wins — but clean them up to avoid stale art.

### Edge cases

- **Emoji missing from the CDN *and* Twemoji** (e.g. fight cloud was 404 on both) → it *must* be
  self-hosted, or it renders broken. There's no other source.
- **Skin-tone variants** of an overridden base aren't covered by a single file — e.g. a local
  `1f483.png` (💃) does not cover `1f483-1f3fd` (💃🏽). Add each variant's file if needed (the CDN
  usually has skin-tone variants already, so this is rare).
- **Picker** uses `GetAppleEmojiHtml` (no Twemoji `onerror`). Its emoji come from the curated
  `EmojiData.cs` set and are all in the CDN, so they never need fallback. Brand-new emoji generally
  aren't in `EmojiData.cs`, so they won't appear in the picker until added there — but they still
  render correctly in messages/reactions via the fallback chain.

## Reverting to Twemoji

Everything Twemoji is still present and working in `EmojiService.cs`. To roll back:

1. In the 6 components, swap the call sites back: `ConvertEmojisToApple`→`ConvertEmojisToTwemoji`,
   `ProcessMessageContentApple`→`ProcessMessageContent`, `GetAppleEmojiHtml`→`GetPickerEmojiHtml`.
2. Optionally delete `Services/EmojiService.Apple.cs`, the `<EmbeddedResource>` line + `Data/emoji.json`,
   and `wwwroot/emoji-fallback/`.

(Grep `ConvertEmojisToApple\|ProcessMessageContentApple\|GetAppleEmojiHtml` to find every call site.)

## Verify after changes

1. `dotnet build`, run, hard-refresh (bypass cached Twemoji SVGs + service worker).
2. Picker shows Apple-style PNGs; Network shows `…emoji-datasource-apple@16.0.0/img/apple/64/*.png`
   returning 200.
3. Legacy/keycap (❤ ☀ © `#️⃣`) and the self-hosted ones (🫪 🫯 orca 🏴‍☠️) render — no 404s — from
   both the picker and after sending.
4. Force a fallback: an emoji the CDN lacks should swap to a `…/jdecked/twemoji…svg` (DevTools).
5. Reactions, emoji-only "jumbo" sizing, inline emoji in names, and custom `:shortcode:` emoji all fine.
