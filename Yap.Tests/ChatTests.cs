using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Chat;

namespace Yap.Tests;

/// <summary>The paths of docs/architecture/message-path.md and chat-hub.md, end to end through HTTP and the hub,
/// in the direct-message-only build (docs/dmonly.md): a person's one conversation is the one with the admin.</summary>
public class ChatTests : IClassFixture<YapFactory>
{
    private readonly YapFactory _app;
    public ChatTests(YapFactory app) => _app = app;

    private UserService Users => _app.Service<UserService>();
    private ChatService Chat => _app.Service<ChatService>();
    private Guid IdOf(string username) => Users.GetByUsername(username)!.Id;

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
        Assert.False(doc.TryGetProperty("rooms", out _));   // no rooms in this build
        Assert.Equal("online", doc.GetProperty("user").GetProperty("status").GetString());
    }

    [Fact(DisplayName = "dmonly: a person's directory holds the admin, the bot and themself; the admin's holds everyone")]
    public async Task Directory_is_cut_for_a_person()
    {
        var admin = await _app.AdminAsync();
        var pat = await _app.SignInAsync("pat_directory");
        await _app.SignInAsync("quinn_directory");

        var names = (await pat.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap")).GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString()).ToList();
        Assert.Contains("pat_directory", names);
        Assert.Contains(admin.Username, names);
        Assert.DoesNotContain("quinn_directory", names);
        Assert.All(names, n => Assert.True(n == "pat_directory" || Users.IsAdmin(n!) || _app.Service<SystemBotService>().IsBotUser(n!)));

        var everyone = (await admin.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap")).GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString()).ToList();
        Assert.Contains("pat_directory", everyone);
        Assert.Contains("quinn_directory", everyone);
    }

    [Fact(DisplayName = "dmonly: a person reaches the admin and nobody else; the admin reaches anyone")]
    public async Task A_person_reaches_the_admin_only()
    {
        var admin = await _app.AdminAsync();
        var rae = await _app.SignInAsync("rae_rule");
        var sam = await _app.SignInAsync("sam_rule");

        var toPerson = await rae.Http.PostAsJsonAsync("/api/dm/sam_rule/messages", new { clientId = Guid.NewGuid(), text = "hi sam" });
        Assert.Equal(HttpStatusCode.NotFound, toPerson.StatusCode);
        Assert.Null(Chat.GetOrCreateDm(IdOf("rae_rule"), IdOf("sam_rule")));

        var raeHub = await rae.ConnectAsync();
        await Assert.ThrowsAsync<HubException>(() => raeHub.InvokeAsync<Conversation>("OpenDm", "sam_rule"));
        var withAdmin = await raeHub.InvokeAsync<Conversation>("OpenDm", admin.Username);
        Assert.NotEqual(Guid.Empty, withAdmin.ChannelId);
        await raeHub.DisposeAsync();

        var toAdmin = await rae.Http.PostAsJsonAsync($"/api/dm/{admin.Username}/messages", new { clientId = Guid.NewGuid(), text = "hi boss" });
        Assert.Equal(HttpStatusCode.OK, toAdmin.StatusCode);
        var fromAdmin = await admin.Http.PostAsJsonAsync("/api/dm/sam_rule/messages", new { clientId = Guid.NewGuid(), text = "hi sam" });
        Assert.Equal(HttpStatusCode.OK, fromAdmin.StatusCode);
    }

    [Fact(DisplayName = "IB-24, MR-69: a send returns the message, the partner receives it, a repeat returns the same message")]
    public async Task Send_reaches_the_partner_and_is_idempotent()
    {
        var admin = await _app.AdminAsync();
        var alice = await _app.SignInAsync("alice_send");

        var adminHub = await admin.ConnectAsync();
        var received = new Expectation<MessageDto>();
        adminHub.On<MessageDto>("message", m => { if (m.Username == "alice_send") received.Set(m); });
        await adminHub.InvokeAsync<Conversation>("OpenDm", "alice_send");

        var clientId = Guid.NewGuid();
        var first = await alice.Http.PostAsJsonAsync($"/api/dm/{admin.Username}/messages", new { clientId, text = "hello boss" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sent = (await first.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal("hello boss", sent.Text);
        Assert.Equal(clientId, sent.ClientId);

        var seen = await received.Within();
        Assert.Equal(sent.Id, seen.Id);
        Assert.Equal("alice_send", seen.Username);

        var repeat = await (await alice.Http.PostAsJsonAsync($"/api/channels/{sent.ChannelId}/messages", new { clientId, text = "hello boss" })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.Equal(sent.Id, repeat!.Id);

        var empty = await alice.Http.PostAsJsonAsync($"/api/channels/{sent.ChannelId}/messages", new { clientId = Guid.NewGuid(), text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await adminHub.DisposeAsync();
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
            Guid first, second;
            await using (var app = new YapFactory { SqlitePath = db })
            {
                var boss = await app.AdminAsync();
                var alice = await app.SignInAsync("alice_load");
                var carol = await app.SignInAsync("carol_load");
                var chat = app.Service<ChatService>();
                var bossId = app.Service<Yap.Services.Accounts.UserService>().GetByUsername(boss.Username)!.Id;
                first = chat.GetOrCreateDm(bossId, alice.Username)!.Id;    // the admin's two conversations (docs/dmonly.md)
                second = chat.GetOrCreateDm(bossId, carol.Username)!.Id;
                foreach (var (channel, text) in new[] { (first, "m1"), (first, "m2"), (first, "m3"), (first, "m4"), (first, "m5"), (second, "d1"), (second, "d2") })
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

            // The count bound cuts the first conversation to m4 and m5; the day bound cuts the second to d2.
            await using (var app = new YapFactory { SqlitePath = db, Settings = { ["ChatSettings:Persistence:Load:MaxMessagesPerChannel"] = "2", ["ChatSettings:Persistence:Load:MaxDays"] = "5" } })
            {
                var chat = app.Service<ChatService>();
                var bossId = app.Service<Yap.Services.Accounts.UserService>().GetByUsername("boss")!.Id;
                var (firstTail, hasMore) = chat.Tail(first, 50, bossId);   // no cutoff tuple in this build
                Assert.Equal(new[] { "m4", "m5" }, firstTail.Select(m => m.Content));
                Assert.False(hasMore);   // the history starts at the oldest loaded message
                var (secondTail, _) = chat.Tail(second, 50, bossId);
                Assert.Equal(new[] { "d2" }, secondTail.Select(m => m.Content));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(db)!, Path.GetFileName(db) + "*")) File.Delete(f);
        }
    }

    [Fact(DisplayName = "TL-01, TL-06: the tail is 50 and older pages follow the cursor, the same for both people")]
    public async Task Tail_and_older_pages()
    {
        var admin = await _app.AdminAsync();
        var reader = await _app.SignInAsync("reader_pages");
        var dm = Chat.GetOrCreateDm(IdOf(admin.Username), IdOf("reader_pages"))!;
        Assert.Equal(120, Chat.GenerateTestMessages(dm.Id, 120, TimeSpan.FromDays(3)));

        var hub = await admin.ConnectAsync();
        var tail = await hub.InvokeAsync<Conversation>("OpenDm", "reader_pages");
        Assert.Equal(50, tail.Messages.Count);
        Assert.True(tail.HasMore);

        var oldest = tail.Messages[0];
        var page = await hub.InvokeAsync<Page>("Older", dm.Id, oldest.Timestamp, oldest.Id);
        Assert.Equal(50, page.Messages.Count);
        Assert.True(page.Messages[^1].Timestamp <= oldest.Timestamp);
        var last = await hub.InvokeAsync<Page>("Older", dm.Id, page.Messages[0].Timestamp, page.Messages[0].Id);
        Assert.Equal(20, last.Messages.Count);
        Assert.False(last.HasMore);
        await hub.DisposeAsync();

        var readerHub = await reader.ConnectAsync();   // no history cutoff in a direct message
        var same = await readerHub.InvokeAsync<Conversation>("OpenDm", admin.Username);
        Assert.Equal(50, same.Messages.Count);
        Assert.True(same.HasMore);
        await readerHub.DisposeAsync();
    }

    [Fact(DisplayName = "NT-01, NT-03, NT-06: a message counts for the other person until they open the conversation")]
    public async Task Unread_counts_and_mark_read()
    {
        var admin = await _app.AdminAsync();
        var carol = await _app.SignInAsync("carol_unread");
        var dm = Chat.GetOrCreateDm(IdOf("carol_unread"), admin.Username)!;

        var adminHub = await admin.ConnectAsync();
        var unread = new Expectation<(Guid, int)>();
        adminHub.On<Guid, int>("unread", (channelId, count) => { if (channelId == dm.Id && count == 1) unread.Set((channelId, count)); });

        var sent = await carol.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "psst" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await unread.Within();

        var doc = await admin.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal(1, doc.GetProperty("unread").GetProperty(dm.Id.ToString()).GetInt32());
        Assert.Contains(doc.GetProperty("dms").EnumerateArray(), d => d.GetProperty("partner").GetString() == "carol_unread");   // the people page orders by this

        var cleared = new Expectation<int>();
        adminHub.On<Guid, int>("unread", (channelId, count) => { if (channelId == dm.Id && count == 0) cleared.Set(count); });
        await adminHub.InvokeAsync<Conversation>("OpenDm", "carol_unread");   // TL-03: opening marks read
        await cleared.Within();
        Assert.Equal(0, Chat.UnreadCount(IdOf(admin.Username), dm.Id));
        await adminHub.DisposeAsync();
    }

    [Fact(DisplayName = "dmonly: the bot writes into the person's conversation with the admin, and both count it")]
    public async Task Bot_writes_into_the_persons_conversation()
    {
        var admin = await _app.AdminAsync();
        var tess = await _app.SignInAsync("tess_bot");
        var bot = _app.Service<SystemBotService>();
        var dm = Chat.GetOrCreateDm(IdOf(admin.Username), IdOf("tess_bot"))!;

        var tessHub = await tess.ConnectAsync();
        var arrived = new Expectation<MessageDto>();
        tessHub.On<MessageDto>("message", m => { if (m.ChannelId == dm.Id && m.Username == bot.BotUsername) arrived.Set(m); });
        await tessHub.InvokeAsync<Conversation>("OpenDm", admin.Username);
        await tessHub.InvokeAsync("Leave");   // not looking: the count must grow

        await bot.NotifyNewDeviceLoginAsync("tess_bot", "link", "203.0.113.9");
        var notice = await arrived.Within();
        Assert.Contains("New device sign-in", notice.Text);
        Assert.Equal(1, Chat.UnreadCount(IdOf("tess_bot"), dm.Id));
        Assert.Equal(1, Chat.UnreadCount(IdOf(admin.Username), dm.Id));
        Assert.True(Chat.HasMessageFrom(dm.Id, IdOf(bot.BotUsername)));
        await tessHub.DisposeAsync();
    }

    [Fact(DisplayName = "MR-40, MR-57: a reaction toggles and arrives; an edit of someone else's message is refused")]
    public async Task Reactions_and_edit_rules()
    {
        var admin = await _app.AdminAsync();
        var erin = await _app.SignInAsync("erin_react");
        var message = (await (await erin.Http.PostAsJsonAsync($"/api/dm/{admin.Username}/messages", new { clientId = Guid.NewGuid(), text = "react to me" })).Content.ReadFromJsonAsync<MessageDto>())!;

        var adminHub = await admin.ConnectAsync();
        await adminHub.InvokeAsync<Conversation>("OpenDm", "erin_react");
        var reactions = new Expectation<IReadOnlyList<ReactionGroupDto>>();
        adminHub.On<Guid, IReadOnlyList<ReactionGroupDto>>("reactions", (id, groups) => { if (id == message.Id) reactions.Set(groups); });

        var ok = await adminHub.InvokeAsync<HubError?>("React", message.Id, "👍");
        Assert.Null(ok);
        var groups = await reactions.Within();
        Assert.Single(groups);
        Assert.Equal("👍", groups[0].Emoji);
        Assert.Equal([admin.Username], groups[0].Users);

        var refused = await adminHub.InvokeAsync<HubError?>("Edit", message.Id, "mine now");
        Assert.NotNull(refused);
        await adminHub.DisposeAsync();
    }

    [Fact(DisplayName = "GEN-04: a reply quotes an original of the same conversation only")]
    public async Task Reply_across_conversations_is_refused()
    {
        var admin = await _app.AdminAsync();
        var alice = await _app.SignInAsync("alice_quote");
        var eve = await _app.SignInAsync("eve_quote");
        var dm = Chat.GetOrCreateDm(IdOf("alice_quote"), admin.Username)!;
        var secret = (await (await alice.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "PRIVATE SENTINEL" })).Content.ReadFromJsonAsync<MessageDto>())!;

        // a third person knows the id: the send into their own conversation is refused and the answer carries no text of the original
        var leak = await eve.Http.PostAsJsonAsync($"/api/dm/{admin.Username}/messages", new { clientId = Guid.NewGuid(), text = "quote", replyToId = secret.Id });
        Assert.Equal(HttpStatusCode.BadRequest, leak.StatusCode);
        Assert.DoesNotContain("SENTINEL", await leak.Content.ReadAsStringAsync());

        // the same conversation: the quote carries the text
        var reply = await (await admin.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "re", replyToId = secret.Id })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.Equal("PRIVATE SENTINEL", reply!.ReplyTo!.Text);

        // an original that is gone passes; the quote says so (MR-35)
        var gone = await (await admin.Http.PostAsJsonAsync($"/api/channels/{dm.Id}/messages", new { clientId = Guid.NewGuid(), text = "re2", replyToId = Guid.NewGuid() })).Content.ReadFromJsonAsync<MessageDto>();
        Assert.True(gone!.ReplyTo!.Unavailable);
    }

    [Fact(DisplayName = "PR-19: typing counts for the conversation the connection has open, and for no other")]
    public async Task Typing_needs_the_conversation_open()
    {
        var admin = await _app.AdminAsync();
        var ann = await _app.SignInAsync("ann_typing");
        var cid = await _app.SignInAsync("cid_typing");
        var dm = Chat.GetOrCreateDm(IdOf("ann_typing"), admin.Username)!;

        var adminHub = await admin.ConnectAsync();
        await adminHub.InvokeAsync<Conversation>("OpenDm", "ann_typing");
        var typists = new List<string>();
        var annTypes = new Expectation<string>();
        adminHub.On<Guid, string, bool>("typing", (channelId, username, on) =>
        {
            if (channelId != dm.Id || !on) return;
            lock (typists) typists.Add(username);
            if (username == "ann_typing") annTypes.Set(username);
        });

        var cidHub = await cid.ConnectAsync();   // a third person with their own conversation open and the other one's id
        await cidHub.InvokeAsync<Conversation>("OpenDm", admin.Username);
        await cidHub.SendAsync("Typing", dm.Id, true);

        var annHub = await ann.ConnectAsync();   // the partner, typing after the intruder: events arrive in order
        await annHub.InvokeAsync<Conversation>("OpenDm", admin.Username);
        await annHub.SendAsync("Typing", dm.Id, true);
        await annTypes.Within();
        lock (typists) Assert.Equal(["ann_typing"], typists);

        await adminHub.DisposeAsync(); await cidHub.DisposeAsync(); await annHub.DisposeAsync();
    }

    [Fact(DisplayName = "OF-08: a direct message is addressed by its partner; the server finds or creates the channel")]
    public async Task Send_to_a_direct_message_by_partner()
    {
        var admin = await _app.AdminAsync();
        var gina = await _app.SignInAsync("gina_dm");
        var first = await gina.Http.PostAsJsonAsync($"/api/dm/{admin.Username}/messages", new { clientId = Guid.NewGuid(), text = "hi boss" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sent = (await first.Content.ReadFromJsonAsync<MessageDto>())!;

        var adminHub = await admin.ConnectAsync();
        var opened = await adminHub.InvokeAsync<Conversation>("OpenDm", "gina_dm");
        Assert.Equal(opened.ChannelId, sent.ChannelId);
        Assert.Contains(opened.Messages, m => m.Id == sent.Id);
        await adminHub.DisposeAsync();

        var unknown = await gina.Http.PostAsJsonAsync("/api/dm/nobody_dm/messages", new { clientId = Guid.NewGuid(), text = "?" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
