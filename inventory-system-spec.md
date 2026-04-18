# Inventory System — Implementation Spec
> Godot 4 · C# · Compatible with ENet and Custom TCP/UDP transport

---

## 0. Context & constraints

Your codebase already has two network paths that must both work:

| Path | Transport | How messages move |
|---|---|---|
| **ENet** | Godot `MultiplayerPeer` | `[Rpc]` attributes, `RpcId()`, `Rpc()` |
| **Custom** | TCP (TLS, newline-delimited JSON) + UDP (AES-256-CBC) | `Client.cs` / `CustomNetworkClient.cs` |

The inventory system **must not hard-code either path**. All game logic goes through an `INetworkAdapter` interface. The rest of this document tells you exactly what to build and in what order.

---

## 1. Data structures

These are pure C# classes — no Godot, no network code. Put them in `Scripts/Inventory/Data/`.

### InventorySlot.cs

```csharp
public class InventorySlot
{
    public int SlotId        { get; set; }
    public string ItemId     { get; set; } = "";   // "" = empty slot
    public int Quantity      { get; set; } = 0;
    public Dictionary<string, object> Metadata { get; set; } = new();

    public bool IsEmpty => string.IsNullOrEmpty(ItemId) || Quantity <= 0;

    // Minimal serialisation for network transport
    public Dictionary<string, object> ToDict() => new()
    {
        ["slotId"]   = SlotId,
        ["itemId"]   = ItemId,
        ["quantity"] = Quantity,
        ["meta"]     = Metadata
    };

    public static InventorySlot FromDict(Dictionary<string, object> d) => new()
    {
        SlotId   = Convert.ToInt32(d["slotId"]),
        ItemId   = d["itemId"].ToString(),
        Quantity = Convert.ToInt32(d["quantity"]),
        Metadata = d.ContainsKey("meta")
                   ? (Dictionary<string, object>)d["meta"]
                   : new()
    };
}
```

### Inventory.cs

```csharp
public class Inventory
{
    public string OwnerId   { get; set; }   // sessionId of owning player
    public int MaxSlots     { get; set; } = 30;
    public InventorySlot[] Slots { get; set; }

    public Inventory(string ownerId, int maxSlots = 30)
    {
        OwnerId  = ownerId;
        MaxSlots = maxSlots;
        Slots    = Enumerable.Range(0, maxSlots)
                             .Select(i => new InventorySlot { SlotId = i })
                             .ToArray();
    }

    public InventorySlot? GetSlot(int slotId) =>
        slotId >= 0 && slotId < Slots.Length ? Slots[slotId] : null;

    public int FirstEmptySlot() =>
        Array.FindIndex(Slots, s => s.IsEmpty);

    // Full snapshot for initial sync (INV_STATE_FULL)
    public List<Dictionary<string, object>> Serialise() =>
        Slots.Select(s => s.ToDict()).ToList();
}
```

### ItemDefinition.cs

```csharp
// Static — loaded from JSON/resource at startup, never sent over the wire
public class ItemDefinition
{
    public string ItemId      { get; set; }
    public string DisplayName { get; set; }
    public int    MaxStack    { get; set; } = 99;
    public float  Weight      { get; set; } = 1f;
    public bool   IsDroppable { get; set; } = true;
    public bool   IsUsable    { get; set; } = false;
    public Dictionary<string, object> Tags { get; set; } = new();
}
```

---

## 2. Network message protocol

Inventory messages travel over **TCP** (not UDP). They are not per-frame data — they are discrete state changes. Use your existing newline-delimited JSON format.

### New client → server commands

Append these to your existing command table in `Server.cs`:

| Command | Fields | Meaning |
|---|---|---|
| `INV_MOVE_SLOT` | `sessionId`, `fromSlot` (int), `toSlot` (int) | Move/swap two slots |
| `INV_USE_ITEM` | `sessionId`, `slotId` (int) | Activate item in slot |
| `INV_DROP_ITEM` | `sessionId`, `slotId` (int), `quantity` (int) | Drop quantity from slot |
| `INV_REQUEST_SYNC` | `sessionId` | Client requests full state resync |

