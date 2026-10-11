using Yap.Models;

namespace Yap.Services;

public sealed class MemoryChatStore : IChatStore
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid UserId, Guid OperationId), TextSendReceipt> receipts = new();
    private readonly Dictionary<Guid, LinkedList<TextSendReceipt>> receiptOrder = new();
    // References are the accepted live objects, not a second copy of message state.
    private readonly Dictionary<Guid, ChatMessage> messages = new();

    public Task<TextSendReceipt?> GetTextReceiptAsync(Guid userId, Guid operationId)
    {
        lock (gate) return Task.FromResult(receipts.GetValueOrDefault((userId, operationId)));
    }
    public Task<ChatMessage?> GetAcceptedMessageAsync(Guid messageId)
    {
        lock (gate) return Task.FromResult(messages.GetValueOrDefault(messageId));
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
