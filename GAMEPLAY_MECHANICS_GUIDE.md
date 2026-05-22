# MP-Server — Core Gameplay Mechanics Guide

> Client-facing reference for the three core gameplay actions: **move**, **inventory**, and **combat**.  
> Covers protocol packets, server responses, and GDScript integration patterns.  
> Assumes the player is already authenticated and in a room.  
> See `GODOT_CLIENT_GUIDE.md` for connection, auth, and UDP encryption setup.

---

## Table of Contents

1. [Move (UDP)](#1-move-udp)
2. [Inventory](#2-inventory-tcp)
3. [Combat — player_kill & respawn](#3-combat-tcp)
4. [Full Message Reference](#4-full-message-reference)
5. [Signal Wiring Cheatsheet](#5-signal-wiring-cheatsheet)

---

## 1. Move (UDP)

Movement is UDP-only. All UDP packets must be AES-256-CBC encrypted — see `GODOT_CLIENT_GUIDE.md §6`.

### 1.1 Send your position

Send every physics frame (or at a fixed rate, 30–60 Hz recommended):

```gdscript
func send_position(position: Vector3, rotation: Quaternion) -> void:
    _send_udp({
        "command":   "UPDATE",
        "sessionId": _session_id,
        "position":  {"x": position.x, "y": position.y, "z": position.z},
        "rotation":  {"x": rotation.x, "y": rotation.y, "z": rotation.z, "w": rotation.w}
    })
```

### 1.2 Receive other players' positions

The server rebroadcasts every move to all other players in the room:

```json
{
    "command":  "UPDATE",
    "sessionId": "<sender session id>",
    "position": {"x": 1.0, "y": 0.0, "z": -3.5},
    "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
}
```

```gdscript
func _poll_udp() -> void:
    while _udp.get_available_packet_count() > 0:
        var msg := _parse_udp_packet(_udp.get_packet())
        match msg.get("command", ""):
            "UPDATE":
                var sid: String = msg["sessionId"]
                if sid == _session_id:
                    return  # ignore own echo (server filters, but guard anyway)
                var pos := Vector3(msg["position"]["x"], msg["position"]["y"], msg["position"]["z"])
                var rot := Quaternion(msg["rotation"]["x"], msg["rotation"]["y"],
                                     msg["rotation"]["z"], msg["rotation"]["w"])
                emit_signal("remote_position_updated", sid, pos, rot)
```

### 1.3 Send raw input (optional)

Use this instead of (or alongside) positions if you relay inputs for client-side prediction:

```gdscript
func send_input(data: Dictionary) -> void:
    data["command"]   = "INPUT"
    data["sessionId"] = _session_id
    data["roomId"]    = _current_room_id
    _send_udp(data)
```

Other players receive the same dictionary back, keyed by `sessionId`. Parse it however your game needs.

---

## 2. Inventory (TCP)

Inventory uses **TCP** for reliability. All commands require auth and an active room.

> ⚠️ Inventory responses use `"type"` as the key — **not** `"command"`.  
> Match them in your TCP handler under the `"type"` field.

### 2.1 Request a full sync

Call this once after joining a room to populate your local inventory UI:

```gdscript
_send({"command": "INV_REQUEST_SYNC"})
```

**Server responds (only to you):**
```json
{
    "type": "INV_STATE_FULL",
    "slots": [
        {"slotId": 0, "itemId": "sword_01", "quantity": 1, "meta": {}},
        {"slotId": 1, "itemId": "",         "quantity": 0, "meta": {}},
        ...
    ]
}
```
Iterate `slots` (30 total by default) and populate your inventory grid.

### 2.2 Move a slot (drag & drop)

```gdscript
func inventory_move_slot(from_slot: int, to_slot: int) -> void:
    _send({"command": "INV_MOVE_SLOT", "fromSlot": from_slot, "toSlot": to_slot})
```

**Server responds (only to you) with one or two slot updates:**
```json
{"type": "INV_SLOT_UPDATED", "slotId": 1, "itemId": "sword_01", "quantity": 1, "meta": {}}
{"type": "INV_SLOT_CLEARED",  "slotId": 0}
```
`INV_SLOT_CLEARED` means the source slot is now empty. `INV_SLOT_UPDATED` carries the new state.  
If the items were merged (same item type), you get two `INV_SLOT_UPDATED` messages.

### 2.3 Drop an item

```gdscript
func inventory_drop(slot_id: int, quantity: int = 1) -> void:
    _send({"command": "INV_DROP_ITEM", "slotId": slot_id, "quantity": quantity})
```

**Server responds (only to you):** `INV_SLOT_UPDATED` or `INV_SLOT_CLEARED` for the affected slot.

**Server broadcasts to the entire room:**
```json
{
    "type":     "FLOOR_ITEM_SPAWN",
    "id":       "<unique floor item id>",
    "itemType": "sword_01",
    "quantity": 1,
    "position": {"x": 5.0, "y": 0.0, "z": 2.0}
}
```
Use `id` to track the floor item node. Spawn a pickup entity at `position` for everyone in the room.

### 2.4 Use an item

```gdscript
func inventory_use(slot_id: int) -> void:
    _send({"command": "INV_USE_ITEM", "slotId": slot_id})
```

**Server responds (only to you):** `INV_SLOT_UPDATED` or `INV_SLOT_CLEARED` (quantity decremented by 1).  
Apply the item's effect locally — the server does not currently broadcast use events.

### 2.5 Inventory error responses

```json
{"type": "INV_ERROR", "code": "SLOT_EMPTY", "slotId": 3, "message": ""}
```

| Code | Meaning |
|---|---|
| `SLOT_EMPTY` | Source slot has no item |
| `INVALID_SLOT` | Slot index out of range |
| `NOT_OWNER` | No inventory found for this session |

### 2.6 GDScript handler

```gdscript
func _on_tcp_message(raw: String) -> void:
    var msg = JSON.parse_string(raw)
    if msg == null: return

    # Inventory events use "type"; gameplay/combat use "command"
    match msg.get("type", msg.get("command", "")):
        # ── Inventory ──────────────────────────────────────────────────────
        "INV_STATE_FULL":
            emit_signal("inventory_full_sync", msg["slots"])
        "INV_SLOT_UPDATED":
            emit_signal("inventory_slot_updated", msg)   # pass whole dict
        "INV_SLOT_CLEARED":
            emit_signal("inventory_slot_cleared", msg["slotId"])
        "INV_ERROR":
            push_error("Inventory error [%s] slot %d: %s" % [msg["code"], msg["slotId"], msg["message"]])
        "FLOOR_ITEM_SPAWN":
            emit_signal("floor_item_spawned", msg)
        # ... other commands
```

---

## 3. Combat (TCP)

Combat uses **TCP** (reliable delivery is essential for kill/respawn events).  
Both `player_kill` and `respawn` use the **envelope action** format (`"action"` key, lowercase).

### 3.1 Kill a player

Send when your game logic determines you killed another player (e.g. projectile hit confirmed locally):

```gdscript
func report_kill(victim_session_id: String) -> void:
    _send({
        "action":   "player_kill",
        "targetId": victim_session_id
    })
```

**Server validates:**
- Both players are in the same room
- Target is not already dead

**Server broadcasts to the entire room (TCP):**
```json
{
    "command":    "PLAYER_KILLED",
    "killerId":   "<killer session id>",
    "killerName": "Alice",
    "victimId":   "<victim session id>",
    "victimName": "Bob"
}
```

**Server sends error to killer (TCP) if invalid:**
```json
{"command": "ERROR", "message": "Target not found in your room."}
{"command": "ERROR", "message": "Target is already dead."}
```

### 3.2 Death handling on the victim client

When you receive `PLAYER_KILLED` and `victimId == _session_id`, you are dead:

```gdscript
func _handle_player_killed(msg: Dictionary) -> void:
    var victim_id: String = msg["victimId"]
    var killer_name: String = msg["killerName"]

    emit_signal("player_killed", msg["killerId"], victim_id)

    if victim_id == _session_id:
        # You died — play death animation, then request respawn
        emit_signal("local_player_died", killer_name)
        # Your game triggers respawn_request() after the animation finishes
    else:
        # Another player died — hide/despawn their node
        emit_signal("remote_player_died", victim_id)
```

### 3.3 Request respawn

Call this after your death animation completes:

```gdscript
func request_respawn() -> void:
    _send({"action": "respawn"})
```

**Server validates:** you are actually marked as dead.

**Server responds (only to you):**
```json
{
    "command":    "RESPAWN_OK",
    "spawnIndex": 2
}
```
`spawnIndex` is a 0-based index into your scene's spawn point array (same system used by `START_GAME`).

**Server broadcasts to the entire room:**
```json
{
    "command":     "PLAYER_RESPAWNED",
    "sessionId":   "<your session id>",
    "playerName":  "Bob",
    "spawnIndex":  2
}
```

### 3.4 Full combat GDScript flow

```gdscript
# ── Signals ────────────────────────────────────────────────────────────────
signal player_killed(killer_id: String, victim_id: String)
signal local_player_died(killer_name: String)
signal remote_player_died(victim_id: String)
signal local_player_respawned(spawn_index: int)
signal remote_player_respawned(session_id: String, spawn_index: int)

# ── TCP handler additions ───────────────────────────────────────────────────
func _on_tcp_message(raw: String) -> void:
    var msg = JSON.parse_string(raw)
    if msg == null: return
    match msg.get("command", ""):
        "PLAYER_KILLED":
            _handle_player_killed(msg)
        "PLAYER_RESPAWNED":
            _handle_player_respawned(msg)
        "RESPAWN_OK":
            emit_signal("local_player_respawned", msg["spawnIndex"])
        # ... other commands

func _handle_player_killed(msg: Dictionary) -> void:
    var victim_id: String = msg["victimId"]
    emit_signal("player_killed", msg["killerId"], victim_id)
    if victim_id == _session_id:
        emit_signal("local_player_died", msg["killerName"])
    else:
        emit_signal("remote_player_died", victim_id)

func _handle_player_respawned(msg: Dictionary) -> void:
    var sid: String   = msg["sessionId"]
    var idx: int      = msg["spawnIndex"]
    if sid == _session_id:
        # RESPAWN_OK already handled this — optionally ignore duplicate
        pass
    else:
        emit_signal("remote_player_respawned", sid, idx)

# ── Game world wiring (attach in your GameWorld scene) ─────────────────────
func _ready() -> void:
    mp_client.local_player_died.connect(_on_local_died)
    mp_client.remote_player_died.connect(_on_remote_died)
    mp_client.local_player_respawned.connect(_on_local_respawned)
    mp_client.remote_player_respawned.connect(_on_remote_respawned)

func _on_local_died(killer_name: String) -> void:
    $Player.play_death_animation()
    $HUD.show_death_screen(killer_name)
    # After animation:
    await $Player.death_animation_finished
    mp_client.request_respawn()

func _on_remote_died(victim_id: String) -> void:
    get_node("RemotePlayers/" + victim_id).play_death_animation()

func _on_local_respawned(spawn_index: int) -> void:
    var spawn_point: Node3D = $SpawnPoints.get_child(spawn_index)
    $Player.respawn(spawn_point.global_position)
    $HUD.hide_death_screen()

func _on_remote_respawned(session_id: String, spawn_index: int) -> void:
    var spawn_point: Node3D = $SpawnPoints.get_child(spawn_index)
    get_node("RemotePlayers/" + session_id).respawn(spawn_point.global_position)
```

---

## 4. Full Message Reference

### Client → Server

| Transport | Field | Value | Extra Fields | Description |
|---|---|---|---|---|
| UDP | `command` | `UPDATE` | `sessionId`, `position{x,y,z}`, `rotation{x,y,z,w}` | Send your position |
| UDP | `command` | `INPUT` | `sessionId`, `roomId`, `input{...}` | Send raw input |
| TCP | `command` | `INV_REQUEST_SYNC` | — | Request full inventory |
| TCP | `command` | `INV_MOVE_SLOT` | `fromSlot`, `toSlot` | Move/merge slot |
| TCP | `command` | `INV_DROP_ITEM` | `slotId`, `quantity` (opt, def 1) | Drop item to world |
| TCP | `command` | `INV_USE_ITEM` | `slotId` | Use item |
| TCP | `action` | `player_kill` | `targetId` | Report a kill |
| TCP | `action` | `respawn` | — | Request respawn after death |

### Server → Client

| Transport | Field | Value | Extra Fields | Recipient |
|---|---|---|---|---|
| UDP | `command` | `UPDATE` | `sessionId`, `position`, `rotation` | All peers in room |
| UDP | `command` | `INPUT` | `sessionId`, `roomId`, `input` | All peers in room |
| TCP | `type` | `INV_STATE_FULL` | `slots[]` | You only |
| TCP | `type` | `INV_SLOT_UPDATED` | `slotId`, `itemId`, `quantity`, `meta` | You only |
| TCP | `type` | `INV_SLOT_CLEARED` | `slotId` | You only |
| TCP | `type` | `INV_ERROR` | `code`, `slotId`, `message` | You only |
| TCP | `type` | `FLOOR_ITEM_SPAWN` | `id`, `itemType`, `quantity`, `position` | All in room |
| TCP | `command` | `PLAYER_KILLED` | `killerId`, `killerName`, `victimId`, `victimName` | All in room |
| TCP | `command` | `RESPAWN_OK` | `spawnIndex` | You only |
| TCP | `command` | `PLAYER_RESPAWNED` | `sessionId`, `playerName`, `spawnIndex` | All in room |

---

## 5. Signal Wiring Cheatsheet

Declare these signals on your `MPClient` autoload:

```gdscript
# Movement
signal remote_position_updated(session_id: String, position: Vector3, rotation: Quaternion)

# Inventory
signal inventory_full_sync(slots: Array)
signal inventory_slot_updated(slot: Dictionary)   # {slotId, itemId, quantity, meta}
signal inventory_slot_cleared(slot_id: int)
signal floor_item_spawned(data: Dictionary)       # {id, itemType, quantity, position}

# Combat
signal player_killed(killer_id: String, victim_id: String)
signal local_player_died(killer_name: String)
signal remote_player_died(victim_id: String)
signal local_player_respawned(spawn_index: int)
signal remote_player_respawned(session_id: String, spawn_index: int)
```