### New server → client events

Append these to your existing event table in `Client.cs`:

| Message | Fields | Triggers |
|---|---|---|
| `INV_STATE_FULL` | `slots` (array of slot dicts) | Full inventory replace — sent on join and on request |
| `INV_SLOT_UPDATED` | `slotId` (int), `itemId` (string), `quantity` (int), `meta` (dict) | Single slot change |
| `INV_SLOT_CLEARED` | `slotId` (int) | Slot was emptied |
| `INV_ERROR` | `code` (string), `slotId` (int), `message` (string) | Command rejected — client must roll back |

### Error codes (INV_ERROR.code)

| Code | Meaning |
|---|---|
| `INVALID_SLOT` | Slot index out of range |
| `NOT_OWNER` | Peer does not own this inventory |
| `SLOT_EMPTY` | No item in that slot |
| `STACK_FULL` | Destination slot cannot accept more |
| `ITEM_NOT_DROPPABLE` | Item definition forbids dropping |
| `ITEM_NOT_USABLE` | Item definition forbids use |
| `COOLDOWN` | Action rate-limited |

### Floor item events (currently ENet-only — see §8)

`SpawnFloorItemRpc` currently only fires over Godot RPCs. For the custom path you need two new TCP messages:

| Message | Fields | Replaces |
|---|---|---|
| `FLOOR_ITEM_SPAWN` | `id`, `position {x,y,z}`, `itemType` | `SpawnFloorItemRpc` |
| `FLOOR_ITEM_DESPAWN` | `id` | (currently missing entirely) |

---

## 3. INetworkAdapter interface

Place in `Scripts/Network/INetworkAdapter.cs`. This is the only thing inventory code is allowed to call.

```csharp
public interface INetworkAdapter
{
    // --- Client side ---
    // Send an inventory command to the server
    void SendInventoryCommand(string command, Dictionary<string, object> payload);

    // Subscribe to inventory events arriving from server
    event Action<string, Dictionary<string, object>> OnInventoryEvent;

    // --- Server side ---
    // Push an event to a specific peer (by sessionId)
    void SendInventoryEvent(string sessionId, string eventType, Dictionary<string, object> payload);

    // Push an event to every peer in a room
    void BroadcastInventoryEvent(string roomId, string eventType, Dictionary<string, object> payload);
}
```

---

## 4. ENetAdapter

`Scripts/Network/ENetAdapter.cs` — wraps Godot `Rpc` calls.

```csharp
public partial class ENetAdapter : Node, INetworkAdapter
{
    public event Action<string, Dictionary<string, object>> OnInventoryEvent;

    // Client side: pack command into an RPC call to the server node
    public void SendInventoryCommand(string command, Dictionary<string, object> payload)
    {
        payload["command"] = command;
        var json = Json.Stringify(payload);
        // Call server-side RpcInventoryCommand on the authoritative node
        RpcId(1, nameof(RpcInventoryCommand), json);
    }

    // Server side: called by clients via RPC
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void RpcInventoryCommand(string json)
    {
        var payload = Json.ParseString(json).AsGodotDictionary();
        var senderId = Multiplayer.GetRemoteSenderId().ToString();
        payload["sessionId"] = senderId;
        // Forward to InventoryManager for processing
        InventoryManager.Instance.HandleCommand(payload["command"].ToString(), payload);
    }

    // Server side: send event to one peer
    public void SendInventoryEvent(string sessionId, string eventType, Dictionary<string, object> payload)
    {
        payload["type"] = eventType;
        var json = Json.Stringify(payload);
        RpcId(long.Parse(sessionId), nameof(RpcInventoryEvent), json);
    }

    // Server side: broadcast to room (ENet has no rooms natively — iterate peers)
    public void BroadcastInventoryEvent(string roomId, string eventType, Dictionary<string, object> payload)
    {
        foreach (var sessionId in RoomManager.Instance.GetPeersInRoom(roomId))
            SendInventoryEvent(sessionId, eventType, payload);
    }

    // Client side: receive event from server
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false)]
    private void RpcInventoryEvent(string json)
    {
        var payload = Json.ParseString(json).AsGodotDictionary();
        var eventType = payload["type"].ToString();
        var dict = GodotDictToCSharp(payload);
        OnInventoryEvent?.Invoke(eventType, dict);
    }
}
```

