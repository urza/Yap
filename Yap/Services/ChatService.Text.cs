using System.Security.Cryptography;
using System.Text;
using Yap.Models;

namespace Yap.Services;

public sealed class ChatSendException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public partial class ChatService
{
    public int MaxTextLength => _limits.MaxTextLength;

    public async Task<TextSendReceipt> SendTextAsync(User user, Guid channelId, Guid operationId, string? content, Guid? replyToMessageId = null, List<string>? images = null, List<string>? videos = null, List<GifAttachment>? gifs = null,
        string? mediaIdentity = null, Func<Task<(List<string>? Images, List<string>? Videos, List<GifAttachment>? Gifs)>>? resolveMedia = null)
    {
        content ??= "";
        var hasMedia = mediaIdentity != null || (images?.Count ?? 0) + (videos?.Count ?? 0) + (gifs?.Count ?? 0) > 0;
        if (operationId == Guid.Empty || (!hasMedia && string.IsNullOrWhiteSpace(content)) || content.Length > MaxTextLength)
            throw new ChatSendException(400, "invalid_message", $"Enter between 1 and {MaxTextLength} characters.");
        // Receipt hashes are persistent protocol data. Preserve the existing field names,
        // order and raw-text case so a retry after an upgrade matches its saved receipt.
        string receiptPayload;
        if (mediaIdentity != null)
            receiptPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                content,
                replyToMessageId,
                mediaIdentity
            });
        else if (hasMedia)
            receiptPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                content,
                replyToMessageId,
                images,
                videos,
                gifs
            });
        else if (replyToMessageId != null)
            receiptPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                content,
                replyToMessageId
            });
        else
            receiptPayload = content;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receiptPayload)));
        using (await LockAcceptance($"operation:{user.Id}:{operationId}"))
        {
            var previous = GetReceipt(user.Id, operationId);
            if (previous != null)
            {
                if (previous.ChannelId != channelId || previous.ContentHash != hash)
                    throw new ChatSendException(409, "operation_conflict", "This send ID was already used for different content.");
                return previous; // Return even after a delete or a later permission change; never resend.
            }
            var channel = GetChannel(channelId);
            if (channel == null || !channel.CanAccess(user.Id))
                throw new ChatSendException(404, "conversation_unavailable", "This conversation is no longer available.");
            if (!channel.CanWrite(user.Id, IsAdmin(user.Id)))
                throw new ChatSendException(403, "read_only", "You no longer have permission to send in this conversation.");
            if (replyToMessageId is { } replyId && GetMessageById(channelId, replyId) is { } target && !CanReadMessage(user, target))
                throw new ChatSendException(404, "message_unavailable", "The reply target is unavailable.");
            // Resolve uploads/GIFs only for a new acceptance. A deleted file or GIF must not
            // make a previously accepted operation fail its replay or repeat provider side effects.
            if (resolveMedia != null)
                (images, videos, gifs) = await resolveMedia();
            var message = new ChatMessage(channelId, user.Id, user.Username, content, DateTime.UtcNow, images, replyToMessageId, videos, gifs) { OperationId = operationId, ReplyToMessageId = replyToMessageId };
            var receipt = new TextSendReceipt
            {
                UserId = user.Id,
                OperationId = operationId,
                ChannelId = channelId,
                MessageId = message.Id,
                ContentHash = hash,
                AcceptedAt = message.Timestamp
            };
            await AcceptMessageAsync(channel, message, receipt);
            return receipt;
        }
    }

    // Memory is the data layer. Under readStateGate the message, its receipt and the recipients'
    // unread counts become visible together. The database copy (when enabled) is queued inside
    // the conversation lock, so the single writer gets it before any mutation of this message
    // and in memory order. No request waits for it; a failed copy only logs.
    private async Task AcceptMessageAsync(Channel channel, ChatMessage message, TextSendReceipt receipt, bool countUnread = true)
    {
        List<Guid> recipients = countUnread ? UnreadRecipients(channel.Id, message.UserId) : [];
        ChannelReadState[] states;
        await readStateGate.WaitAsync();
        try
        {
            states = recipients.Select(id =>
            {
                var state = CopyReadState(id, channel.Id);
                state.ReceivedCount++;
                state.UnreadCount++;
                return state;
            }).ToArray();
            lock (GetChannelLock(channel.Id))
            {
                // A room deletion can take the list away between the access check and here.
                // No receipt may acknowledge a message that reached no list.
                if (!_channelMessages.TryGetValue(channel.Id, out var messages))
                    throw new ChatSendException(404, "conversation_unavailable", "This conversation is no longer available.");
                AddReceipt(receipt);
                messages.Add(message);
                IndexMessage(message, 1);
                if (!countUnread)
                    messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                _persistence.PersistAcceptedMessage(message, receipt, states);
            }
            // The message is visible before its arrival checkpoint can be observed.
            foreach (var state in states)
                _readStates[(state.UserId, channel.Id)] = state;
        }
        finally { readStateGate.Release(); }
        if (channel.IsDirectMessage)
            foreach (var state in states)
                RunNotification(() => _audit.RecordUnreadChange(_userService.GetById(state.UserId)?.Username ?? "?", DescribeChannel(channel), "+1",
                    state.UnreadCount, $"msg from {message.Username}", "—"));
        // Acceptance is authoritative. Notification failures cannot turn it into a retry.
        try
        {
            if (countUnread)
                await PublishMessageAsync(channel, message, recipients);
            else
            {
                // Historical admin fixtures appear in streams without firing arrival push.
                _changes.Touch(channel.Id, message.Id, history: false);
                NotifySubscribers(OnMessageReceived, message);
            }
        }
        catch (Exception error) { _logger.LogError(error, "Post-commit notification failed for {MessageId}", message.Id); }
    }

    public async Task<Channel> OpenDirectMessageAsync(User user, string username)
    {
        var other = _userService.GetByUsername(username);
        if (other == null || other.Id == user.Id)
            throw new ChatSendException(404, "user_unavailable", "That direct message is unavailable.");
        var pair = new[] { user.Id, other.Id }.Order().ToArray();
        using (await LockAcceptance($"dm:{pair[0]}:{pair[1]}"))
        {
            var existing = GetDMChannels(user.Username).FirstOrDefault(c => c.IsDMBetween(user.Id, other.Id));
            if (existing != null)
                return existing;
            var channel = Channel.CreateDM(user.Id, user.Username, other.Id, other.Username);
            _channels[channel.Id] = channel;
            _channelMessages[channel.Id] = new();
            _channelTypingUsers[channel.Id] = new();
            _persistence.PersistChannel(channel);
            _changes.Touch(channel.Id);
            OnChannelCreated?.Invoke(channel);
            return channel;
        }
    }
}
