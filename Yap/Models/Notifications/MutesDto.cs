namespace Yap.Models.Notifications;

/// <summary>The three mute levels of NT-16 as the client keeps them: the server, the two classes, the single conversations.</summary>
public sealed record MutesDto(ServerMuteDto Server, NotificationMode DmMode, NotificationMode RoomMode, bool NewDmsMuted, Dictionary<Guid, bool> Channels);

public sealed record ServerMuteDto(bool Muted, DateTime? Until);
