using Yap.Models;
using Yap.Contracts;
using Yap.Services;

static class SnapshotFixture
{
    // A test inspection view, not an additional server/client wire protocol.
    public static SnapshotView FullView(this ChatProjection snapshots, User user)
    {
        var conversations = snapshots.Channels(user).Select(c => snapshots.Conversation(user, c.Id)).OfType<ConversationView>().ToArray();
        return snapshots.Header(user) with
        {
            Sequence = snapshots.Stamp(),
            Conversations = conversations,
            Revision = string.Join(";", conversations.Select(c => $"{c.Id}:{c.ContentVersion}"))
        };
    }
}
