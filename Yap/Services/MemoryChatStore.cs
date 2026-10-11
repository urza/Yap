using Yap.Models;

namespace Yap.Services;

public sealed class MemoryChatStore : IChatStore
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid UserId, Guid OperationId), TextSendReceipt> receipts = new();
    private readonly Dictionary<Guid, LinkedList<TextSendReceipt>> receiptOrder = new();
    // Accepted message IDs, read only by the duplicate-ID check on acceptance and the
    // missing-target check on mutation. ChatService owns message state: the values are
    // its live objects, kept for their ChannelId, not a second copy to read back.
    private readonly Dictionary<Guid, ChatMessage> messages = new();

    public Task<TextSendReceipt?> GetTextReceiptAsync(Guid userId, Guid operationId)
    {
        lock (gate) return Task.FromResult(receipts.GetValueOrDefault((userId, operationId)));
    }
    public Task PersistTextAcceptanceAsync(ChatMessage message, TextSendReceipt receipt, IReadOnlyList<Guid>? recipients = null)
    {
        lock (gate)
        {
            if (messages.ContainsKey(message.Id) || receipts.ContainsKey((receipt.UserId, receipt.OperationId)))
                throw new ChatSendException(409, "operation_conflict", "Operation ID already accepted.");
            AddReceipt(receipt);
            messages.Add(message.Id, message);
        }
        // ChatService publishes checkpoints and the message before emitting any events.
        return Task.CompletedTask;
    }
    public Task PersistMutationAsync(TextSendReceipt receipt, ChatMutation mutation)
    {
        lock (gate)
        {
            if (receipts.ContainsKey((receipt.UserId, receipt.OperationId)))
                throw new ChatSendException(409, "operation_conflict", "Operation ID already accepted.");
            // Same rule as SQLite: ChatService checked the target, but a room deletion can
            // remove it before this call. A receipt here would acknowledge a change that
            // never reaches any message. Delete stays idempotent.
            if (mutation.Kind != "delete" && messages.GetValueOrDefault(receipt.MessageId)?.ChannelId != receipt.ChannelId)
                throw new ChatSendException(404, "message_unavailable", "Message unavailable.");
            AddReceipt(receipt);
            if (mutation.Kind == "delete") messages.Remove(receipt.MessageId);
        }
        // ChatService applies the validated mutation to the live object after acceptance.
        return Task.CompletedTask;
    }
    public Task PersistReadStateAsync(ChannelReadState state) => Task.CompletedTask;
    public Task DeleteChannelMessagesAsync(Guid channelId)
    {
        lock (gate)
            foreach (var id in messages.Where(p => p.Value.ChannelId == channelId).Select(p => p.Key).ToArray())
                messages.Remove(id);
        return Task.CompletedTask;
    }
    // Called under gate, so a successful acceptance never exceeds the per-account cap.
    private void AddReceipt(TextSendReceipt receipt)
    {
        receipts.Add((receipt.UserId, receipt.OperationId), receipt);
        if (!receiptOrder.TryGetValue(receipt.UserId, out var order))
            receiptOrder[receipt.UserId] = order = new();
        // Count eviction follows acceptance order, independent of wall-clock adjustments.
        order.AddLast(receipt);
        while (order.Count > ChatReceiptCleanup.MaxReceiptsPerUser)
        {
            receipts.Remove((receipt.UserId, order.First!.Value.OperationId));
            order.RemoveFirst();
        }
    }

    public Task PruneReceiptsAsync(DateTime before)
    {
        lock (gate)
            foreach (var (userId, order) in receiptOrder.ToArray())
            {
                // Acceptance order need not match UTC order after a clock correction.
                for (var node = order.First; node != null;)
                {
                    var next = node.Next;
                    if (node.Value.AcceptedAt < before)
                    {
                        receipts.Remove((userId, node.Value.OperationId));
                        order.Remove(node);
                    }
                    node = next;
                }
                if (order.Count == 0) receiptOrder.Remove(userId);
            }
        return Task.CompletedTask;
    }
}
