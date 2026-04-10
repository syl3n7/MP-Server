
# MP-Server Refactor & Protocol Guide (Godot-Compatible, Authoritative, Research-Aligned)

## Overview
This guide consolidates the authentication refactor with your research requirements and class map. It defines the must-have changes for a robust, persistent, schema-driven, Godot-compatible multiplayer server, with full server authority and protocol clarity.

---

## Progress Tracker

### Generalisation & Godot Compatibility
- ✅ Removed hardcoded racing spawn positions from `GameRoom` → replaced with generic 0-based `spawnIndex` (client resolves world position)
- ✅ Removed hardcoded public IP (`89.114.116.19`) → configurable via `appsettings.json` `ServerSettings:PublicIP` or `SERVER_PUBLIC_IP` env var
- ✅ Welcome message: `CONNECTED|{id}` → `{"command":"CONNECTED","sessionId":"..."}` (plain JSON, Godot-friendly)
- ✅ UDP shared secret: `"RacingServerUDP2024!"` hardcoded in `UdpEncryption.cs` → configurable via `appsettings.json` `SecurityConfig:UdpSharedSecret`
- ✅ Renamed `RacingServer` → `GameServer` (`RacingServer.cs` → `GameServer.cs`); all type refs in `ConsoleUI`, `PlayerSession`, `Program` updated; cert hostname default `"racing-server"` → `"mp-server"`
- ✅ Generic room defaults: `"Race Room"` → `"Room"` in `GameRoom.cs` and `PlayerSession.cs`
- ✅ Envelope-based protocol: `action` field routing alongside legacy `command`; `messageId` idempotency (30s window); `heartbeat` → `HEARTBEAT_ACK`; `snapshot_sync` → `SNAPSHOT`; UDP accepts `action: "move"` (payload path) and `action: "input"` alongside legacy `command: "UPDATE"` / `"INPUT"`
- ✅ Configurable `MaxPlayers` per room: `CREATE_ROOM` now accepts optional `maxPlayers` field (default 20); `LIST_ROOMS` includes `maxPlayers` in each room entry
- ✅ Generalized `PacketValidator` input fields: `steering`/`throttle`/`brake` → generic `inputVector` object with `x`/`y`/`z` axes each clamped to `[-1, 1]`
- ✅ Layer file moves: `GameServer`/`PlayerSession` → `Transport/`; `GameRoom` → `Domain/`; namespaces added (`MP.Server.Transport`, `MP.Server.Domain`)
- ✅ Fixed `UdpPort` typo in `appsettings.json` (`77788` → `7778`)

### Authentication (completed prior to April 2026)
- ✅ DB-backed `AuthService` with BCrypt + persistent 30-day tokens
- ✅ `REGISTER`, `LOGIN`, `AUTO_AUTH` commands replace old `NAME+password` flow
- ✅ Account lockout (3 failed attempts → 30-min lock)
- ✅ All gameplay commands gated behind `IsAuthenticated`

### Pre-Phase-3 Cleanup (April 10, 2026)

| # | Priority | Task | File(s) | Status |
|---|----------|------|---------|--------|
| 1 | 🔴 Security | UDP `sessionId` cross-validation — verify payload `sessionId` matches the session used to decrypt the packet | `Transport/GameServer.cs` — `ProcessUdpPacketAsync` | 🔜 |
| 2 | 🟠 Tech debt | Wire up `SecurityManager` kick stub — `// TODO: Implement actual kick mechanism` | `Security/SecurityManager.cs:191` | 🔜 |
| 3 | 🟢 Structure | Move `ConsoleUI.cs` → `Observability/ConsoleUI.cs`, update namespace to `MP.Server.Observability`, fix usages in `Program.cs` | `ConsoleUI.cs`, `Program.cs` | 🔜 |
| 4 | 🟢 Docs | Update `CLIENT_IMPLEMENTATION_GUIDE.md` + `CLIENT_IMPLEMENTATION_REQUIREMENTS.md` — remove SHA-256 / `AUTHENTICATE` / first-come references, document current auth protocol | `CLIENT_IMPLEMENTATION_GUIDE.md`, `CLIENT_IMPLEMENTATION_REQUIREMENTS.md` | 🔜 |

