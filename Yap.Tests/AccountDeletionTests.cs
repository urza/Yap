using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Chat;

namespace Yap.Tests;

/// <summary>The admin's account deletion and the inactive sweep (AD-38 to AD-40).</summary>
public class AccountDeletionTests : IClassFixture<YapFactory>
{
    private readonly YapFactory _app;
    public AccountDeletionTests(YapFactory app) => _app = app;

    [Fact(DisplayName = "AD-40: a deleted account is signed out, its direct messages go, its room messages stay as (deleted), the name is free")]
    public async Task Delete_signs_out_relabels_and_frees_the_name()
    {
        var db = Path.Combine(Path.GetTempPath(), $"yap-test-{Guid.NewGuid():N}.db");
        try
        {
            Guid oldId;
            await using (var app = new YapFactory { SqlitePath = db })
            {
                var admin = await app.AdminAsync();
                var emi = await app.SignInAsync("emi_gone");
                var tom = await app.SignInAsync("tom_stays");
                var chat = app.Service<ChatService>();
                var users = app.Service<UserService>();
                oldId = users.GetByUsername("emi_gone")!.Id;

                Assert.Equal(HttpStatusCode.OK, (await emi.Http.PostAsJsonAsync($"/api/channels/{chat.LobbyId}/messages", new { clientId = Guid.NewGuid(), text = "hello from emi" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await emi.Http.PostAsJsonAsync("/api/dm/tom_stays/messages", new { clientId = Guid.NewGuid(), text = "psst" })).StatusCode);
                Assert.Single(chat.DmConversations(users.GetByUsername("tom_stays")!.Id));

                var emiHub = await emi.ConnectAsync("phone");
                var told = new Expectation<bool>();
                emiHub.On("signedOut", () => told.Set(true));
                var closed = new Expectation<bool>();
                emiHub.Closed += _ => { closed.Set(true); return Task.CompletedTask; };
                var tomHub = await tom.ConnectAsync();
                var gone = new Expectation<string>();
                tomHub.On<string>("accountDeleted", u => gone.Set(u));

                var response = await admin.Http.PostAsJsonAsync("/api/admin/users/delete", new { usernames = new[] { "emi_gone" } });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());

                await told.Within();
                await closed.Within();
                Assert.Equal("emi_gone", await gone.Within());
                Assert.Equal(HttpStatusCode.Unauthorized, (await emi.Http.GetAsync("/api/account/devices?device=phone")).StatusCode);   // the saved sign-in is dead

                Assert.Null(users.GetByUsername("emi_gone"));
                Assert.Empty(chat.DmConversations(users.GetByUsername("tom_stays")!.Id));
                var kept = chat.Tail(chat.LobbyId, 50, users.GetByUsername("boss")!.Id).Messages.Single(m => m.Content == "hello from emi");
                Assert.Equal("emi_gone (deleted)", kept.Username);

                var again = await app.SignInAsync("emi_gone");   // the name is free: a new account, not the old one
                Assert.NotEqual(oldId, users.GetByUsername("emi_gone")!.Id);
                Assert.Equal(HttpStatusCode.OK, (await again.Http.GetAsync("/api/account/devices?device=laptop")).StatusCode);
                await emiHub.DisposeAsync();
                await tomHub.DisposeAsync();
            }   // disposing the factory drains the writer

            await using var connection = new SqliteConnection($"Data Source={db}");
            await connection.OpenAsync();
            Assert.Equal(0L, await Scalar(connection, "select count(*) from Users where Id = @id", oldId));
            Assert.Equal(1L, await Scalar(connection, "select count(*) from Users where Username = 'emi_gone'"));
            Assert.Equal(1L, await Scalar(connection, "select count(*) from Messages where Username = 'emi_gone (deleted)' and UserId = @id", oldId));
            Assert.Equal(0L, await Scalar(connection, "select count(*) from Channels where Participant1 = 'emi_gone' or Participant2 = 'emi_gone'"));
            Assert.Equal(0L, await Scalar(connection, "select count(*) from ChannelReadStates where UserId = @id", oldId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(db)!, Path.GetFileName(db) + "*")) File.Delete(f);
        }
    }

    [Fact(DisplayName = "AD-39: the inactive list has the old accounts only, never the admin, the bot or a connected person")]
    public async Task Inactive_list_skips_the_admin_the_bot_and_connected_people()
    {
        var admin = await _app.AdminAsync();
        await _app.SignInAsync("old_timer");
        await _app.SignInAsync("fresh_face");
        var busy = await _app.SignInAsync("busy_bee");
        var users = _app.Service<UserService>();
        foreach (var name in new[] { "old_timer", "busy_bee" })
        {
            var u = users.GetByUsername(name)!;
            u.CreatedAt = DateTime.UtcNow.AddDays(-300);
            u.LastSeenAt = DateTime.UtcNow.AddDays(-200);
        }
        var busyHub = await busy.ConnectAsync();

        var list = await admin.Http.GetFromJsonAsync<JsonElement>("/api/admin/inactive?days=90");
        var names = list.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("username").GetString()).ToList();
        Assert.Contains("old_timer", names);
        Assert.DoesNotContain("fresh_face", names);
        Assert.DoesNotContain("busy_bee", names);   // connected right now, whatever the row says
        Assert.DoesNotContain("boss", names);
        Assert.DoesNotContain(_app.Service<SystemBotService>().BotUsername, names);
        await busyHub.DisposeAsync();
    }

