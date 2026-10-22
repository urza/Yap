using System.Security.Claims;
using Yap.Configuration;
using Yap.Helpers;
using Yap.Models.Accounts;
using Yap.Models.Chat;
using Yap.Models.Notifications;
using Yap.Services.Accounts;
using Yap.Services.Chat;
using Yap.Services.Gifs;
using Yap.Services.Media;
using Yap.Services.Notifications;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>
/// GET /api/bootstrap: one document with every value the client needs to draw the frame. Fetched at start and
/// after every hub connect; the client replaces its shared store with it and applies hub messages on top.
/// Anonymous: without a user it returns the project name and the login texts only, so the login, verify and
/// invite pages get their texts in the same first call (docs/architecture/bootstrap-and-events.md).
///
/// The directory depends on who asks (docs/dmonly.md): the admin gets every account; a person gets the admin,
/// the bot and themself, because those are the only people a person can see or talk to. Presence is cut the
/// same way.
/// </summary>
public static class BootstrapEndpoints
{

    public static void MapBootstrap(this RouteGroupBuilder api)
    {
        api.MapGet("/bootstrap", (HttpContext http, ClaimsPrincipal principal,
            UserService users, ChatService chat, PresenceService presence, NotificationSettingsService mutes, GifService gifs,
            AccessLinkService links, RegistrationGateService gate, LinkPreviewSettingsService previews,
            PushNotificationService push, SystemBotService bot, ServerConfig config) =>
        {
            if (!principal.IsSignedIn())
                return Results.Ok(new { config = new { config.ProjectName }, texts = config.LoginTexts() });

            var me = users.GetById(principal.Id());
            if (me is null) return Results.Unauthorized();

            var ip = IpHelper.GetClientIp(http);
            AuthCookie.Set(http.Response, me.Token);   // every visit extends the year (AU-18)
            users.RecordKnownIp(me.Id, ip);             // smart login remembers this network (AU-09)
            links.ObserveOrigin(me.Id, http.Request.Origin());           // this person's links use the address this person used (AU-15)

            var isAdmin = users.IsAdmin(me.Id);
            var directory = users.GetAllUsers().Where(u => isAdmin || u.Id == me.Id || users.IsAdmin(u.Id) || bot.IsBotUser(u.Username)).ToList();
            var names = directory.Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new BootstrapDto(
                User: MeDto.From(me, isAdmin, links.HasActiveLink(me.Id)),
                Config: new ClientConfigDto(config.ProjectName, config.UploadUrl, config.MaxUploadSizeMB, VoiceService.MaxSeconds,
                    config.Ipv4BeaconUrl,
                    previews.Enabled, previews.MediaCachingEnabled,
                    new GifProviderDto(gifs.Provider.DisplayName, gifs.IsConfigured, gifs.Provider.AttributionImageUrl),
                    push.GetPublicKey(), BuildInfo.Time, gate.SmartMode, ThemeRegistry.All),
                Texts: config.Texts(),
                Users: directory.Select(u => UserDto.From(u, users.IsAdmin(u.Id), bot.IsBotUser(u.Username))).ToList(),
                Presence: presence.Effective().Where(e => names.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value),
                Dms: chat.DmConversations(me.Id).Select(d => new DmDto(d.Channel.Id, d.Partner, d.LastMessageAt)).ToList(),
                Unread: chat.UnreadCounts(me.Id),
                Mutes: mutes.For(me.Id),
                Emoji: new EmojiStateDto(users.GetRecentEmojis(me.Username), chat.QuickReactions(me.Id)),
                FavoriteGifIds: gifs.GetFavorites(me.Id).Select(g => g.Id).ToList()));
        }).AllowAnonymous();
    }
}

public sealed record BootstrapDto(
    MeDto User, ClientConfigDto Config, TextsDto Texts, List<UserDto> Users,
    Dictionary<string, EffectiveStatus> Presence, List<DmDto> Dms, Dictionary<Guid, int> Unread, MutesDto Mutes,
    EmojiStateDto Emoji, List<Guid> FavoriteGifIds);

public sealed record ClientConfigDto(
    string ProjectName, string UploadUrl, int MaxUploadMB, int VoiceMaxSeconds, string BeaconUrl,
    bool LinkPreviews, bool MediaCaching, GifProviderDto GifProvider, string? VapidPublicKey, string BuildStamp, bool SmartLoginAllowed,
    IReadOnlyList<ThemeDefinition> Themes);   // the Settings theme grid (ST-08): the registry is the server's

public sealed record GifProviderDto(string Name, bool Configured, string AttributionImageUrl);

public sealed record EmojiStateDto(List<string> Recent, List<string> QuickReactions);
