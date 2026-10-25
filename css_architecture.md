# CSS

The client is static files; there is no build step and no scoping. The stylesheets load in this order,
and the order matters: a later file wins a tie.

| File | Holds |
| --- | --- |
| `wwwroot/app.css` | fonts, CSS variables, resets, the body, and the rules of the vanilla client that the component files do not have (the emoji sizes, the pending row, the pill, the connection dot, the notice strip, the start-failure screen) |
| `wwwroot/themes.css`, `wwwroot/themes/teahouse.css` | one block per theme; `[data-theme]` on `<html>` picks it, `[data-scene]` picks the Tea House picture by the sun (sunrise and sunset of the device's time zone city) |
| `wwwroot/css/frame.css` | header, sidebar, status menu, profile card, the notice dots |
| `wwwroot/css/pickers.css` | the emoji picker, the GIF picker, the phone sheet |
| `wwwroot/css/timeline.css` | the message list, the loading row, the top note, the gallery viewer |
| `wwwroot/css/composer.css` | the input box, the reply bar, the typing strip, the drop ring |
| `wwwroot/css/message.css` | one message row: header, content, media, reactions, action bar, editor, cards |
| `wwwroot/css/pages.css` | login, invite, verify, settings, channel settings, admin, the GIF library manager |

The six files under `wwwroot/css/` were flattened from the component stylesheets of the earlier
Blazor client. `wwwroot/css/README.md` lists the class names each component expects and the root classes
(`.page-login`, `.page-admin`, `.emoji-picker`, ...) that stand in front of selectors whose names collided.
The JavaScript sets state through classes and a few data attributes (`data-picker`, `data-active-tab`,
`data-search-miss`, `data-search-empty`, `data-context`, `data-dragging`, `data-composing`); the README names them.

Two width tiers apply: 768 px for the sidebar and the people count, 600 px for the compact components
(`docs/spec/general-rules.md`, GEN-14). The JavaScript reads the 600 px tier with the same media query
(`ui/overlays.js`).

The same working tree is built from Windows and from Linux. Keep `bin/` and `obj/` out of a mixed build
(see `CLAUDE.local.md`); the static files themselves need no build.
