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

    public Task SaveAsync(PushSubscription subscription)
        => _persistence.SavePushSubscriptionAsync(subscription);

    public Task RemoveAsync(string endpoint)
        => _persistence.RemovePushSubscriptionAsync(endpoint);

    public Task RemoveByUsernameAsync(string username)
        => _persistence.RemovePushSubscriptionsByUsernameAsync(username);
}
