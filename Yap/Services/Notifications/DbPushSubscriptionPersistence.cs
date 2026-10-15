using Microsoft.EntityFrameworkCore;
using Yap.Models.Notifications;
using Yap.Services.Persistence;

namespace Yap.Services.Notifications;

/// <summary>Push subscriptions in the database: loaded once at startup, written through the writer.</summary>
public sealed class DbPushSubscriptionPersistence(IServiceProvider services, PersistenceWriter writer) : IPushSubscriptionPersistence
{
    private readonly IDbContextFactory<ChatDbContext>? _db = services.GetService<IDbContextFactory<ChatDbContext>>();

    public Task<List<PushSubscription>> LoadAllAsync()
        => _db is null ? Task.FromResult(new List<PushSubscription>()) : Loads.PushSubscriptionsAsync(_db);

    public Task SaveAsync(PushSubscription subscription)
    {
        writer.Enqueue(Writes.PushSubscription(subscription));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string endpoint)
    {
        writer.Enqueue(Writes.DeletePushSubscription(endpoint));
        return Task.CompletedTask;
    }
}
