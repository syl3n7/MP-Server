# Phase 3 — Concerns to Address

Findings from Godot compatibility audit (April 10, 2026).
All items below were verified against the codebase post-phase-2 refactor.

---

## 🐛 Bugs (Fixed)

| # | File | Issue |
|---|------|-------|
| ✅ | `appsettings.json` | `UdpPort` was `77788` (5 digits) → fixed to `7778` |

---

## 🔴 Critical — Protocol Gaps

Without these, a Godot client cannot implement basic multiplayer awareness.

### 1. No `PLAYER_JOINED` broadcast
**File:** `Transport/PlayerSession.cs` — `JOIN_ROOM` handler  
When a player joins a room, other members in that room receive no notification.  
**Fix needed:** After a successful join, broadcast `{"command":"PLAYER_JOINED","playerId":"...","playerName":"..."}` to all other room members.

### 2. No `PLAYER_LEFT` / `PLAYER_DISCONNECTED` broadcast
**File:** `Transport/GameServer.cs` / `Transport/PlayerSession.cs` — disconnect/cleanup path  
When a player disconnects or leaves a room, no message is sent to remaining room members.  
**Fix needed:** On session cleanup, broadcast `{"command":"PLAYER_LEFT","playerId":"..."}` to all players in the same room.

### 3. `JOIN_OK` returns only `roomId` — no current player list
**File:** `Transport/PlayerSession.cs` — `JOIN_ROOM` handler  
The joining client learns it joined but gets no information about who is already in the room.  
**Fix needed:** Include `players` array (id + name) in the `JOIN_OK` response, or add a `spawnSlot` field.

### 4. Spawn slot assigned server-side but never communicated to client
**File:** `Domain/GameRoom.cs` — `GetPlayerSpawnIndex()`  
Server tracks a 0-based slot per player, but this value is never sent to the client. The comment says "client resolves world position from its own scene" but it never learns which slot it occupies.  
**Fix needed:** Include `spawnSlot` in `JOIN_OK` response (and in `PLAYER_JOINED` broadcast for other clients).

### 5. `START_GAME` command is not broadcast to room members
**File:** `Transport/PlayerSession.cs` — `START_GAME` handler  
Only the caller knows the game started. All other players in the room are unaware.  
**Fix needed:** Broadcast `{"command":"GAME_STARTED","roomId":"...","startedBy":"..."}` to all room members.

### 6. No `LEAVE_ROOM` command
**File:** `Transport/PlayerSession.cs`  
There is no explicit leave mechanism. Rooms can only be vacated by disconnecting entirely. Rooms accumulate empty entries on a long-running server.  
**Fix needed:** Add a `LEAVE_ROOM` command that removes the player from the room and triggers a `PLAYER_LEFT` broadcast.

---

## 🟡 High — Physics Validator Will Reject Valid Movement

These constants were calibrated for a racing game. Any game with faster movement, larger maps, or teleportation will have packets silently dropped.

### 7. Physics constants hardcoded for racing game
**File:** `Security/PacketValidator.cs` — lines ~17–21
```csharp
private const float MAX_POSITION_JUMP = 50.0f;   // too low for many games
private const float MAX_SPEED = 200.0f;           // units/second
private const float MAX_ANGULAR_VELOCITY = 10.0f; // radians/second
private const float MIN_UPDATE_INTERVAL = 0.008f; // 125 FPS cap
private const float MAX_UPDATE_INTERVAL = 5.0f;
```
**Fix needed:** Move these to `appsettings.json` under a `ValidationConfig` section. Default to permissive values and let the operator tighten them.

### 8. World boundary limits hardcoded with racing-track comment
**File:** `Security/PacketValidator.cs` — `IsValidPosition()` method
```csharp
const float MAX_X = 1000f;  const float MIN_X = -1000f;
const float MAX_Y = 100f;   const float MIN_Y = -100f;   // ← racing-track height
const float MAX_Z = 1000f;  const float MIN_Z = -1000f;
// Comment: "adjust as needed for your track"
```
**Fix needed:** Move to `appsettings.json` under `ValidationConfig:WorldBounds`. Remove racing-track comment.

