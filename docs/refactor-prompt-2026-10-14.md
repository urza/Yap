# Prompt for the refactoring session (2026-10-14)

This is a Blazor Server chat app. It works well. I want to refactor it away from Blazor Server.

Scope: Yap is a small community real-time chat. Tens to hundreds of people. It does not replace Discord. 

Keep the behaviour and the look from the user's point of view. The specification in docs/spec lists every feature, its behaviour and its look, with screenshots. Read docs/spec/README.md first, then the pages. Where the spec and the existing code disagree, the spec is right; the spec was written against this code and the differences are deliberate. Report each difference you meet anyway, so I can see them.


The new stack:
- Backend: modern ASP.NET Core. Keep what we have; remove the Blazor parts. No Blazor anywhere, including Settings, Admin, Login, the channel pages and the welcome page. The spec covers all of them.
- Client: vanilla JavaScript, no framework, no bundler. It talks to the backend through an API and SignalR. Vendor the SignalR browser client as one file in wwwroot.
- Offline: the new client should support partial offline use. The design is in docs/offline-design-notes.md. Follow it; it is deliberately modest.
- npm and node: forbidden in production and in the build. In development use them only for browser tests and screenshots, or avoid them with Playwright for .NET (NuGet).
- Deployment stays as it is. SQLite as optional persistence stays. Running without a database is a product feature.

Speed and latency: users are sometimes on the other side of the globe. Yap must stay responsive on both ends. Minimise round trips, make actions local first, keep the backend fast. Keep the latency diagnostics the spec asks for (round trip and send-to-appear, AD-26), so this can be measured.

Code rules for everything new:
1. Minimal and elegant. No unnecessary parts. "The best part is no part." For each part ask: do we need it, is it the right thing, is it elegant?
2. Readable and maintainable, on every level: the whole, the architecture, the parts, the details. Will the next person understand what it does and why it is here? Check this as you add parts.
3. Modern .NET features and patterns where they do not conflict with rules 1 and 2.

Code organisation: keep the layered architecture (Endpoints/, Services/, Models/, Middleware/, Configuration/). Add subfolders by area in Services/ and Models/, following the spec pages: Chat, Presence, Notifications, Media, Gifs, Emoji, Accounts, Admin. Namespaces follow the folders (Services/Presence/ -> Yap.Services.Presence).

Start with a sketch of the new architecture, from three sides:
1. The singleton ChatService, the source of truth running in one instance. It can stay mostly the same. Think outwards from it: what has to change.
2. The client: new web from HTML and JS. How it looks, how it is organised, how it talks to the backend, what it needs from the backend.
3. The wire contract between the two: one page listing the hub events and the routes.
Show me the sketch before you write code.
