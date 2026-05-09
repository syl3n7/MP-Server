using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Domain;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

/// <summary>
/// Handles real-time UDP movement messages ("move" and "input" actions).
/// Moved from GameServer so transport stays free of game logic.
/// </summary>
public sealed class UdpMovementHandler : ICommandHandler
{
    private readonly ILogger<UdpMovementHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["move", "input"];

    public UdpMovementHandler(ILogger<UdpMovementHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Action switch
        {
            "move"  => HandleMove(envelope, session, transport, ct),
            "input" => HandleInput(envelope, session, transport, ct),
            _       => Task.CompletedTask
        };

    // ── Move ──────────────────────────────────────────────────────────────────
    private async Task HandleMove(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId)) return;

        // Envelope format: position lives under "payload"; legacy format: at root
        var posSource = e.Raw.TryGetProperty("payload", out var payload) &&
                        payload.ValueKind != JsonValueKind.Undefined
            ? payload
            : e.Raw;

        var playerInfo = new PlayerInfo(
            session.Id,
            session.PlayerName,
            session.UdpEndpoint,
            ParseVector3(posSource, "position"),
            ParseQuaternion(posSource, "rotation")
        );

        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        if (room == null) return;

        if (!room.ContainsPlayer(session.Id))
            room.TryAddPlayer(playerInfo);
        else
            room.UpdatePlayerPosition(playerInfo);

        var updateMsg = new
        {
            command   = "UPDATE",
            sessionId = playerInfo.Id,
            position  = new { x = playerInfo.Position.X, y = playerInfo.Position.Y, z = playerInfo.Position.Z },
            rotation  = new { x = playerInfo.Rotation.X, y = playerInfo.Rotation.Y, z = playerInfo.Rotation.Z, w = playerInfo.Rotation.W }
        };

        await transport.BroadcastUdpToRoomAsync(session.CurrentRoomId, updateMsg, session.Id, ct);
        _logger.LogDebug("🔄 Relayed move from {SessionId} in room {RoomId}", session.Id, session.CurrentRoomId);
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    private async Task HandleInput(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId)) return;

        // roomId may be explicit in the packet or implied by the session's current room
        var roomId = e.Raw.TryGetProperty("roomId", out var roomIdEl) &&
                     roomIdEl.ValueKind == JsonValueKind.String
            ? roomIdEl.GetString() ?? session.CurrentRoomId
            : session.CurrentRoomId;

        if (string.IsNullOrEmpty(roomId)) return;

        // Verify the room exists before broadcasting
        if (transport.GetAllRooms().All(r => r.Id != roomId)) return;

        // Broadcast the raw input packet to all other players in the room.
        // Deserialize first so JsonSerializer.Serialize in the broadcast path handles it correctly.
        var inputMsg = JsonSerializer.Deserialize<object>(e.Raw.GetRawText());
        if (inputMsg != null)
            await transport.BroadcastUdpToRoomAsync(roomId, inputMsg, session.Id, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Vector3 ParseVector3(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var el))
            return new Vector3(
                el.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
                el.TryGetProperty("y", out var y) ? y.GetSingle() : 0f,
                el.TryGetProperty("z", out var z) ? z.GetSingle() : 0f);
        return Vector3.Zero;
    }

    private static Quaternion ParseQuaternion(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var el))
            return new Quaternion(
                el.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
                el.TryGetProperty("y", out var y) ? y.GetSingle() : 0f,
                el.TryGetProperty("z", out var z) ? z.GetSingle() : 0f,
                el.TryGetProperty("w", out var w) ? w.GetSingle() : 1f);
        return Quaternion.Identity;
    }
}
