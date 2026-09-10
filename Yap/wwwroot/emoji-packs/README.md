# Built-in emoji packs

Image emoji that **ship with every instance** — committed to the repo, so they land in the build
output of every deployment. Unlike `Data/custom-emojis/` (per-deployment, gitignored), nobody has
to drop files on the server for these to show up.

These are pictures, not Unicode: they have no codepoint and are addressed by `:shortcode:`, exactly
like server customs. Everything downstream already understands that form — messages, reactions,
quick reactions, recents, search, and the emoji-only "big emoji" sizing.

## Adding a pack

Create a folder here and drop images in it:

```
wwwroot/emoji-packs/
└── blobs/
    ├── blobwave.png
    ├── blobthink.png
    └── blobparty.gif
```

- **Folder name** = pack name → picker tab + section header ("Blobs"). Must match `^[a-zA-Z0-9_-]+$`.
- **Filename** = the shortcode → `:blobwave:`. Same charset rule; lowercased.
- **Formats**: `.png` `.svg` `.gif` `.webp` `.jpg` `.jpeg`. Animated GIF/WebP animate inline.
- **Sizing**: rendered into a square box with `object-fit: contain`, so square art (128×128 is a
  good default) looks best; anything else letterboxes rather than distorts.
- Order within a pack is alphabetical by shortcode; packs themselves sort A–Z after "Custom".
- The folder is scanned **once at startup** — restart the app after adding files.

## Search keywords (`keywords.json`)

The picker searches each emoji by its shortcode, so `:blobwave:` is found by "blob" or "wave" and
by nothing else. To add search terms, drop a `keywords.json` next to the images:

```
wwwroot/emoji-packs/blobs/
├── keywords.json
├── blobwave.png
└── blobthink.png
```

```json
{
  "blobwave": "hello hi greeting bye",
  "blobthink": ["hmm", "ponder", "suspicious"]
}
```

- Keys are shortcodes (the filename without extension), case-insensitive. A key with no image file
  is logged as a warning at startup — that is how you catch a typo.
- Values are a string or an array of strings. Both end up as one space-separated, lowercased blob.
- Matching stays plain substring, so "greet" finds `blobwave` but "olleh" does not.
- The file is optional, per folder (`Data/custom-emojis/` may have its own), and never fatal: bad
  JSON is logged and those emoji stay searchable by shortcode alone.
- Comments and trailing commas are allowed, so the file can carry notes.

## One flat namespace

All shortcodes — built-in packs and server customs alike — share a single namespace, so `:party:`
can only mean one thing. Resolution order:

1. `Data/custom-emojis/` (server customs) — **wins**, so a deployment can deliberately replace a
   shipped emoji just by naming its file the same.
2. Built-in packs, alphabetically.

A shadowed emoji is logged at startup and hidden from its pack.

## Removing a shipped emoji is a soft break

Messages store the literal `:shortcode:` text, so deleting an emoji makes every old message
containing it render as plain `:shortcode:` text. It degrades gracefully, but treat a pack as
effectively append-only once it's been deployed.
