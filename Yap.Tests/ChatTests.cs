using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Yap.Models.Chat;
using Yap.Services.Chat;

namespace Yap.Tests;

/// <summary>The paths of docs/architecture/message-path.md and chat-hub.md, end to end through HTTP and the hub.</summary>
public class ChatTests : IClassFixture<YapFactory>
{
    private readonly YapFactory _app;
    public ChatTests(YapFactory app) => _app = app;

    [Fact(DisplayName = "AU-04, AU-06: the first account is the admin; the bootstrap answers both states")]
    public async Task Bootstrap_answers_both_states()
    {
        var anonymous = await _app.CreateClient().GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.False(anonymous.TryGetProperty("user", out _));
        Assert.True(anonymous.GetProperty("texts").TryGetProperty("loginHeadings", out _));

        var me = await _app.AdminAsync();
        var doc = await me.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal(me.Username, doc.GetProperty("user").GetProperty("username").GetString());
        Assert.True(doc.GetProperty("user").GetProperty("isAdmin").GetBoolean());
        Assert.True(doc.GetProperty("rooms").GetArrayLength() >= 1);
        Assert.Equal("online", doc.GetProperty("user").GetProperty("status").GetString());
    }

    [Fact(DisplayName = "IB-24, MR-69: a send returns the message, the room's readers receive it, a repeat returns the same message")]
    public async Task Send_reaches_the_open_room_and_is_idempotent()
    {
        var alice = await _app.SignInAsync("alice_send");
        var bob = await _app.SignInAsync("bob_send");
        var lobby = _app.Service<ChatService>().LobbyId;

        var bobHub = await bob.ConnectAsync();
        var received = new Expectation<MessageDto>();
        bobHub.On<MessageDto>("message", received.Set);
        await bobHub.InvokeAsync<Conversation>("OpenRoom", lobby);

        var clientId = Guid.NewGuid();
        var first = await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId, text = "hello bob" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sent = (await first.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal("hello bob", sent.Text);
        Assert.Equal(clientId, sent.ClientId);

        var seen = await received.Within();
        Assert.Equal(sent.Id, seen.Id);
        Assert.Equal("alice_send", seen.Username);

        var repeat = await (await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId, text = "hello bob" })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.Equal(sent.Id, repeat!.Id);

        var empty = await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await bobHub.DisposeAsync();
    }