    [Fact(DisplayName = "AD-40: the admin and the bot cannot be deleted; a name that is gone already is skipped")]
    public async Task Admin_and_bot_are_refused()
    {
        var admin = await _app.AdminAsync();
        var bot = _app.Service<SystemBotService>().BotUsername!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Http.PostAsJsonAsync("/api/admin/users/delete", new { usernames = new[] { "boss" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Http.PostAsJsonAsync("/api/admin/users/delete", new { usernames = new[] { "nobody_here", bot } })).StatusCode);
        Assert.NotNull(_app.Service<UserService>().GetByUsername(bot));

        var response = await admin.Http.PostAsJsonAsync("/api/admin/users/delete", new { usernames = new[] { "nobody_here" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());
    }

    [Fact(DisplayName = "AD-40, AD-37: a deleted account leaves a room's pinners, its pins carry the (deleted) name, and a save without the pin fields changes nothing")]
    public async Task Delete_cleans_the_pins_and_the_pinners()
    {
        var admin = await _app.AdminAsync();
        var kim = await _app.SignInAsync("kim_pins");
        var chat = _app.Service<ChatService>();
        var created = await (await admin.Http.PostAsJsonAsync("/api/rooms", new { name = "pins-gone", description = (string?)null, adminOnly = false, pinPermission = "selected", pinners = new[] { "kim_pins" } })).Content.ReadFromJsonAsync<JsonElement>();
        var room = created.GetProperty("id").GetGuid();
        var message = (await (await kim.Http.PostAsJsonAsync($"/api/channels/{room}/messages", new { clientId = Guid.NewGuid(), text = "kim's words" })).Content.ReadFromJsonAsync<MessageDto>())!;
        var kimHub = await kim.ConnectAsync();
        await kimHub.InvokeAsync<Conversation>("OpenRoom", room);
        Assert.Null(await kimHub.InvokeAsync<HubError?>("Pin", room, message.Id));
        await kimHub.DisposeAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.Http.PostAsJsonAsync("/api/admin/users/delete", new { usernames = new[] { "kim_pins" } })).StatusCode);

        var pin = Assert.Single(chat.PinsFor(room, null));
        Assert.Equal("kim_pins (deleted)", pin.Username);   // a newcomer called kim_pins lends the pin nothing
        Assert.Equal("kim_pins (deleted)", pin.PinnedBy);
        Assert.Empty(chat.GetRoom(room)!.Pinners);          // and gets no right to pin from the old account

        // A save without the pin fields (a tab on an older build) leaves them as they are.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Http.PatchAsJsonAsync($"/api/rooms/{room}", new { name = "pins-gone", description = (string?)null, adminOnly = false })).StatusCode);
        Assert.Equal(PinPermission.Selected, chat.GetRoom(room)!.PinPermission);
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql, Guid? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is { } value) command.Parameters.AddWithValue("@id", value.ToString().ToUpperInvariant());
        return await command.ExecuteScalarAsync();
    }
}
