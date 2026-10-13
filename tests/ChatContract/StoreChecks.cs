using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yap.Data;
using Yap.Models;
using Yap.Services;

static class StoreChecks
{
    public static async Task Run(IServiceProvider services, User user)
    {
        var chat = services.GetRequiredService<ChatService>();
        var users = services.GetRequiredService<UserService>();
        var persistence = services.GetRequiredService<ChatPersistenceService>();
        // Null in memory mode, where ChatService holds the only message state.
        await using var db = services.GetService<IDbContextFactory<ChatDbContext>>() is { } factory ? await factory.CreateDbContextAsync() : null;
        var channel = chat.GetLobbyId();
        var sent = await chat.SendTextAsync(user, channel, Guid.NewGuid(), "object identity");
        var target = chat.GetMessageById(channel, sent.MessageId)!;
        await chat.MutateMessageAsync(user, channel, target.Id, Guid.NewGuid(), "edit", "same object", null, false);
        Check(ReferenceEquals(target, chat.GetMessageById(channel, target.Id)) && target.Content == "same object",
            "accepted mutation updates the existing live object without reloading a storage row");
        var other = (await users.CreateUserAsync("collisionfixture"))!;
        var collision = await chat.SendTextAsync(other, channel, sent.OperationId, "collision");
        Check(sent.MessageId != sent.OperationId && collision.MessageId != sent.MessageId,
            "accounts can reuse an operation ID without choosing or colliding message primary keys");
        Check((await chat.SendTextAsync(other, channel, sent.OperationId, "collision")).MessageId == collision.MessageId,
            "same-account retry keeps the server-generated message ID");
        using var scope = services.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<Yap.Services.ChatProjection>();
        var projected = snapshots.Message(target);
        Check(projected.OperationId == null && snapshots.Message(target, other.Id).OperationId == null
            && snapshots.Message(target, user.Id).OperationId == sent.OperationId
            && snapshots.ForViewer(projected, target, other).OperationId == null
            && snapshots.ForViewer(projected, target, user).OperationId == sent.OperationId,
            "windows and shared fan-out reveal operation IDs only to the sender");
        await chat.MutateMessageAsync(user, channel, target.Id, Guid.NewGuid(), "reaction", null, "👍", true);
        await chat.MutateMessageAsync(other, channel, target.Id, Guid.NewGuid(), "reaction", null, "❤️", true);
        Check(snapshots.Message(target).Reactions.Select(r => r.Emoji).SequenceEqual(new[] { "👍", "❤️" }),
            "reaction pills retain first-used order instead of sorting by codepoint");
        if (db != null)
        {
            await persistence.FlushAsync();
            var stored = await db.Messages.AsNoTracking().Include(m => m.Reactions).SingleOrDefaultAsync(m => m.Id == target.Id);
            Check(stored?.Content == target.Content && stored.IsEdited && stored.Reactions.Count == 2, "the database copy follows the live mutation");
        }
        await chat.SendMessageAsync(channel, user.Id, user.Username, "server acceptance");
        var serverMessage = chat.GetMessages(channel, 100).Single(m => m.Content == "server acceptance");
        await chat.MutateMessageAsync(user, channel, serverMessage.Id, Guid.NewGuid(), "reaction", null, "👍", true);
        Check(serverMessage.Reactions.Count == 1 && chat.GetReceipt(user.Id, serverMessage.OperationId!.Value) != null,
            "server messages use receipt acceptance and support ordinary mutations");
        var media = await chat.SendTextAsync(user, channel, Guid.NewGuid(), "", images: ["/uploads/fixture.png"]);
        try
        {
            await chat.MutateMessageAsync(user, channel, media.MessageId, Guid.NewGuid(), "edit", "forbidden", null, false);
            throw new Exception("Media edit should fail");
        }
        catch (ChatSendException error) { Check(error.Code == "media_message", "media edit policy is enforced before acceptance"); }

        var lockMethod = typeof(ChatService).GetMethod("LockAcceptance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        // A room deleted while a mutation waits for its message lock must not leave a receipt
        // that acknowledges a change to nothing.
        var admin = users.GetAllUsers().First(u => users.IsAdmin(u.Id));
        var doomed = chat.CreateRoom(admin.Id, admin.Username, "doomedfixture", sinceJoined: false)!;
        var doomedMessage = await chat.SendTextAsync(user, doomed.Id, Guid.NewGuid(), "about to vanish");
        var ghostOperation = Guid.NewGuid();
        var hold = await (Task<IDisposable>)lockMethod.Invoke(chat, ["message:" + doomedMessage.MessageId])!;
        var ghost = chat.MutateMessageAsync(user, doomed.Id, doomedMessage.MessageId, ghostOperation, "edit", "ghost", null, false);
        await Task.Delay(50);
        chat.DeleteRoom(admin.Id, doomed.Id);
        hold.Dispose();
        try
        {
            await ghost;
            throw new Exception("Mutation in a deleted room should fail");
        }
        catch (ChatSendException error)
        {
            Check(error.Status == 404 && chat.GetReceipt(user.Id, ghostOperation) == null,
                "a mutation that reaches no message gets no receipt");
        }

        // Competing messages must not reuse one mutation receipt.
        var operation = Guid.NewGuid();
        var results = await Task.WhenAll(new[] { target.Id, serverMessage.Id }.Select(async id =>
        {
            try
            {
                await chat.MutateMessageAsync(user, channel, id, operation, "edit", "shared operation", null, false);
                return true;
            }
            catch (ChatSendException error) when (error.Status == 409) { return false; }
        }));
        Check(results.Count(r => r) == 1 && new[] { target, serverMessage }.Count(m => m.Content == "shared operation") == 1,
            "one operation ID cannot accept mutations of two messages concurrently");

        // Both aliases must wait on the resolved message lock. Otherwise an edit through
        // an outgoing operation ID can race a delete addressed by the accepted server ID.
        var lease = await (Task<IDisposable>)lockMethod.Invoke(chat, ["message:" + target.Id])!;
        var aliased = chat.MutateMessageAsync(user, channel, sent.OperationId, Guid.NewGuid(), "edit", "aliased edit", null, false);
        try
        {
            await Task.Delay(50);
            Check(!aliased.IsCompleted, "pending-operation alias shares the accepted message mutation lock");
        }
        finally { lease.Dispose(); }
        await aliased;
        Check(target.Content == "aliased edit", "pending-operation alias edits the accepted message");

        var capped = (await users.CreateUserAsync("receiptcapfixture"))!;
        var capReceipts = new List<TextSendReceipt>();
        for (var i = 0; i < ChatReceiptCleanup.MaxReceiptsPerUser + 2; i++)
        {
            var cappedReceipt = new TextSendReceipt
            {
                UserId = capped.Id,
                OperationId = Guid.NewGuid(),
                ChannelId = channel,
                MessageId = Guid.NewGuid(),
                ContentHash = "count fixture",
                // The last acceptance follows a backward clock step and must still survive the cap.
                AcceptedAt = DateTime.UtcNow.AddHours(i == ChatReceiptCleanup.MaxReceiptsPerUser + 1 ? -2 : -1).AddMilliseconds(i)
            };
            capReceipts.Add(cappedReceipt);
            chat.AddReceipt(cappedReceipt);
        }
        Check(chat.GetReceipt(capped.Id, capReceipts[0].OperationId) == null
            && chat.GetReceipt(capped.Id, capReceipts[1].OperationId) == null
            && chat.GetReceipt(capped.Id, capReceipts[2].OperationId) != null
            && chat.GetReceipt(capped.Id, capReceipts[^1].OperationId) != null
            && chat.GetReceipt(user.Id, sent.OperationId) != null,
            "receipt cap follows acceptance order across backward clock steps and isolates accounts");

        var kept = await chat.SendTextAsync(user, channel, Guid.NewGuid(), "kept after receipt expiry");
        var stale = new TextSendReceipt
        {
            UserId = user.Id,
            OperationId = Guid.NewGuid(),
            MessageId = kept.MessageId,
            ChannelId = channel,
            ContentHash = "expiry fixture",
            AcceptedAt = DateTime.UtcNow.AddDays(-2)
        };
        chat.AddReceipt(stale);
        if (db != null)
        {
            db.TextSendReceipts.Add(stale);
            await db.SaveChangesAsync();
        }
        chat.PruneReceipts(DateTime.UtcNow - ChatReceiptCleanup.Retention);
        await persistence.FlushAsync();
        Check(chat.GetReceipt(user.Id, stale.OperationId) == null && chat.GetReceipt(user.Id, sent.OperationId) != null
            && (db == null || !await db.TextSendReceipts.AnyAsync(r => r.OperationId == stale.OperationId) && await db.TextSendReceipts.AnyAsync(r => r.OperationId == sent.OperationId)),
            "receipt cleanup removes expired entries from memory and the database copy and preserves the retry window");
        Check(chat.GetMessageById(channel, kept.MessageId) != null && (db == null || await db.Messages.AnyAsync(m => m.Id == kept.MessageId)),
            "receipt expiry never deletes accepted messages");
        var env = services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var folder = Path.Combine(env.ContentRootPath, "Data", "upload-receipts", user.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        var expired = Guid.NewGuid().ToString("N");
        var fresh = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(folder, expired + ".json"), "{}");
        File.WriteAllText(Path.Combine(folder, fresh + ".json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(folder, expired + ".json"), DateTime.UtcNow.AddDays(-2));
        Check(Yap.Endpoints.TusEndpoints.Completed(env, user.Id, expired) == null
            && Yap.Endpoints.TusEndpoints.Completed(env, user.Id, fresh) != null
            && Yap.Endpoints.TusEndpoints.Completed(env, other.Id, fresh) == null,
            "upload receipt lookup enforces lifetime and account isolation before cleanup");
        Yap.Endpoints.TusEndpoints.PruneReceipts(env, DateTime.UtcNow - ChatReceiptCleanup.Retention);
        Check(!File.Exists(Path.Combine(folder, expired + ".json"))
            && Yap.Endpoints.TusEndpoints.Completed(env, user.Id, fresh) != null
            && Yap.Endpoints.TusEndpoints.Completed(env, user.Id, fresh) != null,
            "upload cleanup removes expired files and preserves replayable fresh receipts");
    }
    static void Check(bool condition, string label)
    {
        if (!condition)
            throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
}
