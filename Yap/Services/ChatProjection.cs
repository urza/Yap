using System.Collections.Concurrent;
using System.Globalization;
using Yap.Configuration;
using Yap.Contracts;
using Yap.Helpers;
using Yap.Models;
using Yap.Services.Gifs;

namespace Yap.Services;

/// <summary>
/// Projects authorized chat state into the credential-free views the browser receives (Contracts/),
/// and wraps them into <see cref="ChatUpdate"/> packets with the epoch, sequence and revision the
/// client reconciles against. Never serializes User, Channel or ChatMessage themselves.
/// </summary>
public sealed class ChatProjection(ChatService chat, UserService users, ChatConfigService branding, SystemBotService bots, NotificationSettingsService notifications, GifService gifs, LinkPreviewService previews, LinkPreviewSettingsService previewSettings, MediaCacheService media, IWebHostEnvironment env, ChatChangeTracker changes, ChatLimits limits)
{
    private readonly ConcurrentDictionary<Guid, UserView> summaries = new();
    public void InvalidateUser(Guid id) => summaries.TryRemove(id, out _);
    public string Epoch => changes.Epoch;
    public long Stamp() => changes.Stamp();
    public UserView Summary(User user) => summaries.GetOrAdd(user.Id, _ => BuildSummary(user));
    public int RecentLimit => limits.RecentMessageLimit;
    private UserView BuildSummary(User user)
    {
        var picture = user.ProfilePictureUrl;
        // Profile filenames are overwritten; version the URL so immutable offline caching stays fresh.
        if (picture?.StartsWith("/uploads/profiles/", StringComparison.Ordinal) == true)
        {
            var file = Path.Combine(env.WebRootPath, "uploads", "profiles", Path.GetFileName(picture.Split('?')[0]));
            if (File.Exists(file))
                picture = picture.Split('?')[0] + "?v=" + File.GetLastWriteTimeUtc(file).Ticks;
        }
        return new(user.Id, user.Username, user.EffectiveDisplayName, picture, AvatarColor.GetGradientCss(user.Username), users.IsAdmin(user.Id), bots.IsBotUser(user.Username), user.Bio, user.Country, user.CreatedAt);
    }
    public bool IsDirectMessage(Guid? id) => id is { } value && chat.GetChannel(value)?.IsDirectMessage == true;

    public Channel[] Channels(User user) => chat.GetRooms().Concat(chat.GetDMChannels(user.Username))
        .Where(c => c.CanAccess(user.Id)).DistinctBy(c => c.Id).ToArray();

    public ConversationView? Metadata(User user, Guid id, bool? hasMore = null)
    {
        var channel = chat.GetChannel(id);
        if (channel == null || !channel.CanAccess(user.Id))
            return null;
        var checkpoint = chat.GetReadCheckpoint(user.Id, id);
        return new(id, channel.IsDirectMessage ? "dm" : "room",
            channel.IsDirectMessage ? users.GetById(channel.GetOtherParticipantId(user.Id) ?? Guid.Empty)?.EffectiveDisplayName
                ?? channel.GetOtherParticipant(user.Username) ?? "Deleted user" : channel.Name,
            channel.Description, channel.IsDefault, channel.IsDirectMessage
                ? "/dm/" + Uri.EscapeDataString(channel.GetOtherParticipant(user.Username) ?? "")
                : channel.IsDefault ? "/lobby" : $"/room/{id}", channel.CanWrite(user.Id, users.IsAdmin(user.Id)), hasMore ?? chat.HasMoreMessages(user, id, RecentLimit),
            checkpoint.Unread, chat.IsChannelMuted(user.Id, id), [], checkpoint.Received, checkpoint.ReadThrough,
            channel.IsDirectMessage ? chat.IsChannelMuted(user.Id, id) : !notifications.IsServerMuted(user) && user.NotifRoomMode == NotificationMode.Individual && notifications.IsChannelMutedIndividually(user, id, false), changes.ContentVersion(id), !users.IsAdmin(user.Id) && (channel.HistoryLimit != HistoryLimit.Unlimited || channel.SinceJoined), changes.HistoryVersion(id));
    }

