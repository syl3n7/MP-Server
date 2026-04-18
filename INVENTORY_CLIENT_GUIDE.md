# Inventory System — Godot 4 Client Integration Guide

> Companion to `GODOT_CLIENT_GUIDE.md`.  
> All inventory messages travel over **TCP** (not UDP) — same TLS stream, same newline-delimited JSON format.

---

## Table of Contents

1. [How Inventory Messages Work](#1-how-inventory-messages-work)
2. [Server → Client Events](#2-server--client-events)
3. [Client → Server Commands](#3-client--server-commands)
4. [InventoryClient.gd — Full Implementation](#4-inventoryclientgd--full-implementation)
5. [Wiring into Your Existing _on_tcp_message](#5-wiring-into-your-existing-_on_tcp_message)
6. [Floor Item Events](#6-floor-item-events)
7. [Quick Reference Table](#7-quick-reference-table)

---

## 1. How Inventory Messages Work

After a successful `REGISTER_OK`, `LOGIN_OK`, or `AUTO_AUTH_OK` the server:

1. Creates a fresh 30-slot inventory for your session.
2. Immediately sends an `INV_STATE_FULL` event with all 30 (empty) slots.

From that point every inventory change sends either `INV_SLOT_UPDATED` or `INV_SLOT_CLEARED` for the specific slot(s) that changed. You never need to poll — the server pushes all changes.

**All inventory messages use a `"type"` field (not `"command"`).**  
Filter them in your TCP dispatch by checking `msg.get("type", "").begins_with("INV_")`.

---

## 2. Server → Client Events

### INV_STATE_FULL
Sent on login/register and whenever `INV_REQUEST_SYNC` is called. Replace your entire local slot state.

```json
{
  "type": "INV_STATE_FULL",
  "slots": [
    { "slotId": 0, "itemId": "",          "quantity": 0, "meta": {} },
    { "slotId": 1, "itemId": "iron_sword","quantity": 1, "meta": {} },
    ...
  ]
}
```

### INV_SLOT_UPDATED
One slot changed (item placed, quantity changed, move destination).

```json
{ "type": "INV_SLOT_UPDATED", "slotId": 3, "itemId": "health_potion", "quantity": 4, "meta": {} }
```

### INV_SLOT_CLEARED
One slot became empty (item dropped/consumed completely, or swap moved the item away).

```json
{ "type": "INV_SLOT_CLEARED", "slotId": 5 }
```

### INV_ERROR
Your command was rejected. **Roll back any optimistic UI change for the affected slot.**

```json
{ "type": "INV_ERROR", "code": "STACK_FULL", "slotId": 3, "message": "" }
```

| `code` | Meaning |
|---|---|
| `INVALID_SLOT` | Slot index out of range |
| `NOT_OWNER` | Server has no inventory for your session (shouldn't happen after auth) |
| `SLOT_EMPTY` | Tried to move/drop/use an empty slot |
| `STACK_FULL` | Destination slot can't accept more of that item |
| `ITEM_NOT_DROPPABLE` | Item definition forbids dropping |
| `ITEM_NOT_USABLE` | Item definition forbids use |

---

## 3. Client → Server Commands

All commands use a `"command"` field and require authentication.

### INV_MOVE_SLOT
Swap or merge two slots. The server applies the authoritative result and sends back `INV_SLOT_UPDATED` / `INV_SLOT_CLEARED` for both affected slots (or `INV_ERROR` on failure).

```gdscript
_send({"command": "INV_MOVE_SLOT", "fromSlot": 1, "toSlot": 5})
```

**Stacking rule (server-side):** if both slots contain the same `itemId`, the server merges as much as the destination's `maxStack` allows, then sends updates for both slots. If `fromSlot` empties out it sends `INV_SLOT_CLEARED` for it.  
If the item IDs differ, a pure swap is performed.

### INV_DROP_ITEM
Remove `quantity` of an item from a slot and place it on the world floor. The server clears/updates the slot and broadcasts `FLOOR_ITEM_SPAWN` to everyone in the room.

```gdscript
_send({"command": "INV_DROP_ITEM", "slotId": 2, "quantity": 1})
```

`quantity` defaults to 1 if omitted.  
Must be in a room — the server rejects this command otherwise.

### INV_USE_ITEM
Activate the item in a slot. The server consumes 1 quantity (and clears the slot if it was the last one). Effect logic is server-side.

```gdscript
_send({"command": "INV_USE_ITEM", "slotId": 4})
```

### INV_REQUEST_SYNC
Ask the server to resend the full inventory state. Use this on scene transitions, reconnects after a hiccup, or any time you suspect your local state is out of sync.

```gdscript
_send({"command": "INV_REQUEST_SYNC"})
# Server responds with INV_STATE_FULL
```

---

## 4. InventoryClient.gd — Full Implementation

Create this as an **autoload** node (`InventoryClient`) so it's accessible from anywhere.

```gdscript
# InventoryClient.gd
# Autoload — add to Project > Project Settings > Autoloads as "InventoryClient"
extends Node

# ── Signals ────────────────────────────────────────────────────────────────────
signal slot_changed(slot_id: int)
signal inventory_synced                       # full replace happened
signal inventory_error(code: String, slot_id: int)
signal floor_item_spawned(id: String, item_type: String, quantity: int, position: Vector3)
signal floor_item_despawned(id: String)

# ── State ──────────────────────────────────────────────────────────────────────
# slot_id → { "slotId":int, "itemId":String, "quantity":int, "meta":Dictionary }
var _slots: Dictionary = {}

# Optimistic rollback: key → array of slot snapshots before the change
var _pending: Dictionary = {}

# ── Public API — Queries ───────────────────────────────────────────────────────

func get_slot(slot_id: int) -> Dictionary:
    return _slots.get(slot_id, {"slotId": slot_id, "itemId": "", "quantity": 0, "meta": {}})

func all_slots() -> Array:
    var out := []
    for i in range(30):
        out.append(get_slot(i))
    return out

func is_empty(slot_id: int) -> bool:
    var s := get_slot(slot_id)
    return s["itemId"] == "" or s["quantity"] <= 0

# ── Public API — Commands (optimistic) ────────────────────────────────────────

func move_slot(from_slot: int, to_slot: int) -> void:
    if is_empty(from_slot):
        return

    # Snapshot both slots for rollback
    _save_rollback("MOVE_%d_%d" % [from_slot, to_slot], [from_slot, to_slot])

    # Optimistic: swap locally right away
    var src  := _clone_slot(get_slot(from_slot), from_slot)
    var dest := _clone_slot(get_slot(to_slot),   to_slot)

    var src_item  := src["itemId"]
    var dest_item := dest["itemId"]

    if src_item == dest_item and dest_item != "":
        # Visual merge hint — server decides exact quantities
        dest["quantity"] = dest["quantity"] + src["quantity"]
        src["itemId"]    = ""
        src["quantity"]  = 0
    else:
        # Swap
        var tmp := src.duplicate()
        src["itemId"]    = dest["itemId"]
        src["quantity"]  = dest["quantity"]
        src["meta"]      = dest["meta"]
        dest["itemId"]   = tmp["itemId"]
        dest["quantity"] = tmp["quantity"]
        dest["meta"]     = tmp["meta"]

    _slots[from_slot] = src
    _slots[to_slot]   = dest
    slot_changed.emit(from_slot)
    slot_changed.emit(to_slot)

    # Server is authoritative — it will send back the real result
    _net_send({"command": "INV_MOVE_SLOT", "fromSlot": from_slot, "toSlot": to_slot})

func drop_item(slot_id: int, quantity: int = 1) -> void:
    if is_empty(slot_id):
        return

    _save_rollback("DROP_%d" % slot_id, [slot_id])

    # Optimistic: reduce quantity locally
    var s := _clone_slot(get_slot(slot_id), slot_id)
    s["quantity"] -= quantity
    if s["quantity"] <= 0:
        s["itemId"]   = ""
        s["quantity"] = 0
    _slots[slot_id] = s
    slot_changed.emit(slot_id)

    _net_send({"command": "INV_DROP_ITEM", "slotId": slot_id, "quantity": quantity})

func use_item(slot_id: int) -> void:
    if is_empty(slot_id):
        return
    # No optimistic local change — effect is server-defined
    _net_send({"command": "INV_USE_ITEM", "slotId": slot_id})

func request_sync() -> void:
    _net_send({"command": "INV_REQUEST_SYNC"})

# ── Server event dispatcher — call this from your _on_tcp_message ─────────────

func handle_server_message(msg: Dictionary) -> void:
    var t: String = msg.get("type", "")
    match t:
        "INV_STATE_FULL":
            _apply_full_state(msg)
        "INV_SLOT_UPDATED":
            _apply_slot_updated(msg)
        "INV_SLOT_CLEARED":
            _apply_slot_cleared(msg)
        "INV_ERROR":
            _apply_error(msg)
        "FLOOR_ITEM_SPAWN":
            _apply_floor_spawn(msg)
        "FLOOR_ITEM_DESPAWN":
            _apply_floor_despawn(msg)

# ── Private handlers ───────────────────────────────────────────────────────────

func _apply_full_state(msg: Dictionary) -> void:
    _slots.clear()
    _pending.clear()
    for raw_slot in msg.get("slots", []):
        var s: Dictionary = raw_slot
        _slots[s["slotId"]] = s
    inventory_synced.emit()

func _apply_slot_updated(msg: Dictionary) -> void:
    var slot_id: int = msg["slotId"]
    _slots[slot_id] = {
        "slotId":   slot_id,
        "itemId":   msg.get("itemId", ""),
        "quantity": msg.get("quantity", 0),
        "meta":     msg.get("meta", {})
    }
    _drop_rollback_for_slot(slot_id)
    slot_changed.emit(slot_id)

func _apply_slot_cleared(msg: Dictionary) -> void:
    var slot_id: int = msg["slotId"]
    _slots[slot_id] = {"slotId": slot_id, "itemId": "", "quantity": 0, "meta": {}}
    _drop_rollback_for_slot(slot_id)
    slot_changed.emit(slot_id)

func _apply_error(msg: Dictionary) -> void:
    var code: String  = msg.get("code", "UNKNOWN")
    var slot_id: int  = msg.get("slotId", -1)
    _rollback_slot(slot_id)
    inventory_error.emit(code, slot_id)

func _apply_floor_spawn(msg: Dictionary) -> void:
    var pos_raw: Dictionary = msg.get("position", {})
    var pos := Vector3(
        float(pos_raw.get("x", 0.0)),
        float(pos_raw.get("y", 0.0)),
        float(pos_raw.get("z", 0.0))
    )
    floor_item_spawned.emit(
        str(msg.get("id", "")),
        str(msg.get("itemType", "")),
        int(msg.get("quantity", 1)),
        pos
    )

func _apply_floor_despawn(msg: Dictionary) -> void:
    floor_item_despawned.emit(str(msg.get("id", "")))

# ── Rollback helpers ───────────────────────────────────────────────────────────

func _save_rollback(key: String, slot_ids: Array) -> void:
    var snapshots := []
    for sid in slot_ids:
        snapshots.append(_clone_slot(get_slot(sid), sid))
    _pending[key] = snapshots

func _rollback_slot(slot_id: int) -> void:
    var keys_to_remove := []
    for key in _pending:
        var snapshots: Array = _pending[key]
        for snap in snapshots:
            if snap["slotId"] == slot_id:
                keys_to_remove.append(key)
                break
    for key in keys_to_remove:
        for snap in _pending[key]:
            _slots[snap["slotId"]] = snap
            slot_changed.emit(snap["slotId"])
        _pending.erase(key)

func _drop_rollback_for_slot(slot_id: int) -> void:
    var keys_to_remove := []
    for key in _pending:
        for snap in _pending[key]:
            if snap["slotId"] == slot_id:
                keys_to_remove.append(key)
                break
    for key in keys_to_remove:
        _pending.erase(key)

func _clone_slot(s: Dictionary, new_slot_id: int) -> Dictionary:
    return {
        "slotId":   new_slot_id,
        "itemId":   s.get("itemId", ""),
        "quantity": s.get("quantity", 0),
        "meta":     s.get("meta", {}).duplicate()
    }

# ── Network helper — replace with however you send TCP messages ───────────────

func _net_send(data: Dictionary) -> void:
    # Adjust this to wherever your TCP send function lives.
    # If your network manager is autoloaded as "Network":
    Network._send(data)
```

---

## 5. Wiring into Your Existing `_on_tcp_message`

Add the inventory/floor-item branch at the top of your dispatch function, before the existing `match`:

```gdscript
func _on_tcp_message(raw: String) -> void:
    var msg = JSON.parse_string(raw)
    if msg == null:
        return

    # ── Inventory and floor item events ──────────────────────────────────────
    var msg_type: String = msg.get("type", "")
    if msg_type.begins_with("INV_") or msg_type.begins_with("FLOOR_ITEM_"):
        InventoryClient.handle_server_message(msg)
        return
    # ─────────────────────────────────────────────────────────────────────────

    match msg.get("command", ""):
        "CONNECTED":
            _session_id = msg["sessionId"]
        "REGISTER_OK":
            _on_register_ok(msg)
        # ... rest of your existing cases unchanged ...
```

No other changes to `_on_tcp_message` are needed.

---

## 6. Floor Item Events

When any player drops an item the server broadcasts `FLOOR_ITEM_SPAWN` to the whole room over TCP. Connect `InventoryClient.floor_item_spawned` to your world scene to spawn the pickup node:

```gdscript
# In your world/game scene _ready():
InventoryClient.floor_item_spawned.connect(_on_floor_item_spawned)
InventoryClient.floor_item_despawned.connect(_on_floor_item_despawned)

var _floor_items: Dictionary = {}   # id → Node

func _on_floor_item_spawned(id: String, item_type: String, quantity: int, position: Vector3) -> void:
    if _floor_items.has(id):
        return   # already spawned (shouldn't happen)
    var node := FloorItemScene.instantiate()   # your pickup scene
    node.item_type = item_type
    node.quantity  = quantity
    node.position  = position
    add_child(node)
    _floor_items[id] = node

func _on_floor_item_despawned(id: String) -> void:
    if _floor_items.has(id):
        _floor_items[id].queue_free()
        _floor_items.erase(id)
```

`FLOOR_ITEM_DESPAWN` is not yet triggered by the server automatically — it will be wired when a floor-item pickup mechanic is added server-side. Connect the signal now so the client is ready.

---

## 7. Quick Reference Table

### Server → Client (use `msg.get("type")`)

| `type` | Key fields | When |
|---|---|---|
| `INV_STATE_FULL` | `slots` (array) | On login / `INV_REQUEST_SYNC` |
| `INV_SLOT_UPDATED` | `slotId`, `itemId`, `quantity`, `meta` | Any single-slot change |
| `INV_SLOT_CLEARED` | `slotId` | Slot emptied |
| `INV_ERROR` | `code`, `slotId`, `message` | Command rejected — roll back |
| `FLOOR_ITEM_SPAWN` | `id`, `itemType`, `quantity`, `position {x,y,z}` | Item dropped in room |
| `FLOOR_ITEM_DESPAWN` | `id` | Floor item removed (future) |

### Client → Server (use `"command"` field)

| `command` | Required fields | Optional |
|---|---|---|
| `INV_MOVE_SLOT` | `fromSlot` (int), `toSlot` (int) | — |
| `INV_DROP_ITEM` | `slotId` (int) | `quantity` (int, default 1) |
| `INV_USE_ITEM` | `slotId` (int) | — |
| `INV_REQUEST_SYNC` | — | — |

### Error codes

| `code` | Meaning | Client action |
|---|---|---|
| `INVALID_SLOT` | Index out of range | Rollback, log |
| `NOT_OWNER` | Session has no inventory | Request sync |
| `SLOT_EMPTY` | Nothing in that slot | Rollback |
| `STACK_FULL` | Can't merge any more | Rollback, show feedback |
| `ITEM_NOT_DROPPABLE` | Item definition forbids drop | Rollback, show feedback |
| `ITEM_NOT_USABLE` | Item definition forbids use | Rollback, show feedback |
