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

    [Fact(DisplayName = "AD-40: a deleted account is signed out, its conversation with the admin goes, the name is free")]
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

                // The person's one conversation is the admin's (docs/dmonly.md); every message of the person lives there.
                var adminId = users.GetByUsername(admin.Username)!.Id;
                Assert.Equal(HttpStatusCode.OK, (await emi.Http.PostAsJsonAsync($"/api/dm/{admin.Username}/messages", new { clientId = Guid.NewGuid(), text = "hello from emi" })).StatusCode);
                Assert.Contains(chat.DmConversations(adminId), c => c.Partner == "emi_gone");

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
                Assert.DoesNotContain(chat.DmConversations(adminId), c => c.Partner == "emi_gone");   // the conversation went with the account

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
            Assert.Equal(0L, await Scalar(connection, "select count(*) from Messages where UserId = @id", oldId));   // no room build here: the conversation's rows went with it
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

    private static async Task<object?> Scalar(SqliteConnection connection, string sql, Guid? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is { } value) command.Parameters.AddWithValue("@id", value.ToString().ToUpperInvariant());
        return await command.ExecuteScalarAsync();
    }
}