    [Fact(DisplayName = "IB-70, MR-76: a voice message is sent alone, from an upload of this server, and reaches the row with its bars")]
    public async Task Voice_message_is_sent_alone()
    {
        var alice = await _app.SignInAsync("alice_voice");
        var lobby = _app.Service<ChatService>().LobbyId;
        var voice = new { url = "/uploads/voice.m4a", durationMs = 4200, peaks = "0369cfilorux" };

        var withText = await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), text = "hi", voice });
        Assert.Equal(HttpStatusCode.BadRequest, withText.StatusCode);
        var remote = await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), voice = voice with { url = "https://elsewhere.example/x.m4a" } });
        Assert.Equal(HttpStatusCode.BadRequest, remote.StatusCode);
        var malformed = await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), voice = voice with { peaks = "<script>" } });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var sent = await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), voice });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var dto = (await sent.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal("", dto.Text);
        Assert.Equal("/uploads/voice.m4a", dto.Voice?.Src);
        Assert.Equal(4200, dto.Voice?.DurationMs);
        Assert.Equal("0369cfilorux", dto.Voice?.Peaks);

        // A reply quotes it in words (MR-32), and an edit is refused: there is no caption to change (MR-57).
        var reply = await (await alice.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), text = "nice", replyToId = dto.Id })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.True(reply!.ReplyTo!.Voice);
        Assert.Null(reply.ReplyTo.Text);
        Assert.NotNull(_app.Service<ChatService>().Edit(_app.Service<ChatService>().GetMessage(dto.Id)!.UserId, dto.Id, "a caption"));
    }

    [Fact(DisplayName = "AD-03: the startup load keeps the newest messages of each conversation, within the count and the days")]
    public async Task Startup_load_is_bounded_per_conversation()
    {
        var db = Path.Combine(Path.GetTempPath(), $"yap-test-{Guid.NewGuid():N}.db");
        try
        {
            Guid lobby, dm;
            await using (var app = new YapFactory { SqlitePath = db })
            {
                var boss = await app.AdminAsync();
                var alice = await app.SignInAsync("alice_load");
                var chat = app.Service<ChatService>();
                var bossId = app.Service<Yap.Services.Accounts.UserService>().GetByUsername(boss.Username)!.Id;
                lobby = chat.LobbyId;
                dm = chat.GetOrCreateDm(bossId, alice.Username)!.Id;
                foreach (var (channel, text) in new[] { (lobby, "m1"), (lobby, "m2"), (lobby, "m3"), (lobby, "m4"), (lobby, "m5"), (dm, "d1"), (dm, "d2") })
                {
                    var sent = await boss.Http.PostAsJsonAsync($"/api/channels/{channel}/messages", new { clientId = Guid.NewGuid(), text });
                    Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                }
            }   // the stop drains the writer

            // m1, m2 and d1 are ten days old now, in the text form the writer stores
            await using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                await connection.OpenAsync();
                await using var age = connection.CreateCommand();
                age.CommandText = "update Messages set Timestamp = $t where Content in ('m1', 'm2', 'd1')";
                age.Parameters.AddWithValue("$t", DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
                Assert.Equal(3, await age.ExecuteNonQueryAsync());
            }
            SqliteConnection.ClearAllPools();

            // The count bound cuts the lobby to m4 and m5; the day bound cuts the conversation to d2.
            await using (var app = new YapFactory { SqlitePath = db, Settings = { ["ChatSettings:Persistence:Load:MaxMessagesPerChannel"] = "2", ["ChatSettings:Persistence:Load:MaxDays"] = "5" } })
            {
                var chat = app.Service<ChatService>();
                var bossId = app.Service<Yap.Services.Accounts.UserService>().GetByUsername("boss")!.Id;
                var (lobbyTail, hasMore, _) = chat.Tail(lobby, 50, bossId);
                Assert.Equal(new[] { "m4", "m5" }, lobbyTail.Select(m => m.Content));
                Assert.False(hasMore);   // the history starts at the oldest loaded message
                var (dmTail, _, _) = chat.Tail(dm, 50, bossId);
                Assert.Equal(new[] { "d2" }, dmTail.Select(m => m.Content));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(db)!, Path.GetFileName(db) + "*")) File.Delete(f);
        }
    }

    [Fact(DisplayName = "TL-01, TL-05, TL-06: the tail is 50, older pages follow the cursor, the cutoff hides history from non-admins")]
    public async Task Tail_older_and_cutoff()
    {
        var admin = _app.Service<ChatService>();
        var owner = await _app.AdminAsync();   // only the admin creates rooms (AD-29)
        var ownerId = _app.Service<Yap.Services.Accounts.UserService>().GetByUsername(owner.Username)!.Id;
        var (room, error) = admin.CreateRoom(ownerId, "pages", null, ChannelPermission.Everyone, HistoryLimit.OneDay, sinceJoined: false);
        Assert.Null(error);
        admin.GenerateTestMessages(room!.Id, ownerId, owner.Username, 120, TimeSpan.FromDays(3));

        var hub = await owner.ConnectAsync();
        var tail = await hub.InvokeAsync<Conversation>("OpenRoom", room.Id);
        Assert.Equal(50, tail.Messages.Count);
        Assert.True(tail.HasMore);
        Assert.False(tail.Restricted);   // the admin sees everything

        var oldest = tail.Messages[0];
        var page = await hub.InvokeAsync<Page>("Older", room.Id, oldest.Timestamp, oldest.Id);
        Assert.Equal(50, page.Messages.Count);
        Assert.True(page.Messages[^1].Timestamp <= oldest.Timestamp);
        var last = await hub.InvokeAsync<Page>("Older", room.Id, page.Messages[0].Timestamp, page.Messages[0].Id);
        Assert.Equal(20, last.Messages.Count);
        Assert.False(last.HasMore);
        await hub.DisposeAsync();

        var reader = await _app.SignInAsync("reader_pages");
        var readerHub = await reader.ConnectAsync();
        var limited = await readerHub.InvokeAsync<Conversation>("OpenRoom", room.Id);
        Assert.True(limited.Restricted);
        Assert.All(limited.Messages, m => Assert.True(m.Timestamp >= DateTime.UtcNow.AddDays(-1).AddMinutes(-1)));
        await readerHub.DisposeAsync();
    }

    [Fact(DisplayName = "NT-01, NT-03, NT-06: a direct message counts for the other person until they open it")]
    public async Task Unread_counts_and_mark_read()
    {
        var carol = await _app.SignInAsync("carol_unread");
        var dave = await _app.SignInAsync("dave_unread");
        var chat = _app.Service<ChatService>();
        var users = _app.Service<Yap.Services.Accounts.UserService>();
        var dm = chat.GetOrCreateDm(users.GetByUsername("carol_unread")!.Id, "dave_unread")!;

        var daveHub = await dave.ConnectAsync();
        var unread = new Expectation<(Guid, int)>();
        daveHub.On<Guid, int>("unread", (channelId, count) => { if (channelId == dm.Id && count == 1) unread.Set((channelId, count)); });

        var sent = await carol.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "psst" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await unread.Within();

        var doc = await dave.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal(1, doc.GetProperty("unread").GetProperty(dm.Id.ToString()).GetInt32());

        var cleared = new Expectation<int>();
        daveHub.On<Guid, int>("unread", (channelId, count) => { if (channelId == dm.Id && count == 0) cleared.Set(count); });
        await daveHub.InvokeAsync<Conversation>("OpenDm", "carol_unread");   // TL-03: opening marks read
        await cleared.Within();
        Assert.Equal(0, chat.UnreadCount(users.GetByUsername("dave_unread")!.Id, dm.Id));
        await daveHub.DisposeAsync();
    }

    [Fact(DisplayName = "NT-03, NT-07: a room message counts for a connected person elsewhere, not for one reading the room")]
    public async Task Room_message_skips_the_readers()
    {
        var fay = await _app.SignInAsync("fay_reader");
        var gil = await _app.SignInAsync("gil_elsewhere");
        var hal = await _app.SignInAsync("hal_sender");
        var chat = _app.Service<ChatService>();
        var users = _app.Service<Yap.Services.Accounts.UserService>();
        var lobby = chat.LobbyId;

        var fayHub = await fay.ConnectAsync();
        var gilHub = await gil.ConnectAsync();
        await fayHub.InvokeAsync<Conversation>("OpenRoom", lobby);   // fay is reading the lobby; gil has Yap open on nothing
        var fayCounted = new Expectation<int>();
        fayHub.On<Guid, int>("unread", (channelId, count) => { if (channelId == lobby && count > 0) fayCounted.Set(count); });
        var gilCounted = new Expectation<int>();
        gilHub.On<Guid, int>("unread", (channelId, count) => { if (channelId == lobby && count == 1) gilCounted.Set(count); });

        var sent = await hal.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), text = "room" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await gilCounted.Within();

        Assert.Equal(0, chat.UnreadCount(users.GetByUsername("fay_reader")!.Id, lobby));
        Assert.False(fayCounted.Completed, "a reader got an unread count for the room they are looking at");
        await fayHub.DisposeAsync();
        await gilHub.DisposeAsync();
    }

    [Fact(DisplayName = "MR-40, MR-57: a reaction toggles and arrives; an edit of someone else's message is refused")]
    public async Task Reactions_and_edit_rules()
    {
        var erin = await _app.SignInAsync("erin_react");
        var frank = await _app.SignInAsync("frank_react");
        var lobby = _app.Service<ChatService>().LobbyId;
        var message = (await (await erin.Http.PostAsJsonAsync($"/api/channels/{lobby}/messages", new { clientId = Guid.NewGuid(), text = "react to me" })).Content.ReadFromJsonAsync<MessageDto>())!;

        var frankHub = await frank.ConnectAsync();
        await frankHub.InvokeAsync<Conversation>("OpenRoom", lobby);
        var reactions = new Expectation<IReadOnlyList<ReactionGroupDto>>();
        frankHub.On<Guid, IReadOnlyList<ReactionGroupDto>>("reactions", (id, groups) => { if (id == message.Id) reactions.Set(groups); });

        var ok = await frankHub.InvokeAsync<HubError?>("React", message.Id, "👍");
        Assert.Null(ok);
        var groups = await reactions.Within();
        Assert.Single(groups);
        Assert.Equal("👍", groups[0].Emoji);
        Assert.Equal(["frank_react"], groups[0].Users);

        var refused = await frankHub.InvokeAsync<HubError?>("Edit", message.Id, "mine now");
        Assert.NotNull(refused);
        await frankHub.DisposeAsync();
    }

    [Fact(DisplayName = "GEN-04: a reply quotes an original of the same conversation only")]
    public async Task Reply_across_conversations_is_refused()
    {
        var alice = await _app.SignInAsync("alice_quote");
        var bob = await _app.SignInAsync("bob_quote");
        var eve = await _app.SignInAsync("eve_quote");
        var chat = _app.Service<ChatService>();
        var users = _app.Service<Yap.Services.Accounts.UserService>();
        var dm = chat.GetOrCreateDm(users.GetByUsername("alice_quote")!.Id, "bob_quote")!;
        var secret = (await (await alice.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "PRIVATE SENTINEL" })).Content.ReadFromJsonAsync<MessageDto>())!;

        // a third person knows the id: the lobby send is refused and the answer carries no text of the original
        var leak = await eve.Http.PostAsJsonAsync($"/api/channels/{chat.LobbyId}/messages", new { clientId = Guid.NewGuid(), text = "quote", replyToId = secret.Id });
        Assert.Equal(HttpStatusCode.BadRequest, leak.StatusCode);
        Assert.DoesNotContain("SENTINEL", await leak.Content.ReadAsStringAsync());

        // the same conversation: the quote carries the text
        var reply = await (await bob.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "re", replyToId = secret.Id })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.Equal("PRIVATE SENTINEL", reply!.ReplyTo!.Text);

        // an original that is gone passes; the quote says so (MR-35)
        var gone = await (await bob.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "re2", replyToId = Guid.NewGuid() })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.True(gone!.ReplyTo!.Unavailable);
    }

    [Fact(DisplayName = "PR-19: typing counts for the conversation the connection has open, and for no other")]
    public async Task Typing_needs_the_conversation_open()
    {
        var ann = await _app.SignInAsync("ann_typing");
        var ben = await _app.SignInAsync("ben_typing");
        var cid = await _app.SignInAsync("cid_typing");
        var chat = _app.Service<ChatService>();
        var users = _app.Service<Yap.Services.Accounts.UserService>();
        var dm = chat.GetOrCreateDm(users.GetByUsername("ann_typing")!.Id, "ben_typing")!;

        var benHub = await ben.ConnectAsync();
        await benHub.InvokeAsync<Conversation>("OpenDm", "ann_typing");
        var typists = new List<string>();
        var annTypes = new Expectation<string>();
        benHub.On<Guid, string, bool>("typing", (channelId, username, on) =>
        {
            if (channelId != dm.Id || !on) return;
            lock (typists) typists.Add(username);
            if (username == "ann_typing") annTypes.Set(username);
        });

        var cidHub = await cid.ConnectAsync();   // a third person with the lobby open and the direct message's id
        await cidHub.InvokeAsync<Conversation>("OpenRoom", chat.LobbyId);
        await cidHub.SendAsync("Typing", dm.Id, true);

        var annHub = await ann.ConnectAsync();   // the partner, typing after the intruder: events arrive in order
        await annHub.InvokeAsync<Conversation>("OpenDm", "ben_typing");
        await annHub.SendAsync("Typing", dm.Id, true);
        await annTypes.Within();
        lock (typists) Assert.Equal(["ann_typing"], typists);

        await benHub.DisposeAsync(); await cidHub.DisposeAsync(); await annHub.DisposeAsync();
    }

    [Fact(DisplayName = "SH-43, AD-33: the lobby is marked in the contract, whatever the sidebar order")]
    public async Task Lobby_is_marked_whatever_the_order()
    {
        var owner = await _app.AdminAsync();
        var chat = _app.Service<ChatService>();
        var ownerId = _app.Service<Yap.Services.Accounts.UserService>().GetByUsername(owner.Username)!.Id;
        var (room, error) = chat.CreateRoom(ownerId, "firstroom", null, ChannelPermission.Everyone, HistoryLimit.Unlimited, sinceJoined: false);
        Assert.Null(error);
        while (chat.Rooms()[0].Id != room!.Id) Assert.True(chat.MoveRoom(ownerId, room.Id, up: true));

        var rooms = (await owner.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap")).GetProperty("rooms").EnumerateArray().ToList();
        Assert.Equal("firstroom", rooms[0].GetProperty("name").GetString());
        var marked = Assert.Single(rooms, r => r.GetProperty("isLobby").GetBoolean());
        Assert.Equal(chat.LobbyId, marked.GetProperty("id").GetGuid());

        while (chat.Rooms()[^1].Id != room.Id) Assert.True(chat.MoveRoom(ownerId, room.Id, up: false));   // back to the bottom
    }

    [Fact(DisplayName = "OF-08: a direct message is addressed by its partner; the server finds or creates the channel")]
    public async Task Send_to_a_direct_message_by_partner()
    {
        var gina = await _app.SignInAsync("gina_dm");
        var hal = await _app.SignInAsync("hal_dm");
        var first = await gina.Http.PostAsJsonAsync("/api/dm/hal_dm/messages", new { clientId = Guid.NewGuid(), text = "hi hal" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sent = (await first.Content.ReadFromJsonAsync<MessageDto>())!;

        var halHub = await hal.ConnectAsync();
        var opened = await halHub.InvokeAsync<Conversation>("OpenDm", "gina_dm");
        Assert.Equal(opened.ChannelId, sent.ChannelId);
        Assert.Contains(opened.Messages, m => m.Id == sent.Id);
        await halHub.DisposeAsync();

        var unknown = await gina.Http.PostAsJsonAsync("/api/dm/nobody_dm/messages", new { clientId = Guid.NewGuid(), text = "?" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