---

## 1. Authoritative Server & State Integrity
- The server is the single source of truth for all world, player, and inventory state.
- All gameplay actions (move, inventory, kill, etc.) are validated and processed server-side.
- No client-side state changes are accepted without server validation.
- All action processing is deterministic and logged.
- No item duplication or race condition exploits (see conflict resolver).

## 2. Protocol: Envelope-Based, 2D/3D-Ready, Schema-Driven
- All messages use a single envelope structure (see research doc, section: Envelope).
- Payloads are always JSON, newline-delimited, UTF-8.
- All coordinate and transform payloads use 3D-compatible fields (z=0 for 2D).
- All requests, responses, and events are versioned and include messageId, sessionId, playerId, etc.
- See `/mp-paper/RESEARCH_PROGRESS.md` and `/mp-paper/documentation/custom-server-class-map.md` for schema and DTOs.

## 3. Persistent, Secure, Token-Based Authentication
- Remove all in-memory password maps and legacy auth logic.
- Use the `Users` table for all registration and login.
- Implement an `AuthService` for registration, login, password hashing (PBKDF2/bcrypt/Argon2), and token management.
- On login, generate a secure random token, store its hash in a new `UserAuthTokens` table, and return the token to the client.
- On reconnect, client sends the token in an `AUTO_AUTH` command; server validates and authenticates automatically.
- All gameplay actions require a valid, persistent session (except REGISTER, LOGIN, AUTO_AUTH, PING, LIST_ROOMS).

## 4. Protocol Commands (Godot-Compatible)
- `REGISTER`: `{ command: "REGISTER", username, password, email }` → `{ command: "REGISTER_OK", userId }`
- `LOGIN`: `{ command: "LOGIN", username, password }` → `{ command: "LOGIN_OK", userId, token }`
- `AUTO_AUTH`: `{ command: "AUTO_AUTH", token }` → `{ command: "AUTO_AUTH_OK", userId }`
- `NAME`: For display name only (not authentication).
- All gameplay, chat, and room commands require authentication.
- All responses are JSON objects with a `command` field and clear error reporting.

## 5. Gameplay Action Handling (Research Must-Haves)
- Implement all core actions: move, inventory_grab, inventory_drop, inventory_move_slot, player_kill (optional).
- Use the envelope and payload schemas from your research doc.
- All actions are requests; server validates, applies, and responds with result, reasonCode, and stateRevision.
- All state changes are published as events (inventory_changed, ground_item_spawned, etc.).
- All action processing is idempotent (duplicate messageId is ignored within a window).
- All validation and rejection reasons are explicit and logged.

## 6. DTOs, Handlers, and Validation (Class Map Alignment)
- Implement DTOs, handlers, and validation logic as per `/mp-paper/documentation/custom-server-class-map.md`.
- Use the recommended enums, domain models, and service abstractions.
- Implement conflict resolution for inventory and world actions.
- Implement idempotency and tick-based ordering for all actions.

## 7. UDP/Transport Security
- For UDP, ensure the sessionId in the payload matches the authenticated session.
- Reject any packet where the sender’s sessionId does not match the authenticated session.
- All transport logic should be abstracted for future protocol swaps (see ITransportServer).

## 8. Clean Up and Refactor
- Remove all legacy/in-memory auth code and first-come username logic.
- Remove or refactor any code that is not schema-driven or envelope-based.
- Update all tests and documentation to match the new protocol and research requirements.

---

## 9. Layer Separation (Clean Architecture)

Each layer has a single responsibility and only depends on layers below it. No layer imports from a higher layer.

### Layer Map

```
Models/          ← Domain entities, DTOs, enums  (no project dependencies)
Data/            ← EF DbContext, migrations       (depends on: Models)
Services/        ← Business logic, AuthService    (depends on: Data, Models)
Security/        ← Rate limiting, anti-cheat      (depends on: Models)
Protocol/        ← Command routing, handlers      (depends on: Services, Models)
Transport/       ← TCP/UDP I/O, TLS, PlayerSession (depends on: Protocol, Security)
Observability/   ← Logging, metrics               (cross-cutting, used by any layer)
```

