# LLM bots: design notes (2026-09-05, discussion only, nothing built)

Status: idea stage. Discussed in dialogue, no decisions locked. First open
question when we pick this up: **bots in rooms in v1, or DMs only first?**

## The idea (user's framing)

- Per-deployment config for a local LLM endpoint (vLLM). Set in appsettings or
  by the admin in the UI.
- Admin defines bots in the admin panel and gives each one a system prompt.
- Users talk to the bots.
- Default bot Ping could help with settings and onboarding.
- Custom bots for whatever the admin wants.

## Where it plugs in today

`Services/SystemBotService.cs`, `HandleMessageReceived`: subscribes to
`ChatService.OnMessageReceived`, finds the bot's DM channel, debounces 30s per
user, waits 800ms, posts the "Beep boop" placeholder. The LLM reply replaces
that placeholder call. Ping is already a real `User` row with a reserved
session id, avatar, and admin-revocation guard. Keep that pattern for all bots.

## Endpoint abstraction

Configure an **OpenAI-compatible endpoint**, not "vLLM": base URL, model name,
optional API key. vLLM, Ollama, llama.cpp server, LM Studio, and hosted APIs all
speak `/v1/chat/completions`. A typed `HttpClient` is enough, no SDK.

## Storage

- Each bot = a `User` row (like Ping) + a `Bot` table row: `UserId`,
  `SystemPrompt`, `Model` override, `Temperature`, `MaxHistory`,
  `RespondInRooms` (on @mention), `Enabled`. Needs an EF migration.
- Endpoint settings: either `Data/llm-settings.json` like the other runtime
  toggles, or appsettings with admin override. Runtime JSON matches the
  existing pattern (bot, registration, link-preview, gif settings).
- Bot usernames are reserved so a real user cannot take them.

## Sketch of the moving parts

- `LlmClient`: typed HttpClient, chat completions, optional streaming later.
- `BotService` (singleton, grows out of or sits next to `SystemBotService`):
  subscribes `OnMessageReceived`, routes a message to a bot (DM partner, or
  @mention in a room), builds the prompt from history, calls the LLM, posts the
  reply via `ChatService.SendMessageAsync`.
- Admin page section: endpoint settings + health check, bots CRUD with a
  system-prompt textarea and a "test message" button that replies inline
  without creating a DM.
- Ping stays the system bot (welcome DMs, admin alerts) and gains an LLM brain.

## Things to design in, not bolt on

- **Waiting feedback.** Local model takes 2 to 15 s. Use the existing typing
  indicator: "Ping is typing..." during generation. Streaming via repeated
  `EditMessageAsync` re-renders the channel per chunk over the wire; skip for v1.
- **Conversation window.** Pick a rule up front, e.g. last 20 messages or last
  2 hours, whichever is smaller. Cap by characters too. Add a `/reset` command.
- **Bot loops.** A bot never triggers a bot. Two bots mentioning each other in
  a room would run forever.
- **In-flight rule.** Drop the 30 s debounce. One request in flight per user
  per bot; messages sent meanwhile fold into the next turn.
- **Rooms.** Reply only on @mention. Put usernames inside the content
  ("urza: ...") so the model knows who said what.
- **Markdown.** Models emit markdown, Yap does not render it. Either every
  system prompt says "plain text, no markdown", or add minimal rendering
  (bold, code, lists).
- **Attachments.** v1 replaces images/GIFs with "[image]" in the prompt. A
  vision model can receive real images later.
- **Endpoint down.** Bot replies "I am offline right now", logs the error.
  Health check in Admin diagnostics.
- **Throughput.** One GPU. Global queue with a small concurrency limit, plus a
  per-user rate limit.
- **Sidebar.** Bots get a 🤖 marker and do not count in the online user count.
- **Edits/deletes** of user messages are ignored by bots.
- **Language.** Users span countries. System prompts should say "answer in the
  user's language".
- **Push.** A bot DM reply pushes like any DM. That is fine and expected.
- **Privacy.** Local model keeps data at home. Without tools, prompt injection
  is low risk. Content quality is the admin's responsibility.

## Ping as settings helper

1. First step: a hand-written help doc as Ping's default system prompt. Themes,
   mute levels, passphrase, sessions, PWA install, with deep links like
   `/settings`. Cheap and reliable.
2. Later: tool calling so Ping changes a setting for the user. A bigger trust
   question, do not start there.

## Bot ideas that fit a chat app

- "What did I miss": summarize a room since the user's last read. Needs a
  permission check on room access. Uses `_readStates` in ChatService.
- Translator via reply-to (`replyToMessageId` exists). The country field
  suggests mixed languages already.
- Game master or trivia host in a room.
- Custom persona bots per admin taste. No extra code beyond the system prompt.