---

## 5. CustomAdapter

`Scripts/Network/CustomAdapter.cs` — hooks into your existing `Client.cs` TCP pipeline.

```csharp
public class CustomAdapter : INetworkAdapter
{
    private readonly Client _client;   // your existing Client singleton

    public event Action<string, Dictionary<string, object>> OnInventoryEvent;

    public CustomAdapter(Client client)
    {
        _client = client;
        // Wire into Client's existing message dispatch
        _client.OnRawMessageReceived += HandleRawMessage;
    }

    // CLIENT SIDE: wrap command in the existing TCP JSON format
    public void SendInventoryCommand(string command, Dictionary<string, object> payload)
    {
        payload["command"] = command;
        _client.SendTcpMessage(payload);   // uses your existing SendTcpMessage()
    }

    // CLIENT SIDE: receive — called by Client.cs for every incoming TCP message
    private void HandleRawMessage(Dictionary<string, object> msg)
    {
        var type = msg.GetValueOrDefault("type", "")?.ToString() ?? "";

        // Only handle INV_* messages here — pass everything else through
        if (!type.StartsWith("INV_") && !type.StartsWith("FLOOR_ITEM_"))
            return;

        OnInventoryEvent?.Invoke(type, msg);
    }

    // SERVER SIDE: send event to one peer via your server's TCP writer
    public void SendInventoryEvent(string sessionId, string eventType, Dictionary<string, object> payload)
    {
        payload["type"] = eventType;
        ServerTcpWriter.SendTo(sessionId, payload);   // your server's TCP send utility
    }

    // SERVER SIDE: broadcast to room
    public void BroadcastInventoryEvent(string roomId, string eventType, Dictionary<string, object> payload)
    {
        payload["type"] = eventType;
        ServerTcpWriter.BroadcastToRoom(roomId, payload);
    }
}
```

> **Important:** Add `OnRawMessageReceived` as a raw-passthrough event in `Client.cs` if it doesn't already exist. It should fire for every parsed JSON message before the switch-case that handles known types — that way `CustomAdapter` can intercept inventory messages without modifying `Client.cs`'s own dispatch logic.

---

## 6. Server-side components

### ItemRegistry.cs — `Scripts/Inventory/Server/ItemRegistry.cs`

```csharp
public class ItemRegistry
{
    public static ItemRegistry Instance { get; } = new();

    private readonly Dictionary<string, ItemDefinition> _items = new();

    // Call this at server startup — load from items.json
    public void LoadFromJson(string path)
    {
        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<ItemDefinition>>(json);
        foreach (var item in list)
            _items[item.ItemId] = item;
    }

    public ItemDefinition? Get(string itemId) =>
        _items.TryGetValue(itemId, out var def) ? def : null;

    public bool Exists(string itemId) => _items.ContainsKey(itemId);
}
```

### CommandValidator.cs — `Scripts/Inventory/Server/CommandValidator.cs`