    public ConversationView? Conversation(User user, Guid id, int? limit = null)
    {
        lock (chat.GetChannelLock(id))
        {
            var metadata = Metadata(user, id);
            if (metadata == null)
                return null;
            var page = chat.GetMessagesPaginated(id, limit ?? RecentLimit, isAdmin: users.IsAdmin(user.Id), userId: user.Id);
            return metadata with
            {
                HasMore = page.HasMore,
                Messages = page.Messages.Select(m => Message(m, user.Id)).ToArray()
            };
        }
    }

    public MessageView Message(ChatMessage message, Guid? viewerId = null)
    {
        ReactionView[] reactions;
        lock (message.Reactions)
            reactions = message.Reactions.GroupBy(r => r.Emoji)
                .Select(g => new ReactionView(g.Key, g.Select(r => r.Username).ToArray())).ToArray(); // reaction order, as the old tooltip (audit A-22)
        var author = users.GetById(message.UserId);
        var target = message.ReplyToMessageId is { } targetId ? chat.GetMessageById(message.ChannelId, targetId) : null;
        var viewer = viewerId is { } id ? users.GetById(id) : null;
        ReplyView? reply = null;
        if (target != null && viewer != null && chat.CanReadMessage(viewer, target))
        {
            var targetAuthor = users.GetById(target.UserId);
            reply = new(target.Id, targetAuthor != null ? Summary(targetAuthor) : new(target.UserId, target.Username, target.Username, null, AvatarColor.GetGradientCss(target.Username)),
                string.IsNullOrEmpty(target.Content) && target.HasMedia ? "Click to see attachment" : target.Content);
        }
        return new(message.Id, viewerId == message.UserId ? message.OperationId : null, author != null ? Summary(author) : new(message.UserId, message.Username,
            message.Username, null, AvatarColor.GetGradientCss(message.Username)), message.Content, DateTime.SpecifyKind(message.Timestamp, DateTimeKind.Utc),
            message.IsEdited, message.ReplyToMessageId,
            message.ImageUrls.Select(url => new ImageView(ImageService.GetMediumUrl(url), ImageService.GetLargeUrl(url))).ToArray(),
            message.VideoUrls.ToArray(), message.GifAttachments.Count, reactions,
            message.GifAttachments.Select(a => gifs.GetEntry(a.GifEntryId)).OfType<GifEntry>().Select(e => Gif(e, viewerId ?? Guid.Empty, users.GetById(e.UploadedByUserId ?? Guid.Empty)?.EffectiveDisplayName)).ToArray(),
            LinkPreviewService.ExtractUrls(message.Content).Take(5).Select(url =>
            {
                var preview = previewSettings.Enabled && !message.HasMedia
                    ? previews.GetPreview(message.Id, url) : previews.GetCachedPreview(url);
                var cached = media.GetCachedMedia(url);
                if (cached == null && preview?.HasContent != true && preview?.CachedMediaUrl == null)
                    return null;
                // Enrich media on disk hits too: dimensions reserve portrait
                // players before metadata loads, and the sidecar title survives empty OG scrapes.
                return new LinkPreview
                {
                    Url = url,
                    Title = string.IsNullOrEmpty(preview?.Title) ? cached?.Title : preview.Title,
                    Description = preview?.Description,
                    ImageUrl = string.IsNullOrEmpty(preview?.ImageUrl) ? cached?.Thumbnail : preview.ImageUrl,
                    SiteName = preview?.SiteName,
                    Failed = preview?.Failed ?? false,
                    CachedMediaUrl = cached?.LocalUrl ?? preview?.CachedMediaUrl,
                    MediaType = cached?.MediaType ?? preview?.MediaType,
                    CachedPosterUrl = cached?.PosterUrl ?? preview?.CachedPosterUrl,
                    MediaDurationSeconds = cached?.DurationSeconds ?? preview?.MediaDurationSeconds,
                    MediaWidth = cached is { Width: > 0, Height: > 0 } ? cached.Width : preview?.MediaWidth,
                    MediaHeight = cached is { Width: > 0, Height: > 0 } ? cached.Height : preview?.MediaHeight
                };
            }).OfType<LinkPreview>().ToArray(), reply);
    }

