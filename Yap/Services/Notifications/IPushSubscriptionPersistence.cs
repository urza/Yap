using Yap.Models;
using Yap.Models.Notifications;

namespace Yap.Services.Notifications;

public interface IPushSubscriptionPersistence
{
    Task<List<PushSubscription>> LoadAllAsync();
    Task SaveAsync(PushSubscription subscription);
    Task RemoveAsync(string endpoint);
}
