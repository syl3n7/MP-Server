# MP-Server — Godot 4 Client Implementation Guide

> **This is the authoritative reference for Godot 4 (GDScript) clients.**  
> The other guide files contain outdated information (wrong ports, wrong welcome format, stale auth flow).  
> Always use this document.

---

## Table of Contents

1. [Connection Overview](#1-connection-overview)
2. [TLS TCP Connection](#2-tls-tcp-connection)
3. [TCP Protocol — Reading & Writing](#3-tcp-protocol--reading--writing)
4. [Authentication Flow](#4-authentication-flow)
5. [Room Management](#5-room-management)
6. [UDP Encryption (AES-256-CBC)](#6-udp-encryption-aes-256-cbc)
7. [Sending & Receiving Position Updates](#7-sending--receiving-position-updates)
8. [Sending Input Packets](#8-sending-input-packets)
9. [Keeping the Session Alive](#9-keeping-the-session-alive)
10. [Complete Command Reference](#10-complete-command-reference)
11. [Full GDScript Skeleton](#11-full-gdscript-skeleton)

---

## 1. Connection Overview

```
Server addresses (default from appsettings.json — confirm with server host):
  TCP (TLS):  <server_ip>:7777
  UDP:        <server_ip>:7778
```

**Full flow:**
```
1. Connect TCP → TLS handshake
2. Server sends: {"command":"CONNECTED","sessionId":"<32-char hex id>"}\n
3. Client saves sessionId — required for ALL UDP packets
4. Client sends AUTO_AUTH (if stored token exists) → AUTO_AUTH_OK
      OR registers (REGISTER) or logs in (LOGIN) → REGISTER_OK / LOGIN_OK
   Server responds with a persistent 30-day token — store it for next launch
5. UDP encryption is now active: derive AES key/IV from sessionId + shared secret
6. All UDP packets sent/received must use that AES key
7. Join or create a room, start the game, stream position/input via UDP
```

**Critical rules:**
- ALL UDP packets to the server **must** be AES-encrypted — the server rejects plaintext
- UDP packets the server sends **back** to you are also encrypted with **your** key/IV
- The `sessionId` must be included in every UDP packet payload
- TCP is rate-limited to **10 messages/second** — don't flood it
- Session times out after **60 seconds of inactivity** — send periodic heartbeats

---

## 2. TLS TCP Connection

Godot 4 uses `StreamPeerTCP` + `StreamPeerTLS` for TLS connections.

```gdscript
var _tcp: StreamPeerTCP
var _tls: StreamPeerTLS

func _connect_tcp(host: String, port: int) -> bool:
    _tcp = StreamPeerTCP.new()
    var err = _tcp.connect_to_host(host, port)
    if err != OK:
        push_error("TCP connect error: %s" % err)
        return false

    # Poll until connected (or failed)
    while _tcp.get_status() == StreamPeerTCP.STATUS_CONNECTING:
        _tcp.poll()
        await get_tree().process_frame

    if _tcp.get_status() != StreamPeerTCP.STATUS_CONNECTED:
        push_error("TCP connection failed")
        return false

    # Wrap in TLS — client_unsafe() accepts self-signed certificates (dev/LAN)
    _tls = StreamPeerTLS.new()
    err = _tls.connect_to_stream(_tcp, TLSOptions.client_unsafe())
    if err != OK:
        push_error("TLS start error: %s" % err)
        return false

    # Poll until TLS handshake completes
    while _tls.get_status() == StreamPeerTLS.STATUS_HANDSHAKING:
        _tls.poll()
        await get_tree().process_frame

    if _tls.get_status() != StreamPeerTLS.STATUS_CONNECTED:
        push_error("TLS handshake failed")
        return false

    return true
```

> **Note:** `TLSOptions.client_unsafe()` disables certificate verification — correct for  
> LAN/development with self-signed certs. For production, use `TLSOptions.client(cert)` with  
> a pinned/bundled certificate.

---

## 3. TCP Protocol — Reading & Writing

All TCP messages are **newline-delimited JSON** (`\n` after each message).

### Writing

```gdscript
func _send(data: Dictionary) -> void:
    var json = JSON.stringify(data) + "\n"
    _tls.put_data(json.to_utf8_buffer())
```

### Reading (polling in `_process`)

Because Godot's TLS peer is non-blocking, read byte-by-byte until you find `\n`:

```gdscript
var _tcp_buffer: PackedByteArray = PackedByteArray()

func _poll_tcp() -> void:
    _tls.poll()
    while _tls.get_available_bytes() > 0:
        var byte_arr = _tls.get_data(1)
        if byte_arr[0] != OK:
            break
        var b: int = byte_arr[1][0]
        if b == 10:  # '\n'
            if _tcp_buffer.size() > 0:
                _on_tcp_message(_tcp_buffer.get_string_from_utf8())
                _tcp_buffer.clear()
        else:
            _tcp_buffer.append(b)
```

Call `_poll_tcp()` from `_process(_delta)`.

### Welcome message

Immediately after TLS connects the server sends:
```json
{"command":"CONNECTED","sessionId":"a3f8c2..."}
```
Parse it to extract `sessionId`:

```gdscript
func _on_tcp_message(raw: String) -> void:
    var msg = JSON.parse_string(raw)
    if msg == null:
        return
    match msg.get("command", ""):
        "CONNECTED":
            _session_id = msg["sessionId"]
            # Proceed to auth
        "REGISTER_OK":
            _on_register_ok(msg)
        "LOGIN_OK":
            _on_login_ok(msg)
        "AUTO_AUTH_OK":
            _on_auto_auth_ok(msg)
        "REGISTER_FAILED", "LOGIN_FAILED", "AUTO_AUTH_FAILED":
            push_error("Auth failed: " + msg.get("message", ""))
        "ROOM_CREATED":
            _current_room_id = msg["roomId"]
        "JOIN_OK":
            _current_room_id = msg["roomId"]
        "ROOM_LIST":
            emit_signal("room_list_received", msg["rooms"])
        "ROOM_PLAYERS":
            emit_signal("room_players_received", msg["players"])
        "GAME_STARTED":
            _on_game_started(msg)
        "PLAYER_DISCONNECTED":
            emit_signal("player_disconnected", msg.get("sessionId", ""))
        "PONG":
            pass  # heartbeat reply
        "HEARTBEAT_ACK":
            pass
        "ERROR":
            push_error("Server error: " + msg.get("message", ""))
```

---

## 4. Authentication Flow

### 4.1 First Launch — Register

```gdscript
func register(username: String, password: String, email: String = "") -> void:
    _send({"command": "REGISTER", "username": username, "password": password, "email": email})

func _on_register_ok(msg: Dictionary) -> void:
    _is_authenticated = true
    _username = msg["username"]
    _save_token(msg["token"])     # persist for future launches
    _init_udp_crypto()            # must happen after sessionId + token are known
    emit_signal("authenticated")
```

**Validation rules (enforced by server):**
- Username: 3–50 characters, must be unique
- Password: minimum 6 characters

**Response on success:**
```json
{"command":"REGISTER_OK","userId":42,"username":"PlayerName","token":"base64token..."}
```

**Response on failure:**
```json
{"command":"REGISTER_FAILED","message":"Username already taken."}
```

---

### 4.2 Returning Player — Login

```gdscript
func login(username: String, password: String) -> void:
    _send({"command": "LOGIN", "username": username, "password": password})

func _on_login_ok(msg: Dictionary) -> void:
    _is_authenticated = true
    _username = msg["username"]
    _save_token(msg["token"])
    _init_udp_crypto()
    emit_signal("authenticated")
```

**Account lockout:** 3 wrong passwords → account locked for 30 minutes.

**Response on success:**
```json
{"command":"LOGIN_OK","userId":42,"username":"PlayerName","token":"base64token..."}
```

---

### 4.3 Silent Re-Auth — AUTO_AUTH (recommended on every launch)

Always try `AUTO_AUTH` first on reconnect. Only show login UI if it fails.

```gdscript
func try_auto_auth() -> void:
    var token := _load_token()
    if token.is_empty():
        emit_signal("auto_auth_failed")
        return
    _send({"command": "AUTO_AUTH", "token": token})

func _on_auto_auth_ok(msg: Dictionary) -> void:
    _is_authenticated = true
    _username = msg["username"]
    _init_udp_crypto()
    emit_signal("authenticated")

# If AUTO_AUTH_FAILED is received, delete the stored token and show login UI
```

**Response on success:**
```json
{"command":"AUTO_AUTH_OK","userId":42,"username":"PlayerName"}
```

**Tokens are valid for 30 days.** `AUTO_AUTH` with an expired/revoked token returns `AUTO_AUTH_FAILED`.

---

### 4.4 Token Storage

```gdscript
const _TOKEN_FILE := "user://mp_token.dat"

func _save_token(token: String) -> void:
    var f := FileAccess.open(_TOKEN_FILE, FileAccess.WRITE)
    if f:
        f.store_string(token)

func _load_token() -> String:
    if not FileAccess.file_exists(_TOKEN_FILE):
        return ""
    var f := FileAccess.open(_TOKEN_FILE, FileAccess.READ)
    return f.get_as_text().strip_edges() if f else ""

func _clear_token() -> void:
    if FileAccess.file_exists(_TOKEN_FILE):
        DirAccess.remove_absolute(ProjectSettings.globalize_path(_TOKEN_FILE))
```

---

### 4.5 Recommended Connect Sequence

```gdscript
func _start() -> void:
    if not await _connect_tcp(SERVER_HOST, TCP_PORT):
        return
    # _on_tcp_message will receive "CONNECTED" → _session_id set
    # Then:

func _on_connected() -> void:
    await try_auto_auth()
    # If AUTO_AUTH_FAILED signal fires → show login/register UI
    # If "authenticated" signal fires  → proceed to lobby
```

---

## 5. Room Management

All room commands require authentication first.

### List rooms
```gdscript
_send({"command": "LIST_ROOMS"})
# Response: {"command":"ROOM_LIST","rooms":[{"id":"...","name":"...","playerCount":1,"maxPlayers":20,"isActive":true,"hostId":"..."}]}
```

### Create room
```gdscript
_send({"command": "CREATE_ROOM", "name": "My Room", "maxPlayers": 4})
# Response: {"command":"ROOM_CREATED","roomId":"...","name":"My Room","maxPlayers":4}
# You are automatically added to the room as host
```

### Join room
```gdscript
_send({"command": "JOIN_ROOM", "roomId": target_room_id})
# Response: {"command":"JOIN_OK","roomId":"..."}
```

### Get players in current room
```gdscript
_send({"command": "GET_ROOM_PLAYERS"})
# Response: {"command":"ROOM_PLAYERS","roomId":"...","players":[{"id":"...","name":"..."}]}
```

### Start game (host only)
```gdscript
_send({"command": "START_GAME"})
# Broadcast to all room members: {"command":"GAME_STARTED","roomId":"...","hostId":"...","spawnPositions":{"<sessionId>":{"spawnIndex":0},...}}
# The host also receives it directly
```
`spawnPositions` is a dictionary keyed by `sessionId`. Look up your own `sessionId` to find your spawn index.

### Leave room
```gdscript
_send({"command": "LEAVE_ROOM"})
# Response: {"command":"LEAVE_OK","roomId":"..."}
# If you were host and others remain, host is transferred to the next player
```

### Chat message (in-room)
```gdscript
_send({"command": "MESSAGE", "message": "Hello!"})
# Broadcast to room: {"command":"CHAT_MESSAGE","senderName":"...","message":"..."} (not yet confirmed — see NOTE below)
```

### Relay message (direct, peer-to-peer via server)
```gdscript
_send({"command": "RELAY_MESSAGE", "targetId": other_session_id, "message": "payload"})
# Response: {"command":"RELAY_OK","targetId":"..."}
# Target receives: {"command":"RELAYED_MESSAGE","senderId":"...","senderName":"...","message":"payload"}
```

### Disconnect gracefully
```gdscript
_send({"command": "BYE"})
# Response: {"command":"BYE_OK"} — server then closes the connection
```

---

## 6. UDP Encryption (AES-256-CBC)

### 6.1 Key Derivation

The AES key and IV are derived once per session — they are fixed for the lifetime of the connection.

```
keyMaterial = (sessionId + sharedSecret).to_utf8_buffer()
hash        = SHA256(keyMaterial)            # 32 bytes
aesKey      = hash[0..31]                    # 32 bytes → AES-256
aesIV       = hash[16..31]                   # 16 bytes → CBC IV
```

> **`sharedSecret`** must match `SecurityConfig:UdpSharedSecret` in the server's `appsettings.json`.  
> Ask the server host for the correct value. Never commit it to a public repository.

```gdscript
var _aes_key: PackedByteArray
var _aes_iv: PackedByteArray

func _init_udp_crypto() -> void:
    var shared_secret := "change-me-before-deploying"  # MUST match server appsettings.json
    var material := (_session_id + shared_secret).to_utf8_buffer()

    var ctx := HashingContext.new()
    ctx.start(HashingContext.HASH_SHA256)
    ctx.update(material)
    var hash: PackedByteArray = ctx.finish()  # 32 bytes

    _aes_key = hash.slice(0, 32)
    _aes_iv  = hash.slice(16, 32)   # bytes 16–31 of the hash

    # Open UDP socket
    _udp.close()
    _udp.connect_to_host(SERVER_HOST, UDP_PORT)
```

### 6.2 PKCS7 Padding Helpers

Godot's `AESContext` requires data to be a multiple of 16 bytes — pad manually:

```gdscript
func _pkcs7_pad(data: PackedByteArray) -> PackedByteArray:
    var pad_len := 16 - (data.size() % 16)
    var padded := data.duplicate()
    for i in range(pad_len):
        padded.append(pad_len)
    return padded

func _pkcs7_unpad(data: PackedByteArray) -> PackedByteArray:
    if data.is_empty():
        return data
    var pad_len := int(data[-1])
    if pad_len < 1 or pad_len > 16:
        return data   # invalid padding, return as-is
    return data.slice(0, data.size() - pad_len)
```

### 6.3 Encrypt

```gdscript
func _encrypt(plaintext: String) -> PackedByteArray:
    var plain_bytes := plaintext.to_utf8_buffer()
    var padded := _pkcs7_pad(plain_bytes)

    var aes := AESContext.new()
    aes.start(AESContext.MODE_CBC_ENCRYPT, _aes_key, _aes_iv)
    var encrypted: PackedByteArray = aes.update(padded)
    aes.finish()
    return encrypted
```

### 6.4 Decrypt

```gdscript
func _decrypt(encrypted: PackedByteArray) -> String:
    var aes := AESContext.new()
    aes.start(AESContext.MODE_CBC_DECRYPT, _aes_key, _aes_iv)
    var decrypted: PackedByteArray = aes.update(encrypted)
    aes.finish()
    var unpadded := _pkcs7_unpad(decrypted)
    return unpadded.get_string_from_utf8()
```

### 6.5 Packet Format

```
[4 bytes — little-endian int32 — length of encrypted payload]
[N bytes — AES-256-CBC encrypted JSON]
```

```gdscript
func _make_udp_packet(data: Dictionary) -> PackedByteArray:
    var json := JSON.stringify(data)
    var encrypted := _encrypt(json)

    # 4-byte little-endian length header
    var packet := PackedByteArray()
    packet.resize(4)
    packet.encode_s32(0, encrypted.size())   # little-endian by default on x86/ARM
    packet.append_array(encrypted)
    return packet

func _parse_udp_packet(raw: PackedByteArray) -> Dictionary:
    if raw.size() < 4:
        return {}
    var length := raw.decode_s32(0)
    if length <= 0 or length != raw.size() - 4:
        return {}
    var encrypted := raw.slice(4, 4 + length)
    var json_str := _decrypt(encrypted)
    if json_str.is_empty():
        return {}
    var parsed = JSON.parse_string(json_str)
    return parsed if parsed is Dictionary else {}
```

---

## 7. Sending & Receiving Position Updates

### Sending (every game frame or at fixed rate)

The server accepts up to **120 UDP packets/second**. Sending at 30–60 Hz is recommended.

```gdscript
var _udp := PacketPeerUDP.new()

func send_position(position: Vector3, rotation: Quaternion) -> void:
    if not _is_authenticated:
        return
    var data := {
        "command":  "UPDATE",
        "sessionId": _session_id,
        "position": {"x": position.x, "y": position.y, "z": position.z},
        "rotation": {"x": rotation.x, "y": rotation.y, "z": rotation.z, "w": rotation.w}
    }
    _udp.put_packet(_make_udp_packet(data))
```

### Receiving (poll in `_process`)

The server sends position updates for OTHER players **back to you**, encrypted with YOUR key.

```gdscript
func _poll_udp() -> void:
    while _udp.get_available_packet_count() > 0:
        var raw := _udp.get_packet()
        var msg := _parse_udp_packet(raw)
        if msg.is_empty():
            continue
        match msg.get("command", ""):
            "UPDATE":
                _apply_remote_position(msg)
            "INPUT":
                _apply_remote_input(msg)

func _apply_remote_position(msg: Dictionary) -> void:
    var sender_id: String = msg.get("sessionId", "")
    if sender_id == _session_id:
        return  # ignore own echoes (server does not send these, but be safe)
    var pos := Vector3(msg["position"]["x"], msg["position"]["y"], msg["position"]["z"])
    var rot := Quaternion(msg["rotation"]["x"], msg["rotation"]["y"],
                          msg["rotation"]["z"], msg["rotation"]["w"])
    emit_signal("remote_position_updated", sender_id, pos, rot)
```

---

## 8. Sending Input Packets

If your game relays raw inputs (e.g. steering, throttle) rather than positions, use the `INPUT` command. Include `roomId` — the server uses it to route the broadcast.

```gdscript
func send_input(throttle: float, steer: float, brake: float) -> void:
    if not _is_authenticated or _current_room_id.is_empty():
        return
    var data := {
        "command":   "INPUT",
        "sessionId": _session_id,
        "roomId":    _current_room_id,
        "input": {
            "throttle": throttle,
            "steer":    steer,
            "brake":    brake
        }
    }
    _udp.put_packet(_make_udp_packet(data))
```

Other players in the room receive the same packet (re-encrypted with their own key), with the original `sessionId` intact so they know whose input it is.

---

## 9. Keeping the Session Alive

The server disconnects sessions idle for **60 seconds**. Send a TCP heartbeat periodically:

```gdscript
const HEARTBEAT_INTERVAL := 15.0   # seconds
var _heartbeat_timer := 0.0

func _process(delta: float) -> void:
    _poll_tcp()
    _poll_udp()

    _heartbeat_timer += delta
    if _heartbeat_timer >= HEARTBEAT_INTERVAL:
        _heartbeat_timer = 0.0
        _send_heartbeat()

func _send_heartbeat() -> void:
    if not _is_authenticated:
        _send({"command": "PING"})   # works pre-auth too
    else:
        _send({
            "action":    "heartbeat",
            "messageId": str(Time.get_ticks_msec())
        })
# Server replies: {"command":"HEARTBEAT_ACK","ackFor":"...","serverTimestampMs":...,"sessionId":"..."}
# or {"command":"PONG"} for the legacy PING
```

---

## 10. Complete Command Reference

### TCP Commands (Client → Server)

| Command | Before Auth? | Key Fields | Success Response | Failure Response |
|---|---|---|---|---|
| `REGISTER` | ✓ | `username`, `password`, `email` (opt) | `REGISTER_OK` + `userId`, `username`, `token` | `REGISTER_FAILED` + `message` |
| `LOGIN` | ✓ | `username`, `password` | `LOGIN_OK` + `userId`, `username`, `token` | `LOGIN_FAILED` + `message` |
| `AUTO_AUTH` | ✓ | `token` | `AUTO_AUTH_OK` + `userId`, `username` | `AUTO_AUTH_FAILED` + `message` |
| `PING` | ✓ | — | `PONG` | — |
| `LIST_ROOMS` | ✓ | — | `ROOM_LIST` + `rooms[]` | — |
| `PLAYER_INFO` | ✓ | — | `PLAYER_INFO` + `playerInfo` | — |
| `NAME` | Auth | `name` | `NAME_OK` + `name` | `ERROR` |
| `CREATE_ROOM` | Auth | `name`, `maxPlayers` (opt, default 20) | `ROOM_CREATED` + `roomId`, `name`, `maxPlayers` | `ERROR` |
| `JOIN_ROOM` | Auth | `roomId` | `JOIN_OK` + `roomId` | `ERROR` |
| `LEAVE_ROOM` | Auth | — | `LEAVE_OK` + `roomId` | `ERROR` |
| `GET_ROOM_PLAYERS` | Auth | — | `ROOM_PLAYERS` + `roomId`, `players[]` | `ERROR` |
| `START_GAME` | Auth (host) | — | `GAME_STARTED` broadcast + `spawnPositions` | `ERROR` |
| `MESSAGE` | Auth | `message` | `MESSAGE_OK` | `ERROR` |
| `RELAY_MESSAGE` | Auth | `targetId`, `message` | `RELAY_OK` + `targetId` | `ERROR` |
| `BYE` | ✓ | — | `BYE_OK` (then disconnects) | — |

### TCP Envelope Actions (Client → Server, requires Auth)

Send with `action` field instead of `command`:

| Action | Key Fields | Response |
|---|---|---|
| `heartbeat` | `messageId` (opt) | `HEARTBEAT_ACK` + `ackFor`, `serverTimestampMs`, `sessionId` |
| `snapshot_sync` | `messageId` (opt) | `SNAPSHOT` + `ackFor`, `roomId`, `players[]` |

### UDP Packets (Client → Server, Auth required, must be encrypted)

| `command` | Key Fields | Effect |
|---|---|---|
| `UPDATE` | `sessionId`, `position{x,y,z}`, `rotation{x,y,z,w}` | Broadcasts position to room members |
| `INPUT` | `sessionId`, `roomId`, `input{...}` | Broadcasts raw input to room members |

### UDP Packets (Server → Client, encrypted with YOUR key)

| `command` | Key Fields | Meaning |
|---|---|---|
| `UPDATE` | `sessionId`, `position{x,y,z}`, `rotation{x,y,z,w}` | Another player's position |
| `INPUT` | `sessionId`, `roomId`, `input{...}` | Another player's raw input |

---

## 11. Full GDScript Skeleton

```gdscript
extends Node
class_name MPClient

signal authenticated
signal auto_auth_failed
signal room_list_received(rooms: Array)
signal room_players_received(players: Array)
signal game_started(spawn_positions: Dictionary)
signal remote_position_updated(session_id: String, position: Vector3, rotation: Quaternion)
signal player_disconnected(session_id: String)

# ── Configuration ───────────────────────────────────────────────────────────
const SERVER_HOST     := "127.0.0.1"
const TCP_PORT        := 7777
const UDP_PORT        := 7778
# Must match SecurityConfig:UdpSharedSecret in server appsettings.json
const UDP_SHARED_SECRET := "change-me-before-deploying"
const TOKEN_FILE      := "user://mp_token.dat"
const HEARTBEAT_SECS  := 15.0

# ── State ───────────────────────────────────────────────────────────────────
var _tcp: StreamPeerTCP
var _tls: StreamPeerTLS
var _udp := PacketPeerUDP.new()

var _session_id       := ""
var _username         := ""
var _current_room_id  := ""
var _is_authenticated := false

var _aes_key: PackedByteArray
var _aes_iv:  PackedByteArray
var _tcp_buf  := PackedByteArray()
var _hb_timer := 0.0

# ── Lifecycle ────────────────────────────────────────────────────────────────
func _process(delta: float) -> void:
    _poll_tls()
    _poll_udp()
    _hb_timer += delta
    if _hb_timer >= HEARTBEAT_SECS:
        _hb_timer = 0.0
        _heartbeat()

# ── Connect ──────────────────────────────────────────────────────────────────
func connect_to_server() -> bool:
    _tcp = StreamPeerTCP.new()
    if _tcp.connect_to_host(SERVER_HOST, TCP_PORT) != OK:
        return false

    while _tcp.get_status() == StreamPeerTCP.STATUS_CONNECTING:
        _tcp.poll()
        await get_tree().process_frame

    if _tcp.get_status() != StreamPeerTCP.STATUS_CONNECTED:
        return false

    _tls = StreamPeerTLS.new()
    if _tls.connect_to_stream(_tcp, TLSOptions.client_unsafe()) != OK:
        return false

    while _tls.get_status() == StreamPeerTLS.STATUS_HANDSHAKING:
        _tls.poll()
        await get_tree().process_frame

    return _tls.get_status() == StreamPeerTLS.STATUS_CONNECTED

# ── TCP I/O ───────────────────────────────────────────────────────────────────
func _send(data: Dictionary) -> void:
    _tls.put_data((JSON.stringify(data) + "\n").to_utf8_buffer())

func _poll_tls() -> void:
    if _tls == null:
        return
    _tls.poll()
    while _tls.get_available_bytes() > 0:
        var res := _tls.get_data(1)
        if res[0] != OK:
            break
        var b: int = res[1][0]
        if b == 10:
            if _tcp_buf.size() > 0:
                _on_message(_tcp_buf.get_string_from_utf8())
                _tcp_buf.clear()
        else:
            _tcp_buf.append(b)

func _on_message(raw: String) -> void:
    var msg = JSON.parse_string(raw)
    if not msg is Dictionary:
        return
    match msg.get("command", ""):
        "CONNECTED":
            _session_id = msg["sessionId"]
        "REGISTER_OK":
            _is_authenticated = true
            _username = msg["username"]
            _save_token(msg["token"])
            _init_udp_crypto()
            emit_signal("authenticated")
        "LOGIN_OK":
            _is_authenticated = true
            _username = msg["username"]
            _save_token(msg["token"])
            _init_udp_crypto()
            emit_signal("authenticated")
        "AUTO_AUTH_OK":
            _is_authenticated = true
            _username = msg["username"]
            _init_udp_crypto()
            emit_signal("authenticated")
        "AUTO_AUTH_FAILED", "REGISTER_FAILED", "LOGIN_FAILED":
            emit_signal("auto_auth_failed")
        "ROOM_CREATED", "JOIN_OK":
            _current_room_id = msg["roomId"]
        "LEAVE_OK":
            _current_room_id = ""
        "ROOM_LIST":
            emit_signal("room_list_received", msg["rooms"])
        "ROOM_PLAYERS":
            emit_signal("room_players_received", msg["players"])
        "GAME_STARTED":
            emit_signal("game_started", msg.get("spawnPositions", {}))
        "PLAYER_DISCONNECTED":
            emit_signal("player_disconnected", msg.get("sessionId", ""))
        "ERROR":
            push_error("[MPClient] Server error: " + msg.get("message", ""))

# ── Auth ──────────────────────────────────────────────────────────────────────
func register(username: String, password: String, email: String = "") -> void:
    _send({"command": "REGISTER", "username": username, "password": password, "email": email})

func login(username: String, password: String) -> void:
    _send({"command": "LOGIN", "username": username, "password": password})

func try_auto_auth() -> void:
    var token := _load_token()
    if token.is_empty():
        emit_signal("auto_auth_failed")
        return
    _send({"command": "AUTO_AUTH", "token": token})

# ── Token storage ─────────────────────────────────────────────────────────────
func _save_token(token: String) -> void:
    var f := FileAccess.open(TOKEN_FILE, FileAccess.WRITE)
    if f:
        f.store_string(token)

func _load_token() -> String:
    if not FileAccess.file_exists(TOKEN_FILE):
        return ""
    var f := FileAccess.open(TOKEN_FILE, FileAccess.READ)
    return f.get_as_text().strip_edges() if f else ""

func _clear_token() -> void:
    if FileAccess.file_exists(TOKEN_FILE):
        DirAccess.remove_absolute(ProjectSettings.globalize_path(TOKEN_FILE))

# ── AES / UDP crypto ──────────────────────────────────────────────────────────
func _init_udp_crypto() -> void:
    var ctx := HashingContext.new()
    ctx.start(HashingContext.HASH_SHA256)
    ctx.update((_session_id + UDP_SHARED_SECRET).to_utf8_buffer())
    var hash: PackedByteArray = ctx.finish()   # 32 bytes
    _aes_key = hash.slice(0, 32)
    _aes_iv  = hash.slice(16, 32)
    _udp.close()
    _udp.connect_to_host(SERVER_HOST, UDP_PORT)

func _pkcs7_pad(data: PackedByteArray) -> PackedByteArray:
    var pad_len := 16 - (data.size() % 16)
    var out := data.duplicate()
    for i in range(pad_len):
        out.append(pad_len)
    return out

func _pkcs7_unpad(data: PackedByteArray) -> PackedByteArray:
    if data.is_empty():
        return data
    var pad_len := int(data[-1])
    if pad_len < 1 or pad_len > 16:
        return data
    return data.slice(0, data.size() - pad_len)

func _encrypt(plaintext: String) -> PackedByteArray:
    var padded := _pkcs7_pad(plaintext.to_utf8_buffer())
    var aes := AESContext.new()
    aes.start(AESContext.MODE_CBC_ENCRYPT, _aes_key, _aes_iv)
    var result: PackedByteArray = aes.update(padded)
    aes.finish()
    return result

func _decrypt(encrypted: PackedByteArray) -> String:
    var aes := AESContext.new()
    aes.start(AESContext.MODE_CBC_DECRYPT, _aes_key, _aes_iv)
    var result: PackedByteArray = aes.update(encrypted)
    aes.finish()
    return _pkcs7_unpad(result).get_string_from_utf8()

func _make_udp_packet(data: Dictionary) -> PackedByteArray:
    var encrypted := _encrypt(JSON.stringify(data))
    var pkt := PackedByteArray()
    pkt.resize(4)
    pkt.encode_s32(0, encrypted.size())
    pkt.append_array(encrypted)
    return pkt

func _parse_udp_packet(raw: PackedByteArray) -> Dictionary:
    if raw.size() < 4:
        return {}
    var length := raw.decode_s32(0)
    if length <= 0 or length != raw.size() - 4:
        return {}
    var json_str := _decrypt(raw.slice(4, 4 + length))
    if json_str.is_empty():
        return {}
    var parsed = JSON.parse_string(json_str)
    return parsed if parsed is Dictionary else {}

# ── UDP I/O ───────────────────────────────────────────────────────────────────
func _poll_udp() -> void:
    while _udp.get_available_packet_count() > 0:
        var msg := _parse_udp_packet(_udp.get_packet())
        if msg.is_empty():
            continue
        match msg.get("command", ""):
            "UPDATE":
                var sid: String = msg.get("sessionId", "")
                if sid == _session_id:
                    continue
                var p := msg["position"]
                var r := msg["rotation"]
                emit_signal("remote_position_updated",
                    sid,
                    Vector3(p["x"], p["y"], p["z"]),
                    Quaternion(r["x"], r["y"], r["z"], r["w"]))

func send_position(position: Vector3, rotation: Quaternion) -> void:
    if not _is_authenticated:
        return
    _udp.put_packet(_make_udp_packet({
        "command":   "UPDATE",
        "sessionId": _session_id,
        "position":  {"x": position.x, "y": position.y, "z": position.z},
        "rotation":  {"x": rotation.x, "y": rotation.y, "z": rotation.z, "w": rotation.w}
    }))

func send_input(input: Dictionary) -> void:
    if not _is_authenticated or _current_room_id.is_empty():
        return
    var data := {
        "command":   "INPUT",
        "sessionId": _session_id,
        "roomId":    _current_room_id,
        "input":     input
    }
    _udp.put_packet(_make_udp_packet(data))

# ── Room helpers ──────────────────────────────────────────────────────────────
func list_rooms() -> void:
    _send({"command": "LIST_ROOMS"})

func create_room(room_name: String, max_players: int = 20) -> void:
    _send({"command": "CREATE_ROOM", "name": room_name, "maxPlayers": max_players})

func join_room(room_id: String) -> void:
    _send({"command": "JOIN_ROOM", "roomId": room_id})

func leave_room() -> void:
    _send({"command": "LEAVE_ROOM"})

func get_room_players() -> void:
    _send({"command": "GET_ROOM_PLAYERS"})

func start_game() -> void:
    _send({"command": "START_GAME"})

func send_chat(message: String) -> void:
    _send({"command": "MESSAGE", "message": message})

# ── Heartbeat ─────────────────────────────────────────────────────────────────
func _heartbeat() -> void:
    if _session_id.is_empty():
        return
    if _is_authenticated:
        _send({"action": "heartbeat", "messageId": str(Time.get_ticks_msec())})
    else:
        _send({"command": "PING"})

# ── Disconnect ────────────────────────────────────────────────────────────────
func disconnect_from_server() -> void:
    if _tls != null:
        _send({"command": "BYE"})
        _tls.disconnect_from_stream()
    _udp.close()
    _is_authenticated = false
    _session_id = ""
    _current_room_id = ""
```

---

## Common Mistakes to Avoid

| Mistake | Correct Behaviour |
|---|---|
| Connecting to port 443 | Use port **7777** (TCP) and **7778** (UDP) |
| Parsing welcome as `"CONNECTED\|<id>"` | Welcome is JSON: `{"command":"CONNECTED","sessionId":"..."}` |
| Sending plain/unencrypted UDP | Server **rejects** all unencrypted UDP — always encrypt |
| Forgot to include `sessionId` in UDP payload | Include `"sessionId": _session_id` in every UDP packet |
| Hardcoding shared secret | Read it from config; it must match server's `appsettings.json SecurityConfig:UdpSharedSecret` |
| Using `NAME`+`AUTHENTICATE` commands | Those commands are **removed** — use `REGISTER` / `LOGIN` / `AUTO_AUTH` |
| Not sending heartbeat | Session drops after 60 s of TCP inactivity |
| Calling `START_GAME` as non-host | Only the host (room creator) can start the game |
| Sending `START_GAME` to wrong room | You must have joined/created a room first |
