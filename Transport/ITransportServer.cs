using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MP.Server.Domain;

namespace MP.Server.Transport;

/// <summary>
/// Abstracts the transport layer for use by Protocol handlers.
/// Protocol/ depends on this interface; Transport/ implements it.
/// </summary>
public interface ITransportServer
{
    // ── Session access ────────────────────────────────────────────────────────
    PlayerSession? GetPlayerSession(string sessionId);
    IReadOnlyCollection<PlayerSession> GetAllSessions();

    // ── Room management ───────────────────────────────────────────────────────
    GameRoom CreateRoom(string name, string hostId, int maxPlayers = 20);
    bool RemoveRoom(string roomId);
    IReadOnlyCollection<GameRoom> GetAllRooms();
    IReadOnlyCollection<GameRoom> GetActiveRooms();

    // ── Broadcast ─────────────────────────────────────────────────────────────
    Task BroadcastToRoomAsync<T>(string roomId, T message, CancellationToken ct = default);
    Task BroadcastChatMessageAsync(string roomId, string senderName, string message, string senderId);
}
