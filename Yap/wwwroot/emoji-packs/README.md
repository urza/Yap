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