---

## 🟠 Medium — Hardcoded Security / Auth Values

### 9. TLS certificate password hardcoded
**File:** `Transport/GameServer.cs` — `GenerateOrLoadCertificate()`
```csharp
string certPassword = "MPServer2024!";
```
**Fix needed:** Move to `appsettings.json` `ServerSettings:CertPassword` (with env var override).

### 10. Auth policy constants are not configurable
**File:** `Services/AuthService.cs` — top of class
```csharp
private const int MaxFailedAttempts = 3;
private const int LockoutMinutes = 30;
private const int TokenExpiryDays = 30;
private const int MinUsernameLength = 3;
private const int MaxUsernameLength = 50;
private const int MinPasswordLength = 6;
```
**Fix needed:** Move to `appsettings.json` under `AuthConfig` section.

### 11. No TCP command rate limiting
**File:** `Transport/PlayerSession.cs`  
UDP packets pass through `SecurityManager.ValidateUdpPacket()` (rate-limited), but TCP commands have no equivalent rate limiting. A client can spam commands freely.  
**Fix needed:** Apply `RateLimiter` to the TCP command processing loop, or add a per-session TCP rate limit check in `ProcessMessageAsync`.

---

## 🟢 Low / Future

### 12. No `END_GAME` command
`START_GAME` is handled but there is no corresponding `END_GAME`. Game state can start but never officially end from the server's perspective.

### 13. Message deduplication (messageId) only covers TCP
UDP has no idempotency protection. Move/input packets could theoretically be processed twice on retransmission, though UDP-level replay is less common with AES-CBC.

### 14. No WebSocket transport
Godot HTML5/web exports cannot use raw TCP/UDP sockets — they require WebSocket. A `WebSocketTransport` layer would be needed to support web builds. This is a larger refactor.

### 15. No `END_GAME` broadcast / room reset
Related to #12: after a game ends, rooms should return to a "waiting" state and broadcast a `GAME_ENDED` message. Currently there is no room state machine.

### 16. Empty rooms not garbage collected
**File:** `Transport/GameServer.cs` — room storage  
Rooms created with `CREATE_ROOM` are never deleted. Long-running servers accumulate stale empty rooms. Needs a cleanup job (e.g., remove rooms idle for >1 hour with 0 players).

### 17. Player name accepts unsanitized input
**File:** `Transport/PlayerSession.cs` — `NAME` command handler  
No validation on Unicode control characters, zero-width spaces, or excessively long names that could break downstream client UIs.

---

## Summary Table

| Priority | # | Description | Status |
|----------|---|-------------|--------|
| Bug | — | `UdpPort` typo | ✅ Fixed |
| 🔴 Critical | 1 | `PLAYER_JOINED` broadcast | 🔜 |
| 🔴 Critical | 2 | `PLAYER_LEFT` broadcast | 🔜 |
| 🔴 Critical | 3 | `JOIN_OK` returns current player list + spawnSlot | 🔜 |
| 🔴 Critical | 4 | Spawn slot sent to client | 🔜 (part of #3) |
| 🔴 Critical | 5 | `START_GAME` broadcast to room | 🔜 |
| 🔴 Critical | 6 | `LEAVE_ROOM` command | 🔜 |
| 🟡 High | 7 | Physics constants → `appsettings.json` | 🔜 |
| 🟡 High | 8 | World bounds → `appsettings.json` | 🔜 |
| 🟠 Medium | 9 | Cert password → config | 🔜 |
| 🟠 Medium | 10 | Auth policy constants → config | 🔜 |
| 🟠 Medium | 11 | TCP rate limiting | 🔜 |
| 🟢 Low | 12 | `END_GAME` command + room reset | 🔜 |
| 🟢 Low | 13 | UDP messageId idempotency | 🔜 |
| 🟢 Low | 14 | WebSocket transport for web builds | 🔜 |
| 🟢 Low | 15 | Room state machine (waiting → playing → ended) | 🔜 |
| 🟢 Low | 16 | Empty room garbage collection | 🔜 |
| 🟢 Low | 17 | Player name sanitization | 🔜 |
