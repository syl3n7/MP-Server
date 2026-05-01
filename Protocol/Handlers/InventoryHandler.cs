using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Inventory;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

public sealed class InventoryHandler : ICommandHandler
{
    private readonly ILogger<InventoryHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["INV_MOVE_SLOT", "INV_DROP_ITEM", "INV_USE_ITEM", "INV_REQUEST_SYNC"];

    public InventoryHandler(ILogger<InventoryHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Command switch
        {
            "INV_MOVE_SLOT"    => HandleMoveSlot(envelope, session, ct),
            "INV_DROP_ITEM"    => HandleDropItem(envelope, session, transport, ct),
            "INV_USE_ITEM"     => HandleUseItem(envelope, session, ct),
            "INV_REQUEST_SYNC" => InventoryManager.Instance.HandleSyncRequest(session.Id, ct),
            _                  => Task.CompletedTask
        };

    private async Task HandleMoveSlot(MessageEnvelope e, IPlayerSession session, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("fromSlot", out var fromEl) || !fromEl.TryGetInt32(out int from) ||
            !e.Raw.TryGetProperty("toSlot",   out var toEl)   || !toEl.TryGetInt32(out int to))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "fromSlot and toSlot (int) are required." }, ct);
            return;
        }
        await InventoryManager.Instance.HandleMoveSlot(session.Id, from, to, ct);
    }

    private async Task HandleDropItem(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("slotId", out var slotEl) || !slotEl.TryGetInt32(out int slot))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "slotId (int) is required." }, ct);
            return;
        }
        int qty = e.Raw.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out int q) ? q : 1;
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Must be in a room to drop items." }, ct);
            return;
        }
        var room = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        var pos  = room?.Players.FirstOrDefault(p => p.Id == session.Id)?.Position ?? Vector3.Zero;
        await InventoryManager.Instance.HandleDropItem(session.Id, session.CurrentRoomId, slot, qty, pos, ct);
    }

    private async Task HandleUseItem(MessageEnvelope e, IPlayerSession session, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("slotId", out var slotEl) || !slotEl.TryGetInt32(out int slot))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "slotId (int) is required." }, ct);
            return;
        }
        await InventoryManager.Instance.HandleUseItem(session.Id, slot, ct);
    }
}