```csharp
public class CommandValidator
{
    public record ValidationResult(bool Ok, string ErrorCode = "", int SlotId = -1);

    public ValidationResult ValidateMoveSlot(Inventory inv, string sessionId, int from, int to)
    {
        if (inv.OwnerId != sessionId)    return new(false, "NOT_OWNER");
        if (inv.GetSlot(from) == null)   return new(false, "INVALID_SLOT", from);
        if (inv.GetSlot(to)   == null)   return new(false, "INVALID_SLOT", to);
        if (inv.GetSlot(from)!.IsEmpty)  return new(false, "SLOT_EMPTY",   from);

        var dest = inv.GetSlot(to)!;
        var src  = inv.GetSlot(from)!;

        // Stacking check: same item, not full
        if (!dest.IsEmpty && dest.ItemId == src.ItemId)
        {
            var def = ItemRegistry.Instance.Get(src.ItemId);
            if (def != null && dest.Quantity >= def.MaxStack)
                return new(false, "STACK_FULL", to);
        }

        return new(true);
    }

    public ValidationResult ValidateDropItem(Inventory inv, string sessionId, int slotId, int quantity)
    {
        if (inv.OwnerId != sessionId)   return new(false, "NOT_OWNER");
        var slot = inv.GetSlot(slotId);
        if (slot == null)               return new(false, "INVALID_SLOT", slotId);
        if (slot.IsEmpty)               return new(false, "SLOT_EMPTY",   slotId);

        var def = ItemRegistry.Instance.Get(slot.ItemId);
        if (def != null && !def.IsDroppable) return new(false, "ITEM_NOT_DROPPABLE", slotId);

        return new(true);
    }

    public ValidationResult ValidateUseItem(Inventory inv, string sessionId, int slotId)
    {
        if (inv.OwnerId != sessionId)  return new(false, "NOT_OWNER");
        var slot = inv.GetSlot(slotId);
        if (slot == null)              return new(false, "INVALID_SLOT", slotId);
        if (slot.IsEmpty)              return new(false, "SLOT_EMPTY",   slotId);

        var def = ItemRegistry.Instance.Get(slot.ItemId);
        if (def != null && !def.IsUsable) return new(false, "ITEM_NOT_USABLE", slotId);

        return new(true);
    }
}
```

### InventoryManager.cs — `Scripts/Inventory/Server/InventoryManager.cs`

