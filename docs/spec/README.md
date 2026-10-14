# Yap product specification

This folder is the source of truth for what Yap does, as the user sees it. It describes behaviour and appearance, not implementation. A page here is correct when a user who reads it knows exactly what will happen, and an implementer who reads it knows exactly what to build.

The pages are Markdown so that people and agents read the same text. `tools/build.py` renders them to HTML with screenshots in `html/`. The Markdown is the source; never edit the HTML.

## How the pages are organised

1. **Yap** (`yap.md`). What the product is: accounts, rooms, direct messages, messages and media, presence, notifications, offline, installation on a phone, settings and administration. One page, read first.
2. **General rules** (`general-rules.md`). Rules that every component follows: every input acts locally first, sending never moves focus, what is private, how mutes and sounds decide, what the user sees in each connection state.
3. **Components**. Things you can point at on the screen. One page each, same shape: purpose, anatomy with a screenshot, rules, states, wording.
4. **Feature areas**. Behaviour that several components show. One page each: presence, notifications and unread counts, sign-in and sessions, offline and installation.

Component pages and feature pages cross-link. A component page says what a part looks like and how it reacts. A feature page says when and why.

## Pages

| Page | Prefix | Status |
| --- | --- | --- |
| [Yap](yap.md) | YAP | planned |
| [General rules](general-rules.md) | GEN | planned |
| [Input box](input-box.md) | IB | written |
| [Message row and actions](message-row.md) | MR | draft, in review |
| [Emoji and GIF pickers](pickers.md) | PK | draft, in review |
| [Timeline and media](timeline.md) | TL | draft, in review |
| [Header, sidebar and overlays](frame.md) | SH | draft, in review |
| [Settings](settings.md) | ST | planned |
| [Administration](admin.md) | AD | planned |
| [Presence](presence.md) | PR | planned |
| [Notifications and unread](notifications.md) | NT | planned |
| [Sign-in and sessions](sessions.md) | AU | planned |
| [Offline and installation](offline.md) | OF | planned |

## Rules and their IDs

Every rule has an ID made of the page prefix and a number, for example `IB-04`. IDs are stable. A rule that is removed keeps its number and is marked retired; numbers are never reused. Tests, issues and commits cite rule IDs.

A rule is one sentence that says what happens, in the present tense. Where the reason is not obvious, one more sentence gives it. A rule never names a mechanism. "The draft is kept when the user switches conversations" is a rule. "The draft is written to local storage on every keystroke" is not. <!-- lint: allow -->

## Page shape

Every component page has these sections in this order. A feature page has the same sections without Anatomy.

1. **Purpose.** One paragraph. What the part is for.
2. **Anatomy.** A screenshot with the parts numbered, and one line per part.
3. **Rules.** The numbered rules.
4. **States.** A table: the state or trigger, the input type (mouse, touch, keyboard), and what the user sees.
5. **Wording.** Every text the user can read in this part, verbatim.
6. **Related.** Links to the pages this one depends on.

`tools/page-template.md` is the empty template.

## Screenshots

Screenshots are generated, never pasted. A Playwright script captures each named state against a running instance with seeded data and writes to `img/`. A screenshot that no longer matches the page is a bug in one of the two.

## Tools

```
python3 docs/spec/tools/lint.py     # IDs unique, links resolve, no implementation words
python3 docs/spec/tools/build.py    # Markdown to html/
```

The lint refuses words that describe an implementation instead of a behaviour. The list is in `tools/lint.py`. A page that needs one of those words for a good reason marks the line with `<!-- lint: allow -->`.
