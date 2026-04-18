using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MP.Server.Inventory;

public interface IInventoryNetworkAdapter
{
    Task SendEventAsync(string sessionId, string eventType, Dictionary<string, object> payload, CancellationToken ct = default);
    Task BroadcastToRoomAsync(string roomId, string eventType, Dictionary<string, object> payload, CancellationToken ct = default);
}