```csharp
public class InventoryManager
{
    public static InventoryManager Instance { get; } = new();

    private readonly Dictionary<string, Inventory> _inventories = new();
    private readonly CommandValidator _validator = new();
    private INetworkAdapter _adapter = null!;

    public void Initialise(INetworkAdapter adapter) => _adapter = adapter;

    // Called when a player successfully authenticates
    public void OnPlayerJoined(string sessionId)
    {
        _inventories[sessionId] = new Inventory(sessionId);
        SyncFull(sessionId);
    }

    // Called when a player disconnects
    public void OnPlayerLeft(string sessionId) =>
        _inventories.Remove(sessionId);

    public void HandleCommand(string command, Dictionary<string, object> payload)
    {
        var sessionId = payload["sessionId"].ToString()!;

        if (!_inventories.TryGetValue(sessionId, out var inv))
        {
            SendError(sessionId, "NOT_OWNER", -1, "No inventory found");
            return;
        }

        switch (command)
        {
            case "INV_MOVE_SLOT":
                HandleMoveSlot(inv, sessionId, payload);
                break;
            case "INV_USE_ITEM":
                HandleUseItem(inv, sessionId, payload);
                break;
            case "INV_DROP_ITEM":
                HandleDropItem(inv, sessionId, payload);
                break;
            case "INV_REQUEST_SYNC":
                SyncFull(sessionId);
                break;
        }
    }

    private void HandleMoveSlot(Inventory inv, string sessionId, Dictionary<string, object> p)
    {
        var from = Convert.ToInt32(p["fromSlot"]);
        var to   = Convert.ToInt32(p["toSlot"]);

        var result = _validator.ValidateMoveSlot(inv, sessionId, from, to);
        if (!result.Ok) { SendError(sessionId, result.ErrorCode, result.SlotId); return; }

        var src  = inv.GetSlot(from)!;
        var dest = inv.GetSlot(to)!;

        // Simple swap
        (src.ItemId, dest.ItemId) = (dest.ItemId, src.ItemId);
        (src.Quantity, dest.Quantity) = (dest.Quantity, src.Quantity);
        (src.Metadata, dest.Metadata) = (dest.Metadata, src.Metadata);

        // Notify client of both changed slots
        SendSlotUpdate(sessionId, src);
        SendSlotUpdate(sessionId, dest);
    }

    private void HandleDropItem(Inventory inv, string sessionId, Dictionary<string, object> p)
    {
        var slotId   = Convert.ToInt32(p["slotId"]);
        var quantity = Convert.ToInt32(p["quantity"]);

        var result = _validator.ValidateDropItem(inv, sessionId, slotId, quantity);
        if (!result.Ok) { SendError(sessionId, result.ErrorCode, result.SlotId); return; }

        var slot = inv.GetSlot(slotId)!;
        slot.Quantity -= quantity;

        if (slot.Quantity <= 0)
        {
            slot.ItemId   = "";
            slot.Quantity = 0;
            slot.Metadata = new();
            SendSlotCleared(sessionId, slotId);
        }
        else
        {
            SendSlotUpdate(sessionId, slot);
        }

        // TODO: spawn floor item in world via your room/world manager
        // WorldManager.Instance.SpawnFloorItem(sessionId, slot.ItemId, quantity);
    }

    private void HandleUseItem(Inventory inv, string sessionId, Dictionary<string, object> p)
    {
        var slotId = Convert.ToInt32(p["slotId"]);
        var result = _validator.ValidateUseItem(inv, sessionId, slotId);
        if (!result.Ok) { SendError(sessionId, result.ErrorCode, result.SlotId); return; }

        // TODO: delegate to your item effect system
        // ItemEffectHandler.Instance.Apply(inv.GetSlot(slotId)!.ItemId, sessionId);
    }

    // --- Helpers ---

    private void SyncFull(string sessionId) =>
        _adapter.SendInventoryEvent(sessionId, "INV_STATE_FULL", new()
        {
            ["slots"] = _inventories[sessionId].Serialise()
        });

    private void SendSlotUpdate(string sessionId, InventorySlot slot) =>
        _adapter.SendInventoryEvent(sessionId, "INV_SLOT_UPDATED", slot.ToDict());

    private void SendSlotCleared(string sessionId, int slotId) =>
        _adapter.SendInventoryEvent(sessionId, "INV_SLOT_CLEARED", new()
        {
            ["slotId"] = slotId
        });

    private void SendError(string sessionId, string code, int slotId, string message = "") =>
        _adapter.SendInventoryEvent(sessionId, "INV_ERROR", new()
        {
            ["code"]    = code,
            ["slotId"]  = slotId,
            ["message"] = message
        });
}
```

---

## 7. Client-side components

### InventoryClient.cs — `Scripts/Inventory/Client/InventoryClient.cs`

This is the component you asked to deep-dive first. Full implementation:

