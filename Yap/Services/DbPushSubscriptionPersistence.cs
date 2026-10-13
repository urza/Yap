using Yap.Models;

namespace Yap.Services;

public class DbPushSubscriptionPersistence : IPushSubscriptionPersistence
{
    private readonly ChatPersistenceService _persistence;

    public DbPushSubscriptionPersistence(ChatPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public Task<List<PushSubscription>> LoadAllAsync()
        => _persistence.GetAllPushSubscriptionsAsync();

    // Writes are queued; the Task shape stays for the JSON implementation, which does real IO.
    public Task SaveAsync(PushSubscription subscription)
    {
        _persistence.SavePushSubscription(subscription);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string endpoint)
    {
        _persistence.RemovePushSubscription(endpoint);
        return Task.CompletedTask;
    }

    public Task RemoveByUsernameAsync(string username)
    {
        _persistence.RemovePushSubscriptionsByUsername(username);
        return Task.CompletedTask;
    }
}
