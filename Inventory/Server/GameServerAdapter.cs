using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MP.Server.Transport;

namespace MP.Server.Inventory;

internal sealed class GameServerAdapter : IInventoryNetworkAdapter
{
    private readonly GameServer _server;

    public GameServerAdapter(GameServer server) => _server = server;

    public async Task SendEventAsync(string sessionId, string eventType, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        var session = _server.GetPlayerSession(sessionId);
        if (session == null) return;

        var msg = new Dictionary<string, object>(payload) { ["type"] = eventType };
        await session.SendJsonAsync(msg, ct);
    }

    public async Task BroadcastToRoomAsync(string roomId, string eventType, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        var msg = new Dictionary<string, object>(payload) { ["type"] = eventType };
        await _server.BroadcastToRoomAsync(roomId, msg, ct);
    }
}
