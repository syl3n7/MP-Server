using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

/// <summary>
/// Handles core combat actions: "player_kill" and "respawn".
/// Both use the envelope-style lowercase action field.
/// </summary>
public sealed class CombatHandler : ICommandHandler
{
    private readonly ILogger<CombatHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["player_kill", "respawn"];

    public CombatHandler(ILogger<CombatHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Action switch
        {
            "player_kill" => HandlePlayerKill(envelope, session, transport, ct),
            "respawn"     => HandleRespawn(session, transport, ct),
            _             => Task.CompletedTask
        };

    // ── player_kill ───────────────────────────────────────────────────────────

    private async Task HandlePlayerKill(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "You must be in a room to report a kill." }, ct);
            return;
        }

        if (!e.Raw.TryGetProperty("targetId", out var targetEl) || string.IsNullOrEmpty(targetEl.GetString()))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "targetId is required." }, ct);
            return;
        }

        var targetId = targetEl.GetString()!;

        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        if (room == null) return;

        if (!room.ContainsPlayer(targetId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Target not found in your room." }, ct);
            return;
        }

        if (room.IsPlayerDead(targetId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Target is already dead." }, ct);
            return;
        }

        room.MarkPlayerDead(targetId);

        var victim        = transport.GetPlayerSession(targetId);
        var victimName    = victim?.PlayerName ?? targetId;

        _logger.LogInformation("💀 {KillerName} ({KillerId}) killed {VictimName} ({VictimId}) in room {RoomId}",
            session.PlayerName, session.Id, victimName, targetId, session.CurrentRoomId);

        await transport.BroadcastToRoomAsync(session.CurrentRoomId, new
        {
            command     = "PLAYER_KILLED",
            killerId    = session.Id,
            killerName  = session.PlayerName,
            victimId    = targetId,
            victimName
        }, ct);
    }

    // ── respawn ───────────────────────────────────────────────────────────────

    private async Task HandleRespawn(IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "You must be in a room to respawn." }, ct);
            return;
        }

        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        if (room == null) return;

        if (!room.IsPlayerDead(session.Id))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "You are not dead." }, ct);
            return;
        }

        room.RevivePlayer(session.Id);

        var spawnIndex = room.GetPlayerSpawnIndex(session.Id);

        _logger.LogInformation("♻️  {PlayerName} ({SessionId}) respawned at slot {SpawnIndex} in room {RoomId}",
            session.PlayerName, session.Id, spawnIndex, session.CurrentRoomId);

        // Tell the respawning player their spawn point
        await session.SendJsonAsync(new { command = "RESPAWN_OK", spawnIndex }, ct);

        // Tell everyone else in the room
        await transport.BroadcastToRoomAsync(session.CurrentRoomId, new
        {
            command    = "PLAYER_RESPAWNED",
            sessionId  = session.Id,
            playerName = session.PlayerName,
            spawnIndex
        }, ct);
    }
}