```csharp
public partial class InventoryClient : Node
{
    // ── Signals ──────────────────────────────────────────────────────────────
    [Signal] public delegate void SlotChangedEventHandler(int slotId);
    [Signal] public delegate void InventorySyncedEventHandler();         // full replace
    [Signal] public delegate void InventoryErrorEventHandler(string code, int slotId);

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly Dictionary<int, InventorySlot> _slots = new();

    // Optimistic update rollback: command key → snapshot of affected slots before the change
    private readonly Dictionary<string, List<InventorySlot>> _pendingRollbacks = new();

    private INetworkAdapter _adapter = null!;

    // ── Initialisation ────────────────────────────────────────────────────────
    public void Initialise(INetworkAdapter adapter)
    {
        _adapter = adapter;
        _adapter.OnInventoryEvent += HandleServerEvent;
    }

    // ── Queries ───────────────────────────────────────────────────────────────
    public InventorySlot? GetSlot(int slotId) =>
        _slots.TryGetValue(slotId, out var s) ? s : null;

    public IEnumerable<InventorySlot> AllSlots() => _slots.Values;

    // ── Commands (optimistic) ─────────────────────────────────────────────────

    public void MoveSlot(int from, int to)
    {
        // 1. Validate locally (cheap, non-authoritative)
        if (!_slots.TryGetValue(from, out var src) || src.IsEmpty) return;

        // 2. Snapshot for rollback
        var key = $"MOVE_{from}_{to}";
        SaveRollback(key, from, to);

        // 3. Apply optimistically
        if (_slots.TryGetValue(to, out var dest))
        {
            (_slots[from], _slots[to]) = (CloneSlot(dest, from), CloneSlot(src, to));
        }
        else
        {
            _slots[to]   = CloneSlot(src, to);
            _slots[from] = new InventorySlot { SlotId = from };
        }
        EmitSignal(SignalName.SlotChanged, from);
        EmitSignal(SignalName.SlotChanged, to);

        // 4. Send to server — server is authoritative
        _adapter.SendInventoryCommand("INV_MOVE_SLOT", new()
        {
            ["fromSlot"] = from,
            ["toSlot"]   = to
        });
    }

    public void DropItem(int slotId, int quantity = 1)
    {
        if (!_slots.TryGetValue(slotId, out var slot) || slot.IsEmpty) return;

        var key = $"DROP_{slotId}";
        SaveRollback(key, slotId);

        // Optimistically reduce quantity
        var clone = CloneSlot(slot, slotId);
        clone.Quantity -= quantity;
        if (clone.Quantity <= 0) clone = new InventorySlot { SlotId = slotId };
        _slots[slotId] = clone;
        EmitSignal(SignalName.SlotChanged, slotId);

        _adapter.SendInventoryCommand("INV_DROP_ITEM", new()
        {
            ["slotId"]   = slotId,
            ["quantity"] = quantity
        });
    }

    public void UseItem(int slotId)
    {
        if (!_slots.TryGetValue(slotId, out var slot) || slot.IsEmpty) return;

        // UseItem has no optimistic local state change — effect is server-defined
        _adapter.SendInventoryCommand("INV_USE_ITEM", new()
        {
            ["slotId"] = slotId
        });
    }

    public void RequestFullSync() =>
        _adapter.SendInventoryCommand("INV_REQUEST_SYNC", new());

    // ── Server event handler ──────────────────────────────────────────────────

    private void HandleServerEvent(string type, Dictionary<string, object> payload)
    {
        switch (type)
        {
            case "INV_STATE_FULL":
                ApplyFullState(payload);
                break;
            case "INV_SLOT_UPDATED":
                ApplySlotUpdate(payload);
                break;
            case "INV_SLOT_CLEARED":
                ApplySlotCleared(payload);
                break;
            case "INV_ERROR":
                ApplyError(payload);
                break;
        }
    }

    private void ApplyFullState(Dictionary<string, object> payload)
    {
        _slots.Clear();
        _pendingRollbacks.Clear();

        var slots = (List<object>)payload["slots"];
        foreach (Dictionary<string, object> s in slots)
        {
            var slot = InventorySlot.FromDict(s);
            _slots[slot.SlotId] = slot;
        }

        EmitSignal(SignalName.InventorySynced);
    }

    private void ApplySlotUpdate(Dictionary<string, object> payload)
    {
        var slot = InventorySlot.FromDict(payload);
        _slots[slot.SlotId] = slot;

        // Confirm matching rollback if one exists — no longer needed
        DropRollbackForSlot(slot.SlotId);

        EmitSignal(SignalName.SlotChanged, slot.SlotId);
    }

    private void ApplySlotCleared(Dictionary<string, object> payload)
    {
        var slotId = Convert.ToInt32(payload["slotId"]);
        _slots[slotId] = new InventorySlot { SlotId = slotId };

        DropRollbackForSlot(slotId);

        EmitSignal(SignalName.SlotChanged, slotId);
    }

    private void ApplyError(Dictionary<string, object> payload)
    {
        var code   = payload["code"].ToString()!;
        var slotId = Convert.ToInt32(payload["slotId"]);

        // Roll back all pending snapshots that involve this slot
        RollbackSlot(slotId);

        EmitSignal(SignalName.InventoryError, code, slotId);
    }

    // ── Rollback helpers ──────────────────────────────────────────────────────

    private void SaveRollback(string key, params int[] slotIds)
    {
        var snapshots = slotIds
            .Where(id => _slots.ContainsKey(id))
            .Select(id => CloneSlot(_slots[id], id))
            .ToList();
        _pendingRollbacks[key] = snapshots;
    }

    private void RollbackSlot(int slotId)
    {
        // Find any pending rollback that includes this slot and restore it
        var keys = _pendingRollbacks.Keys
            .Where(k => _pendingRollbacks[k].Any(s => s.SlotId == slotId))
            .ToList();

        foreach (var key in keys)
        {
            foreach (var snapshot in _pendingRollbacks[key])
            {
                _slots[snapshot.SlotId] = snapshot;
                EmitSignal(SignalName.SlotChanged, snapshot.SlotId);
            }
            _pendingRollbacks.Remove(key);
        }
    }

    private void DropRollbackForSlot(int slotId)
    {
        var keys = _pendingRollbacks.Keys
            .Where(k => _pendingRollbacks[k].Any(s => s.SlotId == slotId))
            .ToList();
        foreach (var key in keys)
            _pendingRollbacks.Remove(key);
    }

    private static InventorySlot CloneSlot(InventorySlot s, int newSlotId) => new()
    {
        SlotId   = newSlotId,
        ItemId   = s.ItemId,
        Quantity = s.Quantity,
        Metadata = new Dictionary<string, object>(s.Metadata)
    };
}
```

