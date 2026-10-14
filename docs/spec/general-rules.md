# General rules

Rules that every component follows. A component page may make one of these more precise; it may not contradict it.

## Every input acts at once

**GEN-01** An input that can act locally acts in the user's own tap or keystroke: typing, opening a picker, picking an emoji, sending, reacting, scrolling. The screen changes before the server has answered; the server's answer confirms or corrects. What must come from the server, such as search results, older messages and uploads, shows a loading state and never blocks the rest.

**GEN-02** When the server refuses an action, the user sees why at the place where the action happened. A refused change returns the screen to its state before; a refused message stays as a failed row with Retry and Remove.

**GEN-03** The keyboard on a phone opens only when the user taps a text field or starts an action whose point is to type, such as Reply or Edit, and closes only when the user leaves the field or taps something that takes its place.

## Privacy

**GEN-04** The text of a direct message is visible only to its two people; the admin sees that it happened and its uploads on the administration page. A room is visible to every account on the server.

**GEN-05** Signing out, or learning of a sign-out made elsewhere, removes everything of the account from the device: drafts, unsent messages, cached conversations and the notification subscription. A different account signing in on the same device starts from nothing. Device conveniences such as the last route and dismissed prompts stay.

**GEN-06** An invisible person is shown to others as offline and is not counted as online. Admins see the real status, and typing shows the person like anyone else.

## Sounds, counts and mutes

**GEN-07** The conversation a message arrives in decides whether it counts, sounds and notifies. Its mute setting applies; the open conversation's setting does not. The kind of page that is open decides which arrivals the tab title and the sound announce.

**GEN-08** The user's own messages never count, sound or notify.

**GEN-09** A muted conversation shows that something arrived, as a dot, and nothing else. A person who had Yap closed gets no dot for a muted room.

## Connection

**GEN-10** The app stays readable and usable without a connection. Reading what is loaded, writing, and queuing messages work. Actions that need the server say "Needs a connection" where they are taken, or are dimmed.

**GEN-11** The connection state is shown in the header and, for a state that needs the user, in the notice strip, never as a layer over the content.

## Texts and look

**GEN-12** The playful texts that vary come from lists configured on the server, each with a plain fallback. A text is picked when its context appears and stays until the context changes.

**GEN-13** Themes change colours, patterns, tints and glows. They never change behaviour, sizes of controls, or the meaning of the status colours.

**GEN-14** Two width tiers apply: 768 px for the sidebar and the people count, 600 px for the compact components and the header's density. Both depend on width, not on the device. Hover and touch rules depend on the input, not on width.

**GEN-15** On a phone every text field uses 16 px text, so the page never zooms when a field takes focus.

**GEN-16** Every control that shows on hover on desktop is always visible, or reachable by a tap, on a touch screen.

## Shared behaviour

**GEN-17** A tap outside a popup closes it. Escape closes the topmost open layer only.

**GEN-18** On a phone, pickers and menus open as bottom sheets over a dimmed page.

**GEN-19** A person is shown by display name, or by username when there is none.

**GEN-20** Emoji show as images from one set, so everyone sees the same picture.

**GEN-21** Drafts and unsent messages survive a reload, a new version and the error screen.

**GEN-22** A change made elsewhere, on another device or by another person, shows at once.

**GEN-23** The account owns the profile, the theme and text size, the time format, the status, the mutes, the recent emoji, the favourites and the unread counts. The device owns the drafts, the pending messages, the cached conversations, the notification subscription, the last route and the dismissed prompts.