### Dependency Rules
- **Models/** — no imports from any other server layer.
- **Data/** — imports only Models and EF packages.
- **Services/** — imports Data and Models. No socket or session references.
- **Security/** — imports Models only. No DB calls in hot packet path.
- **Protocol/** — imports Services and Models. Handles command dispatch and payload parsing.
- **Transport/** (`GameServer`, `PlayerSession`) — imports Protocol and Security. Drives I/O only — no business logic.
- **Observability/** — cross-cutting. May be referenced by any layer.

### File Moves Required

| Current location | Target layer | Status |
|---|---|---|
| `Models/User.cs` | `Models/` | ✅ Already correct |
| `Models/PlayerInfo.cs` | `Models/` | ✅ Already correct |
| `Models/LogModels.cs` | `Models/` | ✅ Already correct |
| `Data/UserDbContext.cs` | `Data/` | ✅ Already correct |
| `Services/AuthService.cs` | `Services/` | 🆕 New file this iteration |
| `Services/DatabaseLoggingService.cs` | `Services/` | ✅ Already correct |
| `Services/LogCleanupService.cs` | `Services/` | ✅ Already correct |
| `Security/SecurityManager.cs` | `Security/` | ✅ Already correct |
| `Security/PacketValidator.cs` | `Security/` | ✅ Already correct |
| `Security/RateLimiter.cs` | `Security/` | ✅ Already correct |
| `Security/UdpEncryption.cs` | `Security/` | ✅ Already correct |
| `PlayerSession.cs` | `Transport/` | 🔜 Future move |
| `GameServer.cs` | `Transport/` | 🔜 Future move |
| `GameRoom.cs` | `Domain/` | 🔜 Future move |
| `ConsoleUI.cs` | `Observability/` | 🔜 Future move |

> Physical file/folder moves and namespace updates are a separate structural pass. This iteration enforces correct dependency direction within the existing file layout.

---

## Example Protocol Messages

### Envelope (all messages)
```json
{
	"schemaVersion": 1,
	"messageId": "uuid",
	"timestampMs": 1711700000000,
	"sessionId": "string",
	"matchId": "string",
	"playerId": "string",
	"transport": "enet|custom",
	"messageType": "request|response|event",
	"action": "move|inventory_grab|inventory_drop|inventory_move_slot|player_kill|snapshot_sync|heartbeat",
	"payload": {},
	"ackFor": "uuid-or-null"
}
```

### Register
Request:
```json
{ "command": "REGISTER", "username": "player1", "password": "hunter2", "email": "p1@example.com" }
```
Response:
```json
{ "command": "REGISTER_OK", "userId": 123 }
```

### Login
Request:
```json
{ "command": "LOGIN", "username": "player1", "password": "hunter2" }
```
Response:
```json
{ "command": "LOGIN_OK", "userId": 123, "token": "..." }
```

### Auto-Auth
Request:
```json
{ "command": "AUTO_AUTH", "token": "..." }
```
Response:
```json
{ "command": "AUTO_AUTH_OK", "userId": 123 }
```

### Gameplay Action (Move Example)
Request:
```json
{
	"schemaVersion": 1,
	"messageId": "uuid",
	"timestampMs": 1711700000000,
	"sessionId": "string",
	"playerId": "string",
	"messageType": "request",
	"action": "move",
	"payload": {
		"inputVector": { "x": 1.0, "y": 0.0, "z": 0.0 },
		"position": { "x": 24.0, "y": 9.5, "z": 0.0 },
		"tick": 10234
	}
}
```
Response:
```json
{
	"result": "ok|rejected|error",
	"reasonCode": "none|out_of_range|invalid_slot|item_not_found|conflict|rate_limited|invalid_state",
	"serverTick": 20500,
	"latencyMs": 42,
	"stateRevision": 9912,
	"payload": {}
}
```

---

## Next Steps
1. Implement all changes above, using the class map and research doc as the source of truth for DTOs, handlers, and protocol.
2. Remove all legacy/in-memory authentication and non-envelope protocol logic.
3. Test with Godot clients and synthetic players for compatibility and authority.
4. Update all documentation and tests to match the new protocol and requirements.

---

This guide is the unified foundation for the next server iteration. After these changes, further adjustments can be made for Godot/game-specific or research-driven needs.