    /// <summary>The GIF shape embedded in messages and picker listings; only favorite varies by viewer.</summary>
    public object Gif(GifEntry entry, Guid viewerId, string? uploader = null) => new
    {
        entry.Id,
        entry.Width,
        entry.Height,
        url = entry.GifUrl ?? entry.RemoteGifUrl ?? entry.Mp4Url ?? entry.WebmUrl ?? entry.RemoteMp4Url ?? entry.RemoteWebmUrl,
        preview = entry.PreviewUrl ?? entry.GifUrl ?? entry.RemoteGifUrl ?? entry.WebmUrl ?? entry.Mp4Url ?? entry.RemoteMp4Url,
        uploader,
        entry.ServerFolder,
        title = string.Join(" ", gifs.GetTags(entry)),
        favorite = gifs.IsFavorite(viewerId, entry.Id),
        entry.UploadedByUserId
    };

    public (string Epoch, long Sequence, ConversationView? Conversation, MessageView? Message) Capture(User user, Guid id, Guid? messageId, bool full = false)
    {
        lock (chat.GetChannelLock(id))
        {
            var conversation = full ? Conversation(user, id) : Metadata(user, id);
            var message = messageId is { } target ? chat.GetMessageById(id, target) : null;
            return (Epoch, Stamp(), conversation,
                conversation != null && message != null && chat.CanReadMessage(user, message) ? Message(message, user.Id) : null);
        }
    }

    // Expensive media/emoji projection is shared; only reply permission and favorite flags vary by viewer.
    public MessageView ForViewer(MessageView projected, ChatMessage message, User viewer)
    {
        var target = message.ReplyToMessageId is { } id ? chat.GetMessageById(message.ChannelId, id) : null;
        ReplyView? reply = null;
        if (target != null && chat.CanReadMessage(viewer, target))
        {
            var author = users.GetById(target.UserId);
            reply = new(target.Id, author != null ? Summary(author) : new(target.UserId, target.Username, target.Username, null, AvatarColor.GetGradientCss(target.Username)),
                string.IsNullOrEmpty(target.Content) && target.HasMedia ? "Click to see attachment" : target.Content);
        }
        return projected with
        {
            OperationId = viewer.Id == message.UserId ? message.OperationId : null,
            Reply = reply,
            Gifs = message.GifAttachments.Count == 0 ? projected.Gifs :
            message.GifAttachments.Select(a => gifs.GetEntry(a.GifEntryId)).OfType<GifEntry>()
                .Select(e => Gif(e, viewer.Id, users.GetById(e.UploadedByUserId ?? Guid.Empty)?.EffectiveDisplayName)).ToArray()
        };
    }

    private static object DateSettings(User user)
    {
        var zone = LocaleResolver.ResolveTimeZone(user.TimeZone) ?? TimeZoneInfo.Utc;
        var format = LocaleResolver.GetFormat(user.DateFormat);
        var culture = CultureInfo.InvariantCulture;
        try
        {
            culture = LocaleResolver.SanitizeCulture(new CultureInfo(LocaleResolver.GetCultureOverride(user.DateFormat) ?? user.Locale ?? ""));
        }
        catch { }
        var id = zone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
            id = iana;
        return new
        {
            zone = id,
            offsetMinutes = zone.GetUtcOffset(DateTime.UtcNow).TotalMinutes,
            format.Time,
            format.DateInYear,
            format.FullDate,
            culture.DateTimeFormat.DateSeparator,
            culture.DateTimeFormat.TimeSeparator,
            culture.DateTimeFormat.AMDesignator,
            culture.DateTimeFormat.PMDesignator
        };
    }