### InventoryUI (scene wiring only)

`Scripts/Inventory/Client/InventoryUI.cs` — **zero network code here**.

```csharp
public partial class InventoryUI : Control
{
    [Export] private GridContainer _grid = null!;
    [Export] private PackedScene   _slotScene = null!;

    private InventoryClient _client = null!;
    private readonly Dictionary<int, InventorySlotUI> _slotNodes = new();

    public override void _Ready()
    {
        _client = GetNode<InventoryClient>("/root/InventoryClient");
        _client.SlotChanged       += OnSlotChanged;
        _client.InventorySynced   += RebuildAll;
        _client.InventoryError    += OnInventoryError;
    }

    private void RebuildAll()
    {
        foreach (var child in _grid.GetChildren()) child.QueueFree();
        _slotNodes.Clear();

        foreach (var slot in _client.AllSlots().OrderBy(s => s.SlotId))
        {
            var node = _slotScene.Instantiate<InventorySlotUI>();
            _grid.AddChild(node);
            node.Bind(slot.SlotId, _client);
            _slotNodes[slot.SlotId] = node;
        }
    }

    private void OnSlotChanged(int slotId)
    {
        if (_slotNodes.TryGetValue(slotId, out var node))
            node.Refresh(_client.GetSlot(slotId));
    }

    private void OnInventoryError(string code, int slotId)
    {
        // Show brief error feedback — slot node handles the visual flash
        if (_slotNodes.TryGetValue(slotId, out var node))
            node.FlashError();
        GD.PrintErr($"[Inventory] Error: {code} on slot {slotId}");
    }
}
```

---

## 8. The ENet-only gap — what still needs custom server equivalents

These RPCs currently only fire when `Multiplayer.MultiplayerPeer` is set (ENet path). They have **no custom TCP equivalent yet** and must be added before the custom path is feature-complete.

