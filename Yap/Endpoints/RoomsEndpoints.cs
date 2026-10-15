using System.Security.Claims;
using Yap.Models.Chat;
using Yap.Services.Accounts;
using Yap.Services.Chat;

namespace Yap.Endpoints;

/// <summary>The admin's room pages (AD-29 to AD-35). Everyone learns about a change through the <c>rooms</c> hub message.</summary>
public static class RoomsEndpoints
{
    public static void MapRooms(this RouteGroupBuilder api)
    {
        var rooms = api.MapGroup("/rooms").RequireAuthorization(YapCookieAuthHandler.AdminPolicy);

        rooms.MapPost("", (RoomRequest body, ClaimsPrincipal me, ChatService chat) =>
        {
            var (room, error) = chat.CreateRoom(me.Id(), body.Name, body.Description, Permission(body), body.HistoryLimit, body.SinceJoined);
            return room is null ? Results.Problem(error, statusCode: 400) : Results.Created($"/room/{room.Id}", RoomDto.From(room));
        });

        rooms.MapPatch("/{id:guid}", (Guid id, RoomRequest body, ClaimsPrincipal me, ChatService chat) =>
            chat.GetChannel(id) is null ? Results.NotFound()
            : chat.UpdateRoom(me.Id(), id, body.Name, body.Description, Permission(body), body.HistoryLimit, body.SinceJoined) is { } error
                ? Results.Problem(error, statusCode: 400)
                : Results.NoContent());

        rooms.MapDelete("/{id:guid}", (Guid id, ClaimsPrincipal me, ChatService chat) =>
            chat.DeleteRoom(me.Id(), id) ? Results.NoContent() : Results.Problem("Failed to delete channel.", statusCode: 400));

        rooms.MapPost("/{id:guid}/move", (Guid id, MoveRequest body, ClaimsPrincipal me, ChatService chat) =>
            chat.MoveRoom(me.Id(), id, body.Up) ? Results.NoContent() : Results.Problem("Cannot move this channel.", statusCode: 400));
    }

    private static ChannelPermission Permission(RoomRequest body) => body.AdminOnly ? ChannelPermission.AdminOnly : ChannelPermission.Everyone;
}

public sealed record RoomRequest(string Name, string? Description, bool AdminOnly, HistoryLimit HistoryLimit = HistoryLimit.OneMonth, bool SinceJoined = true);

public sealed record MoveRequest(bool Up);
