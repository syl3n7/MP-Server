using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MP.Server.Inventory;

public sealed class InventoryManager
{
    public static InventoryManager Instance { get; } = new();

    private readonly ConcurrentDictionary<string, Inventory> _inventories = new();
    private readonly CommandValidator _validator = new();
    private IInventoryNetworkAdapter _adapter = null!;
    private ILogger? _logger;

    public void Initialise(IInventoryNetworkAdapter adapter, ILogger? logger = null)
    {
        _adapter = adapter;
        _logger  = logger;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>Call immediately after a player successfully authenticates.</summary>
    public async Task OnPlayerJoined(string sessionId, CancellationToken ct = default)
    {
        _inventories[sessionId] = new Inventory(sessionId);
        _logger?.LogInformation("🎒 Inventory created for session {SessionId}", sessionId);
        await SyncFull(sessionId, ct);
    }

    /// <summary>Call when a player disconnects.</summary>
    public void OnPlayerLeft(string sessionId)
    {
        _inventories.TryRemove(sessionId, out _);
        _logger?.LogDebug("🎒 Inventory removed for session {SessionId}", sessionId);
    }

    // ── Command handlers ───────────────────────────────────────────────────────

    public async Task HandleMoveSlot(string sessionId, int fromSlot, int toSlot, CancellationToken ct = default)
    {
        if (!_inventories.TryGetValue(sessionId, out var inv))
        { await SendError(sessionId, "NOT_OWNER", -1, "No inventory found", ct); return; }

        var result = _validator.ValidateMoveSlot(inv, sessionId, fromSlot, toSlot);
        if (!result.Ok) { await SendError(sessionId, result.ErrorCode, result.SlotId, "", ct); return; }

        var src  = inv.GetSlot(fromSlot)!;
        var dest = inv.GetSlot(toSlot)!;

        // Merge stacks when same item and destination has room
        if (!dest.IsEmpty && dest.ItemId == src.ItemId)
        {
            var def      = ItemRegistry.Instance.Get(src.ItemId);
            var maxStack = def?.MaxStack ?? 99;
            var space    = maxStack - dest.Quantity;
            var transfer = Math.Min(src.Quantity, space);

            dest.Quantity += transfer;
            src.Quantity  -= transfer;

            if (src.Quantity <= 0)
            {
                src.ItemId   = "";
                src.Quantity = 0;
                src.Metadata = new();
                await SendSlotCleared(sessionId, fromSlot, ct);
            }
            else
            {
                await SendSlotUpdate(sessionId, src, ct);
            }
            await SendSlotUpdate(sessionId, dest, ct);
        }
        else
        {
            // Pure swap
            (src.ItemId,   dest.ItemId)   = (dest.ItemId,   src.ItemId);
            (src.Quantity, dest.Quantity) = (dest.Quantity, src.Quantity);
            (src.Metadata, dest.Metadata) = (dest.Metadata, src.Metadata);

            if (src.IsEmpty)
                await SendSlotCleared(sessionId, fromSlot, ct);
            else
                await SendSlotUpdate(sessionId, src, ct);

            await SendSlotUpdate(sessionId, dest, ct);
        }
    }

    public async Task HandleDropItem(string sessionId, string roomId, int slotId, int quantity, Vector3 playerPosition, CancellationToken ct = default)
    {
        if (!_inventories.TryGetValue(sessionId, out var inv))
        { await SendError(sessionId, "NOT_OWNER", -1, "No inventory found", ct); return; }

        var result = _validator.ValidateDropItem(inv, sessionId, slotId);
        if (!result.Ok) { await SendError(sessionId, result.ErrorCode, result.SlotId, "", ct); return; }

        var slot          = inv.GetSlot(slotId)!;
        var droppedItemId = slot.ItemId;
        var actualQty     = Math.Min(quantity, slot.Quantity);

        slot.Quantity -= actualQty;

        if (slot.Quantity <= 0)
        {
            slot.ItemId   = "";
            slot.Quantity = 0;
            slot.Metadata = new();
            await SendSlotCleared(sessionId, slotId, ct);
        }
        else
        {
            await SendSlotUpdate(sessionId, slot, ct);
        }

        // Broadcast floor item spawn to everyone in the room
        var floorItemId = Guid.NewGuid().ToString("N");
        await _adapter.BroadcastToRoomAsync(roomId, "FLOOR_ITEM_SPAWN", new Dictionary<string, object>
        {
            ["id"]       = floorItemId,
            ["itemType"] = droppedItemId,
            ["quantity"] = actualQty,
            ["position"] = new Dictionary<string, object>
            {
                ["x"] = playerPosition.X,
                ["y"] = playerPosition.Y,
                ["z"] = playerPosition.Z
            }
        }, ct);

        _logger?.LogInformation("📦 {SessionId} dropped {Qty}x {Item} → floor item {FloorId}", sessionId, actualQty, droppedItemId, floorItemId);
    }

    public async Task HandleUseItem(string sessionId, int slotId, CancellationToken ct = default)
    {
        if (!_inventories.TryGetValue(sessionId, out var inv))
        { await SendError(sessionId, "NOT_OWNER", -1, "No inventory found", ct); return; }

        var result = _validator.ValidateUseItem(inv, sessionId, slotId);
        if (!result.Ok) { await SendError(sessionId, result.ErrorCode, result.SlotId, "", ct); return; }

        var slot = inv.GetSlot(slotId)!;
        slot.Quantity -= 1;

        if (slot.Quantity <= 0)
        {
            slot.ItemId   = "";
            slot.Quantity = 0;
            slot.Metadata = new();
            await SendSlotCleared(sessionId, slotId, ct);
        }
        else
        {
            await SendSlotUpdate(sessionId, slot, ct);
        }
    }

    public async Task HandleSyncRequest(string sessionId, CancellationToken ct = default)
    {
        if (_inventories.TryGetValue(sessionId, out _))
            await SyncFull(sessionId, ct);
        else
            await SendError(sessionId, "NOT_OWNER", -1, "No inventory found", ct);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private Task SyncFull(string sessionId, CancellationToken ct) =>
        _adapter.SendEventAsync(sessionId, "INV_STATE_FULL", new Dictionary<string, object>
        {
            ["slots"] = _inventories[sessionId].Serialise()
        }, ct);

    private Task SendSlotUpdate(string sessionId, InventorySlot slot, CancellationToken ct) =>
        _adapter.SendEventAsync(sessionId, "INV_SLOT_UPDATED", slot.ToDict(), ct);

    private Task SendSlotCleared(string sessionId, int slotId, CancellationToken ct) =>
        _adapter.SendEventAsync(sessionId, "INV_SLOT_CLEARED", new Dictionary<string, object>
        {
            ["slotId"] = slotId
        }, ct);

    private Task SendError(string sessionId, string code, int slotId, string message, CancellationToken ct) =>
        _adapter.SendEventAsync(sessionId, "INV_ERROR", new Dictionary<string, object>
        {
            ["code"]    = code,
            ["slotId"]  = slotId,
            ["message"] = message
        }, ct);
}