    public SnapshotView Header(User user)
    {
        var channels = Channels(user);
        var limit = Math.Min(RecentLimit, Math.Max(1, 20000 / Math.Max(1, channels.Length)));
        return new SnapshotView(ChatProtocol.Number, "", Epoch, 0, Summary(user), users.IsAdmin(user.Id), user.Theme ?? "discord-dark",
            user.FontSize, user.TimeZone, user.DateFormat, branding.ProjectName, limit,
            true, limits.MaxTextLength, users.GetAllUsers().Where(u => u.Id == user.Id || chat.HasActiveSession(u.Username) || channels.Any(c => c.IsDirectMessage && c.CanAccess(u.Id))).OrderBy(u => u.Username).Select(Summary).ToArray(), [], limits.MaxOperationsPerBatch, limits.MaxBatchBytes, limits.MaxFilesPerMessage, limits.AllowedExtensions, limits.MaxUploadBytes, limits.HistoryPageSize, limits.HistoryMaxMessages, limits.ReadBatch, limits.TypingTimeoutMs, limits.AwayAfterMs, DateSettings(user));
    }

    // ---- Packets: the HTTP responses and stream entries built from the views above ----

    public static string Revision(ConversationView value) => value.ContentVersion.ToString(CultureInfo.InvariantCulture);
    private static UserView[] Authors(IEnumerable<MessageView> messages) => messages.Select(m => m.Author).DistinctBy(u => u.Id).ToArray();
    /// <summary>A complete recent-window replacement for one conversation.</summary>
    public static ConversationUpdate Window(ConversationView value) => new(value.Id, value with
    {
        Messages = []
    },
        value.Messages, [], value.Messages.Select(m => m.Id).ToArray(), null, Revision(value));

    /// <summary>
    /// The reset packet a client starts from: header, every conversation's metadata, and the full
    /// window of the selected conversation unless the client proves it already has that revision.
    /// </summary>
    public ChatUpdate Bootstrap(User user, string? path, Guid? channelId = null, string? epoch = null, string? revision = null, Guid? knownUser = null)
    {
        var channels = Channels(user);
        var metadata = channels.Select(c => Metadata(user, c.Id)).OfType<ConversationView>().ToArray();
        var selected = metadata.FirstOrDefault(c => c.Id == channelId || Uri.UnescapeDataString(c.Path).Equals(Uri.UnescapeDataString(path ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            ?? metadata.FirstOrDefault(c => c.IsDefault);
        var unchanged = knownUser == user.Id && epoch == Epoch && selected != null && revision == Revision(selected);
        var capture = selected != null && !unchanged ? Capture(user, selected.Id, null, true) : default;
        var conversations = metadata.Select(c => c.Id == capture.Conversation?.Id
            ? Window(capture.Conversation!)
            : new ConversationUpdate(c.Id, c, [], [], null, null, Revision(c), true)).ToArray();
        // Stamp before header construction; each channel window has its own consistent capture.
        var sequence = capture.Sequence != 0 ? capture.Sequence : Stamp();
        return new(ChatProtocol.Number, user.Id, Epoch, sequence, Header(user), conversations, [],
            Authors(capture.Conversation?.Messages ?? []), null, true);
    }

    /// <summary>
    /// The packet for one conversation after a write or on request: the full window, or the one
    /// message named by <paramref name="messageId"/>, or metadata only.
    /// </summary>
    public ChatUpdate Update(User user, Guid id, Guid? messageId = null, bool full = false)
    {
        var capture = Capture(user, id, messageId, full);
        if (capture.Conversation == null)
            return new(ChatProtocol.Number, user.Id, capture.Epoch, capture.Sequence, null, [], [id], []);
        var conversation = capture.Conversation;
        var messages = full ? conversation.Messages : capture.Message is { } message ? new[] { message } : [];
        var update = full ? Window(conversation) : new ConversationUpdate(id, conversation,
            messages, messageId.HasValue && capture.Message == null ? [messageId.Value] : [],
            null, null, Revision(conversation));
        return new(ChatProtocol.Number, user.Id, capture.Epoch, capture.Sequence, null, [update], [], Authors(messages));
    }
}