| Godot RPC | What it does | Custom server equivalent needed |
|---|---|---|
| `SpawnPlayerRpc(clientId)` | Tells all clients to instantiate a player node | Add `PLAYER_SPAWN` TCP message (you already have `PLAYER_JOINED` — add `spawnPosition` field) |
| `DespawnPlayerRpc(clientId)` | Removes a player node from all clients | `PLAYER_LEFT` already exists — clients should despawn on receiving it |
| `SpawnFloorItemRpc(id, pos, itemType)` | Syncs floor items on room join | Add `FLOOR_ITEM_SPAWN` TCP message (see §2) |
| `SyncPosition(pos)` | Per-frame position from `_PhysicsProcess` | Already handled by your UDP `UPDATE` command — **no action needed** |

> `DespawnPlayerRpc` can likely be removed — your existing `PLAYER_LEFT` message already carries enough information for the client to despawn the player node. Unify the handling there.

---

## 9. File structure

```
Scripts/
├── Inventory/
│   ├── Data/
│   │   ├── InventorySlot.cs
│   │   ├── Inventory.cs
│   │   └── ItemDefinition.cs
│   ├── Server/
│   │   ├── InventoryManager.cs
│   │   ├── CommandValidator.cs
│   │   └── ItemRegistry.cs
│   └── Client/
│       ├── InventoryClient.cs
│       └── InventoryUI.cs
├── Network/
│   ├── INetworkAdapter.cs
│   ├── ENetAdapter.cs
│   └── CustomAdapter.cs
└── Resources/
    └── items.json
```

---

## 10. Wiring it all together (startup)

```csharp
// In your main scene or GameManager _Ready():

INetworkAdapter adapter;

if (useCustomServer)
{
    // CustomAdapter hooks into the existing Client singleton
    adapter = new CustomAdapter(Client.Instance);
}
else
{
    // ENetAdapter is a Node — must be added to scene tree for RPCs to work
    adapter = GetNode<ENetAdapter>("/root/ENetAdapter");
}

// Server side (runs on dedicated server or host):
ItemRegistry.Instance.LoadFromJson("res://Resources/items.json");
InventoryManager.Instance.Initialise(adapter);

// Client side:
var inventoryClient = GetNode<InventoryClient>("/root/InventoryClient");
inventoryClient.Initialise(adapter);
```

Hook `InventoryManager.Instance.OnPlayerJoined(sessionId)` into your existing `AUTH_OK` handler, and `OnPlayerLeft(sessionId)` into your existing disconnect/`PLAYER_LEFT` handler.

---

## 11. Implementation order

Work in this sequence to always have something runnable:

1. **Data structures** — `InventorySlot`, `Inventory`, `ItemDefinition` (no deps)
2. **`INetworkAdapter` interface** (no deps)
3. **`ItemRegistry`** + `items.json` with 2–3 test items
4. **`CommandValidator`** (depends on `ItemRegistry`)
5. **`InventoryManager`** (depends on validator + adapter interface)
6. **`CustomAdapter`** wired to your existing `Client.cs`
7. **`InventoryClient`** + connect to `CustomAdapter`
8. **Test the data round-trip** — add item server-side, verify `INV_STATE_FULL` reaches client
9. **`InventoryUI`** — minimal grid, no drag/drop yet
10. **`ENetAdapter`** — swap in, verify same test passes
11. **Optimistic updates** — enable and test rollback with a forced `INV_ERROR`
12. **Floor item TCP messages** — `FLOOR_ITEM_SPAWN` / `FLOOR_ITEM_DESPAWN`

---

## 12. Open questions to decide before coding

- [ ] **Where does `InventoryManager` live on the custom server?** Is your server a separate C# process, or does Godot run in headless server mode? The adapter wiring differs.
- [ ] **Stacking behaviour on `MoveSlot`:** pure swap, or merge stacks when item IDs match?
- [ ] **What happens to dropped items?** `HandleDropItem` has a `TODO` — do floor items use your existing `SpawnFloorItemRpc` path, or the new `FLOOR_ITEM_SPAWN` TCP message?
- [ ] **Item effect system:** `HandleUseItem` has a `TODO` — consumables, equippables, or both?
- [ ] **Persistence:** is the inventory saved between sessions? If so, where — DB, flat file, or Godot save?
