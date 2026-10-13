using Yap.Models;

namespace Yap.Services;

public partial class ChatService
{
    // Receipts live here, next to the messages. A retry of an accepted operation is answered from
    // this dictionary; the database copy (optional) only refills it after a restart.
    private readonly object receiptGate = new();
    private readonly Dictionary<(Guid UserId, Guid OperationId), TextSendReceipt> receipts = new();
    // Per account, in acceptance order. The count cap evicts the oldest acceptance, not the
    // oldest AcceptedAt, so a backward clock step cannot evict the newest receipt.
    private readonly Dictionary<Guid, LinkedList<TextSendReceipt>> receiptOrder = new();

    public TextSendReceipt? GetReceipt(Guid userId, Guid operationId)
    {
        lock (receiptGate) return receipts.GetValueOrDefault((userId, operationId));
    }

    // Call once per accepted operation, inside the conversation lock that makes the change
    // visible. receiptGate nests inside that lock, never the other way round.
    internal void AddReceipt(TextSendReceipt receipt)
    {
        lock (receiptGate)
        {
            if (receipts.ContainsKey((receipt.UserId, receipt.OperationId)))
                throw new ChatSendException(409, "operation_conflict", "Operation ID already accepted.");
            Remember(receipt);
        }
    }

    // Startup refill from the database copy. Rows arrive in insertion order, so the cap below
    // keeps the same entries the database kept.
    private void LoadReceipts(IEnumerable<TextSendReceipt> stored)
    {
        lock (receiptGate)
            foreach (var receipt in stored)
                if (!receipts.ContainsKey((receipt.UserId, receipt.OperationId)))
                    Remember(receipt);
    }

    // Under receiptGate.
    private void Remember(TextSendReceipt receipt)
    {
        receipts.Add((receipt.UserId, receipt.OperationId), receipt);
        if (!receiptOrder.TryGetValue(receipt.UserId, out var order))
            receiptOrder[receipt.UserId] = order = new();
        order.AddLast(receipt);
        while (order.Count > ChatReceiptCleanup.MaxReceiptsPerUser)
        {
            receipts.Remove((receipt.UserId, order.First!.Value.OperationId));
            order.RemoveFirst();
        }
    }

    // Age-based retention, memory first. The database copy applies the same bound afterwards.
    public void PruneReceipts(DateTime before)
    {
        lock (receiptGate)
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
        _persistence.PruneReceipts(before);
    }
}
