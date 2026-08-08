# GIF Picker Preview Pipeline

**Status: deployed 2026-08-08. Prod follow-up the same night: animated-WebP decode turned out to
need ffmpeg ≥ 9.0 (noble 6.1, resolute 8.0.1, even 8.1.2 all fail) — Dockerfile now ships pinned
static ffmpeg 9.0 (`mwader/static-ffmpeg`) on the default `aspnet:10.0` base. Rebuild + redeploy,
then the startup backfill heals every skipped entry.**

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

- **Animated-WebP sources need ffmpeg ≥ 9.0.** The decoder landed on master after the 8.1 branch;
  no 6.x/7.x/8.x release has it — old builds log "skipping unsupported chunk: ANIM/ANMF … image
  data not found" and exit 69 (decode-error-rate-exceeded) with a 0-byte output the helper
  deletes. Previews are simply skipped (PreviewUrl stays null, renderers fall back) and the
  startup backfill retries them after any ffmpeg upgrade. `.gif` and video sources transcode on
  any ffmpeg. The original "≥ 7.1" claim here was an unverified inference — only 7.0.2, a 2024-06
  git build and master had actually been tested. Verified matrix (2026-08-08, real library
  files): 6.1.1 ❌ (prod noble) · 7.1.5 ❌ · 8.0.1 ❌ (prod resolute) · 8.1.2 ❌ ·
  master-2026-08-07 ✅ · **9.0 ✅** (dev Gyan build and `mwader/static-ffmpeg:9.0`,
  byte-identical preview outputs to master).
- **Docker resolution (after two wrong turns the same night)**: .NET 10's bare `aspnet:10.0` tag
  is Ubuntu noble (ffmpeg 6.1.1), and Microsoft publishes **no Debian variants** for stable
  .NET 10 (the "trixie = 7.1.5" note was wrong); re-pinning to `10.0-resolute` (8.0.1) still
  failed because no release has the decoder at all. Final fix: base back on plain `aspnet:10.0`;
  ffmpeg/ffprobe come from **`COPY --from=mwader/static-ffmpeg:9.0`** (pinned, multi-arch,
  ffprobe included) and ffmpeg is dropped from apt. Verify:
  `docker exec <container> ffmpeg -version` → 9.0.
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
- [ ] Prod container: `ffmpeg -version` → **9.0** (static). History: noble 6.1.1 failed, resolute
  8.0.1 failed too — animated-WebP decode is a ≥ 9.0 feature, see above. After the static-9.0
  rebuild, the backfill should generate previews for every webp-source entry with zero exit-69
  warns (the improved helper warn — source path + stderr tail — stays for future cases).
