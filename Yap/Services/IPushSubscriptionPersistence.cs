using Yap.Models;

namespace Yap.Services;

public interface IPushSubscriptionPersistence
{
    Task<List<PushSubscription>> LoadAllAsync();
    Task SaveAsync(PushSubscription subscription);
    Task RemoveAsync(string endpoint);
    Task RemoveByUsernameAsync(string username);
}
