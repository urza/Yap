# If we started Yap from scratch today

A conversation between urza and Claude, 2026-08-17. Saved verbatim at urza's request,
right after the git-history deep dive into the real-time events evolution
(see `article-realtime-events-blazor-server.md`).

---

**urza:**

> Question. If you were to start Yap today from scratch. What would you choose? With all
> we learned along the way, what would you choose between blazor server vs web assembly.
> And also would you choose blazor/dotnet at all? Since today it would be probably just
> you coding, I barely look at code these days.. What do you think?

**Claude:**

Good question, and the history gives a fairly sharp answer.

**Server vs WASM, with what we know now.** Yap's whole latency saga taught one lesson: for a chat app, the feedback the user watches must be local. Blazor Server puts it on the wire by default, and we spent real weeks building the doctrine that pulls it back to the client. Client-rendered UI gets that property for free. Only the mutations need the wire, and chat mutations are tiny. So for this specific app class, remote users on bad connections, I would put the interactivity on the client from day one. I would still defend the 2025 decision, though. Blazor Server let one person plus an AI ship rooms, DMs, media, push, and PWA fast, because there is no client/server contract to design or maintain. The doctrine cost about two weeks and it is done and field-validated. As an evolution path it was right. As a greenfield choice today it would not be my first pick.

**Would I pick Blazor at all?** Split the question. Backend: yes, .NET without hesitation. You can operate it, and EF, SQLite, tus, web push, and ffmpeg orchestration are all proven in this repo. The backend choice barely affects UX. The real fork is the frontend, and there the honest answer is that Blazor's core pitch has expired for you. Blazor exists so that C# developers do not have to write JavaScript. When I write the code, that benefit is worth nothing. I am at least as reliable in TypeScript as in C#, the training corpus for the JS ecosystem is larger, and the patterns a chat client needs (virtualized message lists, optimistic UI, offline queues, service workers) are first-class there and hand-rolled here.

**So, greenfield Yap today:** ASP.NET Core backend, WebSocket for the event fan-out (the fifteen `Action` events already define that protocol), and a TypeScript PWA frontend, probably Svelte or React. Hosted Blazor WASM is the middle path if you wanted to stay all-C#, but it has the worst first-load on exactly the slow mobile links we care about, and you pay .NET-on-browser debugging costs without gaining the JS ecosystem.

**The honest cost of my pick:** the API contract. Blazor Server's deepest win in this repo is that `ChatService` events flow straight into components with shared C# models and zero serialization design. A SPA brings back message schemas, versioning, and auth tokens, the same tax that made the four-project era miserable. The difference is that in 2025 we paid that tax while also discovering what the app was. Today the protocol is known, it is small, and I would generate both sides of it from one schema. That changes the trade.

One thing I would keep identical in any stack: the architecture lessons themselves. State mutates before events fire, the sender never waits for receivers, and the server stays the source of truth. Those survived two rewrites already, and they are stack-independent.
