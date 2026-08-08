# GIF Picker Preview Pipeline

**Status: implemented 2026-08-08 — needs `dotnet ef migrations add GifPreviewUrl` + build + prod test**

## Problem

The GIF picker's Favorites / Recent / Server / local-search cards (and the Settings GIF manager +
Admin GIFs table) rendered the **full chat-size file** into ~220px cells. Custom uploads are stored
copy-as-is (median 2.6MB, p90 6.1MB, max ~10MB in the current library); provider files are md-tier
webp/gif (median ~314KB, p90 2.4MB). Meanwhile Trending/search provider cards use Klipy's `sm` tier
(~25–100KB) — so a favorites-heavy tab was ~10–30× heavier per card than Trending. A cold open
lazy-loads ~15–25 cards ≈ tens of MB on a slow mobile link, plus the decode/animation cost of
full-resolution files every open.

Caching itself was already correct and unchanged: in-memory `GetFavorites`, immutable HTTP caching
on `/gif-cache` + `/uploads/gifs`, service-worker cache-first, one shared URL between chat and
picker. The gap was purely payload size.

## Design

Every `GifEntry` heavy enough to matter gets a derived **`PreviewUrl`** →
**`/gif-cache/{id}.p.webp`** — animated WebP, `fps=12, scale=min(320,iw), q:v 60`.

Measured on the real library (ffmpeg master 2026, ~0.5s per file):

| Source | Full | Preview |
|---|---|---|
| 480×480 custom webp | 10.1MB | 180KB (57×) |
| 480×480 custom webp | 7.5MB | 322KB (24×) |
| 480×480 custom webp | 6.1MB | 190KB (33×) |
| 626×640 provider webp | 3.5MB | 568KB (6.4×) |
| provider gif | 3.4MB | 396KB (8.6×) |

Decisions:

- **Previews live in `Data/gif-cache/` for all entries**, including custom uploads whose full file
  is under `wwwroot/uploads/gifs/`. They're derived, regenerable data; gif-cache is the sweepable
  derived-data home. Both cache layers (HTTP immutable + SW `MEDIA_CACHE`) already cover the path.
- **Skip-small rule**: full file < 300KB (`PreviewMinSourceBytes`) → no preview, `PreviewUrl` stays
  null, renderers fall back to the full file. Avoids previews that barely differ from sources.
- **Chat is untouched** — `MessageItem` still embeds the full-size file. Only picker + management
  grids use previews (Admin/manager thumbs link out to the full file).
- **`DeleteLocalFiles` needed no change** — its `{entryId}.*` glob already matches `{id}.p.webp`.
- Pack **export** never includes previews (derived); **import** regenerates them per file via the
  `TryAcceptAsGifAsync` hook (adds ~0.5s/file to imports).

## ffmpeg requirement (verified empirically)

- **Animated-WebP sources decode only on ffmpeg ≥ 7.1.** Below that (tested 7.0.2 and a 2024-06
  git build) the transcode **fails hard and clean**: "skipping unsupported chunk: ANIM …
  image data not found", non-zero exit, 0-byte output which the helper deletes. So on old ffmpeg
  previews are simply skipped — no guard code, no corrupt output. `.gif` and video sources
  transcode on any ffmpeg.
- **Docker prod is fine**: `Yap/Dockerfile` installs ffmpeg from the `aspnet:10.0` base image's
  Debian trixie repos = **ffmpeg 7.1.5**. Verify in the running container once:
  `docker exec <container> ffmpeg -version`.
- Video-source entries sidestep the requirement entirely: their preview is cut from the temp
  mp4/webm while it's still on disk, not from the produced webp.

## Generation timing

1. **Entry creation, inline** (~0.5s, behind existing spinners/background work):
   - `NormalizeProviderEntryAsync` — gif/webp branch: after download, from the local file.
     Video branch: from the temp video before deletion (+ an adopt call after, covering the
     file-already-existed restart path).
   - `TryAcceptAsGifAsync` — before `PersistNewEntryAsync` (so the insert carries `PreviewUrl`);
     source = original video when there was one, else the copied gif/webp.
2. **Startup backfill** (`BackfillPreviewsAsync` from `InitializeAsync`): entries with
   `PreviewUrl == null`, resolved via `ResolveLocalFile`; runs in the background through the
   `SemaphoreSlim(2)` transcode gate; persists via targeted `ExecuteUpdate`; fires
   `OnGifEntryUpdated` per file so open pickers upgrade live. Adopt-if-exists makes interrupted
   runs resume free. Entries pending normalization are invisible to it (no local URL yet) —
   normalization generates their preview itself, so there's no double-transcode race.
3. Never on-demand in the picker-open path.

`TryGeneratePreviewAsync` is the single entry point: PreviewUrl-already-set → true;
no ffmpeg → false; adopt existing file; threshold check; transcode; set URL.

## Files touched

- `Models/GifEntry.cs` — `PreviewUrl` property
- `Data/ChatDbContext.cs` — `HasMaxLength(512)` ⇒ **migration required: `dotnet ef migrations add GifPreviewUrl`**
- `Services/Gifs/GifFfmpegHelper.cs` — `TranscodeToPreviewWebpAsync` + shared core
  (also migrates `-vsync 0` → its successor `-fps_mode passthrough`, same semantics)
- `Services/Gifs/GifService.cs` — `PreviewMinSourceBytes`, `TryGeneratePreviewAsync`,
  hooks in both normalize branches + upload accept, `PersistFormatsAsync` column,
  `BackfillPreviewsAsync` + `InitializeAsync` trigger
- `Components/GifPicker.razor` — `PickLocalPreviewSource` prefers `PreviewUrl` (covers cards
  **and** folder tiles on Favorites/Server/Browse)
- `Components/GifLibraryManager.razor` — grid thumbs preview-first
- `Components/Pages/Admin.razor` — GIFs-tab thumb preview-first (link opens full file);
  media-upload-log gif thumbs likewise

## Test checklist

- [ ] `dotnet ef migrations add GifPreviewUrl` from `Yap/`, then run — migration applies clean
- [ ] Startup log: "Backfilling picker previews for up to N GIF entries" → "Generated M GIF picker previews (backfill)"; `Data/gif-cache/` gains `{id}.p.webp` files
- [ ] Favorites tab: cards load visibly faster; DevTools network shows `.p.webp` requests (~100–600KB) instead of multi-MB files
- [ ] Small entries (<300KB) still render (full-file fallback, no preview generated)
- [ ] Send a *new* provider GIF (video-only item too, e.g. one that arrives as mp4) → entry gets a preview without restart
- [ ] Upload a custom .webp/.gif/.mp4 through the picker → preview exists immediately; upload overlay not noticeably longer
- [ ] Settings → GIF manager + Admin → GIFs tab: thumbs light, Admin thumb click opens full file
- [ ] Chat messages still render full-size files (no preview URLs in message HTML)
- [ ] Delete an owned GIF → both `{id}.webp` and `{id}.p.webp` disappear from disk
- [ ] Second restart: backfill logs nothing new (all previews adopted/present)
- [ ] Prod container: `ffmpeg -version` ≥ 7.1
