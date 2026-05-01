using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Domain;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

public sealed class RoomHandler : ICommandHandler
{
    private readonly ILogger<RoomHandler> _logger;

    public IReadOnlyList<string> Handles { get; } =
        ["CREATE_ROOM", "JOIN_ROOM", "LEAVE_ROOM", "LIST_ROOMS", "GET_ROOM_PLAYERS", "START_GAME"];

    public RoomHandler(ILogger<RoomHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Command switch
        {
            "CREATE_ROOM"      => HandleCreateRoom(envelope, session, transport, ct),
            "JOIN_ROOM"        => HandleJoinRoom(envelope, session, transport, ct),
            "LEAVE_ROOM"       => HandleLeaveRoom(session, transport, ct),
            "LIST_ROOMS"       => HandleListRooms(session, transport, ct),
            "GET_ROOM_PLAYERS" => HandleGetRoomPlayers(session, transport, ct),
            "START_GAME"       => HandleStartGame(session, transport, ct),
            _                  => Task.CompletedTask
        };

    private async Task HandleCreateRoom(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("name", out var nameEl)) return;
        var name       = nameEl.GetString() ?? "Room";
        int maxPlayers = e.Raw.TryGetProperty("maxPlayers", out var mpEl) && mpEl.TryGetInt32(out int mp) ? mp : 20;
        var room       = transport.CreateRoom(name, session.Id, maxPlayers);
        session.CurrentRoomId = room.Id;
        room.TryAddPlayer(new PlayerInfo(session.Id, session.PlayerName, null, Vector3.Zero, Quaternion.Identity));
        _logger.LogInformation("👤 Player {SessionId} ({Name}) created room '{RoomName}' ({RoomId}) maxPlayers={Max}",
            session.Id, session.PlayerName, name, room.Id, maxPlayers);
        await session.SendJsonAsync(new { command = "ROOM_CREATED", roomId = room.Id, name, maxPlayers = room.MaxPlayers }, ct);
    }

    private async Task HandleJoinRoom(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("roomId", out var roomIdEl)) return;
        var roomId = roomIdEl.GetString() ?? string.Empty;
        var room   = transport.GetAllRooms().FirstOrDefault(r => r.Id == roomId);
        if (room == null)
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Room not found." }, ct);
            return;
        }
        if (!room.TryAddPlayer(new PlayerInfo(session.Id, session.PlayerName, null, Vector3.Zero, Quaternion.Identity)))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Failed to join room. Room may be full or inactive." }, ct);
            return;
        }
        session.CurrentRoomId = roomId;
        _logger.LogInformation("👤 Player {SessionId} ({Name}) joined room {RoomId}", session.Id, session.PlayerName, roomId);
        await session.SendJsonAsync(new { command = "JOIN_OK", roomId }, ct);
    }

    private async Task HandleLeaveRoom(IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot leave room. No room joined." }, ct);
            return;
        }
        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        if (room == null)
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot leave room. Room not found." }, ct);
            session.CurrentRoomId = null;
            return;
        }
        if (!room.ContainsPlayer(session.Id))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot leave room. You are not in this room." }, ct);
            session.CurrentRoomId = null;
            return;
        }

        bool   wasHost     = room.HostId == session.Id;
        string roomName    = room.Name;
        string prevRoomId  = session.CurrentRoomId;
        room.TryRemovePlayer(session.Id);
        session.CurrentRoomId = null;

        if (wasHost && room.PlayerCount == 0 && !room.IsActive)
        {
            transport.RemoveRoom(prevRoomId);
            _logger.LogInformation("🏁 Room '{RoomName}' ({RoomId}) removed — host left, room empty", roomName, prevRoomId);
        }
        else if (wasHost && room.PlayerCount > 0)
        {
            var newHost = room.Players.FirstOrDefault();
            if (newHost != null)
            {
                room.HostId = newHost.Id;
                _logger.LogInformation("👑 Host transferred to {NewHostId} in room '{RoomName}'", newHost.Id, roomName);
            }
        }

        _logger.LogInformation("👤 Player {SessionId} ({Name}) left room '{RoomName}' ({RoomId})",
            session.Id, session.PlayerName, roomName, prevRoomId);
        await session.SendJsonAsync(new { command = "LEAVE_OK", roomId = prevRoomId }, ct);
    }

    private async Task HandleListRooms(IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        var rooms = transport.GetAllRooms().Select(r => new
        {
            id          = r.Id,
            name        = r.Name,
            playerCount = r.PlayerCount,
            maxPlayers  = r.MaxPlayers,
            isActive    = r.IsActive,
            hostId      = r.HostId
        });
        await session.SendJsonAsync(new { command = "ROOM_LIST", rooms }, ct);
        _logger.LogDebug("🏠 Sent room list to {SessionId}, {Count} rooms", session.Id, rooms.Count());
    }

    private async Task HandleGetRoomPlayers(IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot get players. No room joined." }, ct);
            return;
        }
        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        if (room == null)
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot get players. Room not found." }, ct);
            return;
        }
        var players = room.Players.Select(p => new { id = p.Id, name = p.Name }).ToList();
        await session.SendJsonAsync(new { command = "ROOM_PLAYERS", roomId = session.CurrentRoomId, players }, ct);
    }

    private async Task HandleStartGame(IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot start game. No room joined." }, ct);
            return;
        }
        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        if (room == null)
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot start game. Room not found." }, ct);
            return;
        }
        if (!room.ContainsPlayer(session.Id))
        {
            session.CurrentRoomId = null;
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot start game. You are not in this room." }, ct);
            return;
        }
        if (room.HostId != session.Id)
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Cannot start game. Only the host can start the game." }, ct);
            return;
        }

        room.StartGame();
        _logger.LogInformation("🎮 Game started in room {RoomId} by host {HostId}", session.CurrentRoomId, session.Id);

        var spawnPositions = new Dictionary<string, object>();
        foreach (var p in room.Players)
            spawnPositions[p.Id] = new { spawnIndex = room.GetPlayerSpawnIndex(p.Id) };

        var msg = new { command = "GAME_STARTED", roomId = session.CurrentRoomId, hostId = session.Id, spawnPositions };
        await transport.BroadcastToRoomAsync(session.CurrentRoomId, msg, ct);
        // Host also gets a direct send (matches original behaviour)
        await session.SendJsonAsync(msg, ct);
    }
}
