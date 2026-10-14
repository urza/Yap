# General rules

Rules that every component follows. A component page may make one of these more precise; it may not contradict it.

## Every input acts at once

**GEN-01** An input acts in the user's own tap or keystroke: typing, opening a picker, picking an emoji, sending, reacting, scrolling. The screen changes before the server has answered. The server's answer confirms or corrects, it never gates.

**GEN-02** When the server refuses an action, the user sees why at the place where the action happened, and the screen returns to the state before the action.

**GEN-03** Nothing that the user did not ask for moves the keyboard on a phone. Focus moves into a text field only when the user taps it or starts an action whose point is to type, such as Reply or Edit.

## Privacy

**GEN-04** A direct message is visible only to its two people. A room is visible to every account on the server.

**GEN-05** Signing out removes everything of the account from the device: drafts, unsent messages, cached conversations and the notification subscription. A different account signing in on the same device starts from nothing.

**GEN-06** An invisible person is shown to others as offline and is not counted as online.

## Sounds, counts and mutes

**GEN-07** The conversation a message arrives in decides whether it counts, sounds and notifies. Its mute setting applies; the open conversation's setting does not.

**GEN-08** The user's own messages never count, sound or notify.

**GEN-09** A muted conversation shows that something arrived, as a dot, and nothing else.

## Connection

**GEN-10** The app stays readable and usable without a connection. Reading what is loaded, writing, and queuing messages work. Actions that need the server say "Needs a connection" where they are taken.

**GEN-11** The connection state is shown in the header, never as a banner over the content.

## Texts and look

**GEN-12** Playful texts come from lists configured on the server, each with a plain fallback. A text is picked when its context appears and stays until the context changes.

**GEN-13** Themes change colours, patterns and tints. They never change behaviour, sizes of controls, or the meaning of the status colours.

**GEN-14** Two width tiers apply: 768 px for the frame, 600 px for the compact components. Both depend on width, not on the device.

**GEN-15** On a phone every text field uses 16 px text, so the page never zooms when a field takes focus.

**GEN-16** Every control that shows on hover on desktop is always visible, or reachable by a tap, on a touch screen.
