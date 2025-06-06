# MP-Server Protocol Documentation

## 1. Overview
MP-Server is a high-performance, secure TCP/UDP multiplayer racing game server with enterprise-grade security features.  
Clients connect over TLS-encrypted TCP (for commands, room management, chat) and send/receive AES-encrypted UDP packets (for real‐time position updates).

**Latest Updates (June 2025):**
- ✅ **SECURITY ENHANCED**: Comprehensive packet validation and anti-cheat system
- ✅ **PERFORMANCE**: Advanced rate limiting and DDoS protection
- ✅ **MONITORING**: Real-time web dashboard with security analytics
- ✅ **CRITICAL FIX**: Resolved server-side UDP encryption bug causing JsonReaderException errors
- ✅ **FIXED**: Race condition in spawn position assignment during game start
- ✅ **ENHANCED**: Server now properly handles both encrypted and plain UDP packets
- ✅ **IMPROVED**: Comprehensive error handling for malformed/encrypted UDP data

Ports (defaults):
- **TCP: 443** (TLS/SSL encrypted) - Uses standard HTTPS port for firewall traversal
- **UDP: 443** (AES-256-CBC encrypted for authenticated users) - Same port as TCP 
- **Dashboard Web UI: 8080** - Real-time monitoring and administration

## 1.1 Security Features
- **TLS 1.2/1.3 TCP encryption**: All command traffic is encrypted using modern TLS
- **AES-256-CBC UDP encryption**: Position and input data encrypted with session-specific keys
- **Advanced packet validation**: Physics-based validation to prevent cheating
  - Position boundary checking and physics constraint validation
  - Input range validation for steering, throttle, and brake controls
  - Timestamp validation to prevent replay attacks
  - Maximum speed and angular velocity enforcement
- **Sophisticated rate limiting**: Sliding window rate limiting with burst protection
  - TCP: 10 messages/second per client (configurable)
  - UDP: 60 packets/second per client (configurable) 
  - Automatic cleanup of inactive client data
  - Real-time utilization monitoring
- **Multi-tier anti-cheat system**: Real-time threat assessment and violation tracking
  - Automatic violation counting with configurable thresholds
  - 4-level threat assessment (0-3 scale)
  - Configurable auto-kick for repeat offenders
  - Security event correlation and pattern detection
- **Comprehensive security monitoring**: Real-time dashboard with analytics
  - Live security event monitoring with severity levels
  - Player threat level tracking and visualization
  - Rate limit utilization monitoring per client
  - Security statistics with event type breakdown
- **Player authentication**: SHA-256 password hashing with session management
- **Session isolation**: Each player gets unique encryption keys and validation state
- **Administrative controls**: Web-based security management interface
  - Player banning with customizable reasons
  - Real-time disconnect capabilities
  - Security event analysis and filtering
- **Self-signed certificate generation**: Automatic certificate creation with public IP support
- **Hybrid UDP support**: Server handles both encrypted (authenticated) and plain (legacy) UDP packets
- **Security event logging**: Comprehensive logging and threat level assessment with configurable retention

## 1.2 Recent Critical Fixes

### 1.2.1 UDP Encryption Server-Side Bug (RESOLVED)
**Issue**: Server was not properly handling UDP packet decryption, causing JsonReaderException errors with '0xEF' invalid start characters when receiving encrypted UDP packets from authenticated clients.

**Root Cause**: The `ProcessUdpPacketAsync` method in `RacingServer.cs` was attempting to parse encrypted binary data as plain JSON strings.

**Solution**: Complete rewrite of UDP packet processing to:
- Attempt decryption using each authenticated session's UdpCrypto
- Gracefully fallback to plain JSON parsing for unauthenticated clients
- Prevent crashes when receiving encrypted data
- Maintain backward compatibility

**Status**: ✅ **RESOLVED** - Server now properly handles all UDP packet types

### 1.2.2 Spawn Position Race Condition (RESOLVED)
**Issue**: Players occasionally received "Spawn position data missing in game start message!" errors when starting games.

**Root Cause**: Race condition in `GameRoom.cs` `TryAddPlayer` method where spawn positions were assigned before players were fully added to the collection.

**Solution**: Modified spawn position assignment to occur AFTER successful player addition.

**Status**: ✅ **RESOLVED** - All players now receive proper spawn positions

## 2. Prerequisites
- .NET 9.0 runtime
- A TLS-capable TCP socket library
- A UDP socket library for state updates  
- UTF-8 support for text commands
- JSON parser (server uses System.Text.Json for all command processing)
- AES encryption support for UDP packets (authenticated users only)

## 3. TCP Protocol

### 3.1 Connection & Framing
1. Client opens a **TLS-encrypted** TCP connection to server:  
   ```csharp
   var client = new TcpClient();
   await client.ConnectAsync("server.address", 443);
   var sslStream = new SslStream(client.GetStream());
   await sslStream.AuthenticateAsClientAsync("server.address");
   ```
2. Server immediately responds with a welcome message terminated by `\n`:  
   ```
   CONNECTED|<sessionId>\n
   ```
3. All subsequent messages are **newline‐delimited JSON**:
   ```
   {"command":"COMMAND_NAME","param1":"value1","param2":"value2"}\n
   ```

**Important**: The server automatically generates and uses a self-signed certificate. For production use, clients should either:
- Accept self-signed certificates (for local/LAN use)
- Provide a proper CA-signed certificate to the server
- Implement certificate validation callbacks

### 3.2 Authentication
The server supports a simple authentication system to protect player identities:

1. **During Registration** (first time using a username):
   - When setting a player name for the first time, a password can be provided
   - Example: `{"command":"NAME","name":"playerName","password":"secretPassword"}`

2. **During Login** (using an existing username):
   - When connecting with a previously used name, password verification is required
   - Example: `{"command":"NAME","name":"playerName","password":"secretPassword"}`

3. **Separate Authentication**:
   - Players can also set their name first, then authenticate separately
   - Example: `{"command":"AUTHENTICATE","password":"secretPassword"}`

4. **Command Restrictions**:
   - Unauthenticated players can only use: NAME, AUTHENTICATE, PING, BYE, PLAYER_INFO, LIST_ROOMS
   - All other commands require authentication
   - Attempting restricted commands without auth returns an error

5. **UDP Encryption Setup**:
   - Once authenticated via TCP, players receive unique UDP encryption keys
   - UDP packets from authenticated players are automatically encrypted with AES-256
   - The server can handle both encrypted and plain-text UDP packets for backward compatibility

### 3.3 Supported Commands

| Command        | Direction    | Payload (JSON)                                    | Response (JSON)                         | Requires Auth |
| -------------- | ------------ | ------------------------------------------------- | --------------------------------------- | ------------- |
| `NAME`         | Client → Srv | `{"command":"NAME","name":"playerName","password":"secret"}` | `{"command":"NAME_OK","name":"playerName","authenticated":true,"udpEncryption":true}` or `{"command":"AUTH_FAILED","message":"Invalid password for this player name."}` | No |
| `AUTHENTICATE` | Client → Srv | `{"command":"AUTHENTICATE","password":"secret"}` | `{"command":"AUTH_OK","name":"playerName"}` or `{"command":"AUTH_FAILED","message":"Invalid password."}` | No |
| `CREATE_ROOM`  | Client → Srv | `{"command":"CREATE_ROOM","name":"roomName"}`   | `{"command":"ROOM_CREATED","roomId":"id","name":"roomName"}` | Yes |
| `JOIN_ROOM`    | Client → Srv | `{"command":"JOIN_ROOM","roomId":"id"}`         | `{"command":"JOIN_OK","roomId":"id"}` or `{"command":"ERROR","message":"Failed to join room. Room may be full or inactive."}` | Yes |
| `LEAVE_ROOM`   | Client → Srv | `{"command":"LEAVE_ROOM"}`                      | `{"command":"LEAVE_OK","roomId":"id"}` or `{"command":"ERROR","message":"Cannot leave room. No room joined."}` | Yes |
| `PING`         | Client → Srv | `{"command":"PING"}`                            | `{"command":"PONG"}`                  | No |
| `LIST_ROOMS`   | Client → Srv | `{"command":"LIST_ROOMS"}`                     | `{"command":"ROOM_LIST","rooms":[{"id":"id","name":"roomName","playerCount":0,"isActive":false,"hostId":"hostId"}]}` | No |
| `GET_ROOM_PLAYERS` | Client → Srv | `{"command":"GET_ROOM_PLAYERS"}`          | `{"command":"ROOM_PLAYERS","roomId":"id","players":[{"id":"playerId","name":"playerName"}]}` or `{"command":"ERROR","message":"Cannot get players. No room joined."}` | Yes |
| `RELAY_MESSAGE` | Client → Srv | `{"command":"RELAY_MESSAGE","targetId":"playerId","message":"text"}` | `{"command":"RELAY_OK","targetId":"playerId"}` or `{"command":"ERROR","message":"Target player not found."}` | Yes |
| `PLAYER_INFO`  | Client → Srv | `{"command":"PLAYER_INFO"}`                    | `{"command":"PLAYER_INFO","playerInfo":{"id":"id","name":"playerName","currentRoomId":"roomId"}}` | No |
| `START_GAME`   | Client → Srv | `{"command":"START_GAME"}`                     | `{"command":"GAME_STARTED","roomId":"roomId","spawnPositions":{"playerId1":{"x":66,"y":-2,"z":0.8},"playerId2":{"x":60,"y":-2,"z":0.8}}}` or `{"command":"ERROR","message":"Cannot start game. Only the host can start the game."}` | Yes |
| `BYE`          | Client → Srv | `{"command":"BYE"}`                            | `{"command":"BYE_OK"}` | No |
| Any other      | Client → Srv | e.g. `{"command":"FOO"}`                        | `{"command":"UNKNOWN_COMMAND","originalCommand":"FOO"}` | - |

#### Server-to-Client Messages
The server may also send these messages without a direct client request:

| Message | Purpose | Format |
| ------- | ------- | ------ |
| `RELAYED_MESSAGE` | Message relayed from another player | `{"command":"RELAYED_MESSAGE","senderId":"id","senderName":"name","message":"text"}` |
| `GAME_STARTED` | Notification that a game has started | `{"command":"GAME_STARTED","roomId":"roomId","hostId":"hostId","spawnPositions":{"playerId1":{"x":66,"y":-2,"z":0.8},"playerId2":{"x":60,"y":-2,"z":0.8}}}` |
| `AUTH_FAILED` | Authentication failure notification | `{"command":"AUTH_FAILED","message":"Invalid password for this player name."}` |

#### Error Handling
- Malformed JSON commands return `{"command":"ERROR","message":"Invalid JSON format"}`.
- Unrecognized commands return `{"command":"UNKNOWN_COMMAND","originalCommand":"cmd"}`.
- Authentication errors return `{"command":"ERROR","message":"Authentication required. Please use NAME command with password."}` or `{"command":"AUTH_FAILED","message":"Invalid password."}`.
- If server detects inactivity (>60 s without messages), it will close the TCP socket.
- Common error messages include:
  - `"Room not found."`
  - `"Cannot start game. No room joined."`
  - `"Cannot start game. Room not found."`
  - `"Cannot start game. You are not in this room."`
  - `"Cannot start game. Only the host can start the game."`
  - `"Cannot leave room. No room joined."`
  - `"Cannot leave room. Room not found."`
  - `"Cannot leave room. You are not in this room."`
  - `"Invalid message relay request. Missing target or message."`
  - `"Target player not found."`

## 4. UDP Protocol

### 4.1 Purpose
The UDP protocol provides low-latency communication for:
- Position and rotation updates
- Input control commands (steering, throttle, brake)
- Game state synchronization between clients

All UDP communication happens after the client has joined or created a room via TCP.

### 4.2 Encryption (Updated with Recent Fixes)
**For Authenticated Players:**
- UDP packets are automatically encrypted using AES-256-CBC
- Each session gets unique encryption keys derived from the session ID
- Packet format: `[4-byte length header][encrypted JSON data]`
- **FIXED**: Server now properly handles encryption/decryption (was broken previously)
- Server automatically detects encrypted packets and decrypts them correctly

**For Unauthenticated Players:**
- UDP packets are sent as plain-text JSON (backward compatibility)
- Limited functionality compared to authenticated users

**Server-Side UDP Processing (Fixed):**
- Server attempts decryption with each authenticated session's keys
- Gracefully falls back to plain JSON parsing if decryption fails
- No more JsonReaderException errors from encrypted packets
- Supports hybrid environments with both encrypted and plain clients

### 4.3 Packet Format (JSON-based)
The server supports two primary types of UDP packets, both based on JSON:

#### Position Updates
```json
{
  "command": "UPDATE",
  "sessionId": "id",
  "position": {"x": 0.0, "y": 0.0, "z": 0.0},
  "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
}
```

#### Input Controls
```json
{
  "command": "INPUT",
  "sessionId": "id",
  "roomId": "roomId",
  "input": {
    "steering": 0.0,
    "throttle": 0.0,
    "brake": 0.0,
    "timestamp": 123.456
  },
  "client_id": "id"
}
```

### 4.3 Required Fields

#### For Position Updates:
- `command`: Must be "UPDATE"
- `sessionId`: Your TCP session ID (received during connection)
- `position`: A Vector3 object with:
  - `x`: X coordinate (float)
  - `y`: Y coordinate (float)
  - `z`: Z coordinate (float)
- `rotation`: A Quaternion object with:
  - `x`: X component (float)
  - `y`: Y component (float)
  - `z`: Z component (float)
  - `w`: W component (float, default 1.0)

#### For Input Controls:
- `command`: Must be "INPUT"
- `sessionId`: Your TCP session ID (received during connection)
- `roomId`: The ID of the room you're currently in
- `input`: An object containing control values:
  - `steering`: Steering input (-1.0 to 1.0, where -1 is full left, 1 is full right)
  - `throttle`: Acceleration input (0.0 to 1.0)
  - `brake`: Brake input (0.0 to 1.0)
  - `timestamp`: Game time in seconds when this input was recorded
- `client_id`: Your client ID (should match sessionId)

### 4.4 UDP Communication Flow

1. **Endpoint Registration**:
   - The server automatically associates a client's UDP endpoint (IP:port) with their session ID
   - This mapping happens when the first UDP packet is received from a client
   - Each player must send at least one UDP packet to register their endpoint

2. **Broadcasting**:
   - Position updates are sent only to players in the same room
   - Input commands are broadcast to all clients in the room except the sender
   - The exact format of received messages matches what was sent (preserving all fields)
   - Players without a registered UDP endpoint won't receive any broadcasts

3. **Reliability**:
   - UDP does not guarantee delivery - clients should implement their own reliability mechanisms
   - High-frequency updates (like position) should be sent periodically regardless of changes
   - Input commands should be sent when input values change or at a consistent rate

### 4.5 Example (C# send with encryption)
```csharp
// For authenticated players - packets are automatically encrypted
using var udp = new UdpClient();
var posUpdate = new { 
    command = "UPDATE", 
    sessionId = sessionId, 
    position = new { x = posX, y = posY, z = posZ },
    rotation = new { x = rotX, y = rotY, z = rotZ, w = rotW }
};

// If you have UDP encryption enabled (authenticated player)
if (udpCrypto != null)
{
    var encryptedPacket = udpCrypto.CreatePacket(posUpdate);
    await udp.SendAsync(encryptedPacket, encryptedPacket.Length, serverHost, 443);
}
else
{
    // Fallback to plain text for unauthenticated users
    var json = JsonSerializer.Serialize(posUpdate) + "\n";
    var bytes = Encoding.UTF8.GetBytes(json);
    await udp.SendAsync(bytes, bytes.Length, serverHost, 8443);
}
```

### 4.6 Example (C# receive with decryption)
```csharp
using var udpClient = new UdpClient(localPort); // Local port to listen on
var endpoint = new IPEndPoint(IPAddress.Any, 0);

while (true)
{
    var result = await udpClient.ReceiveAsync();
    var packetData = result.Buffer;
    
    JsonElement update;
    
    // Try to decrypt if this looks like an encrypted packet
    if (packetData.Length >= 4 && udpCrypto != null)
    {
        var parsedData = udpCrypto.ParsePacket<JsonElement>(packetData);
        if (parsedData.ValueKind != JsonValueKind.Undefined)
        {
            update = parsedData;
        }
        else
        {
            // Fallback to plain text parsing
            var json = Encoding.UTF8.GetString(packetData);
            update = JsonSerializer.Deserialize<JsonElement>(json);
        }
    }
    else
    {
        // Plain text packet
        var json = Encoding.UTF8.GetString(packetData);
        update = JsonSerializer.Deserialize<JsonElement>(json);
    }
    
    // Process by command type
    if (update.TryGetProperty("command", out var cmdElement))
    {
        string command = cmdElement.GetString();
        
        if (command == "UPDATE")
        {
            // Process position update (same as before)
            var sessionId = update.GetProperty("sessionId").GetString();
            var position = update.GetProperty("position");
            var rotation = update.GetProperty("rotation");
            
            float posX = position.GetProperty("x").GetSingle();
            float posY = position.GetProperty("y").GetSingle();
            float posZ = position.GetProperty("z").GetSingle();
            
            float rotX = rotation.GetProperty("x").GetSingle();
            float rotY = rotation.GetProperty("y").GetSingle();
            float rotZ = rotation.GetProperty("z").GetSingle();
            float rotW = rotation.GetProperty("w").GetSingle();
            
            // Use position and rotation to update game state
            // ...
        }
        else if (command == "INPUT")
        {
            // Process input update (same as before)
            var sessionId = update.GetProperty("sessionId").GetString();
            var input = update.GetProperty("input");
            
            float steering = input.GetProperty("steering").GetSingle();
            float throttle = input.GetProperty("throttle").GetSingle();
            float brake = input.GetProperty("brake").GetSingle();
            float timestamp = input.GetProperty("timestamp").GetSingle();
            
            // Apply input to the relevant player's vehicle
            // ...
        }
    }
}
```

### 4.7 Best Practices
- Send position updates at a consistent rate (10-30 Hz recommended)
- Keep UDP packet size under 1200 bytes to avoid fragmentation
- Handle networking jitter with client-side interpolation
- Include timestamps in input commands to help with lag compensation
- Apply basic smoothing or prediction for missing updates

## 5. Room Management

### 5.1 Room Properties
- Each room has a unique ID, name, and a host player
- Rooms have a maximum player limit (default: 20)
- Rooms can be active (game started) or inactive (lobby)
- Creation timestamp is recorded
- When the host leaves a room:
  - If the room is empty, it is automatically removed
  - If other players are present, host status is transferred to another player

### 5.2 Room Operations
- Create room: A player can create a new room and becomes its host
- Join room: Players can join rooms that are not active and not full
- Start game: Only the host can start the game, which marks the room as active
- List rooms: Get all available rooms with their basic information
- Get room players: Get the list of players in the current room

## 6. Example TCP Client (C# with TLS)

```csharp
using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;

class RacingClient
{
    private TcpClient _tcp;
    private SslStream _sslStream;
    private string _sessionId;

    public async Task RunAsync(string host, int port)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);
        
        // Setup TLS connection
        _sslStream = new SslStream(_tcp.GetStream(), false, ValidateServerCertificate);
        await _sslStream.AuthenticateAsClientAsync(host);

        // Read welcome
        var reader = new StreamReader(_sslStream, Encoding.UTF8);
        var welcome = await reader.ReadLineAsync();
        Console.WriteLine(welcome); // "CONNECTED|<sessionId>"
        _sessionId = welcome.Split('|')[1];

        // Set name with password
        await SendJsonAsync(new { command = "NAME", name = "Speedy", password = "secret123" });
        var response = await reader.ReadLineAsync();
        Console.WriteLine(response); // {"command":"NAME_OK","name":"Speedy","authenticated":true,"udpEncryption":true}

        // Create a room
        await SendJsonAsync(new { command = "CREATE_ROOM", name = "FastTrack" });
        response = await reader.ReadLineAsync();
        Console.WriteLine(response); // {"command":"ROOM_CREATED","roomId":"<id>","name":"FastTrack"}

        // ... then start your UDP updates with encryption …
    }

    private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // For development/LAN use, you might accept self-signed certificates
        if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors)
            return true; // Accept self-signed certificates

        return sslPolicyErrors == SslPolicyErrors.None;
    }

    private async Task SendJsonAsync<T>(T data)
    {
        var json = JsonSerializer.Serialize(data) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sslStream.WriteAsync(bytes, 0, bytes.Length);
    }
}
```

## 7. Player Session Management

### 7.1 Session Lifecycle
- Sessions are created when a client connects via TCP
- Each session has a unique ID that's shared with the client
- Inactivity timeout: Sessions with no activity for > 60 seconds are disconnected
- Sessions track player name, authentication status, current room, and last activity time

### 7.2 Authentication Process
1. **Initial Connection**: A player connects and receives a session ID
2. **Name Registration**: 
   - New players set their name and password using the NAME command
   - The server stores a hash of the password for future verification
3. **Authentication**:
   - When a player reconnects with a previously used name, they must provide the correct password
   - Authentication can happen in the NAME command or separately with AUTHENTICATE
   - Successfully authenticated players can access all game features
   - Unauthenticated players can only use basic commands like listing rooms

### 7.3 Player Information Structure
The server maintains player information using the `PlayerInfo` record with the following properties:
- `Id`: Unique player identifier (matches session ID)
- `Name`: Display name of the player
- `UdpEndpoint`: The IP:Port combination for UDP communication
- `Position`: Player's 3D position in the game world as a Vector3
- `Rotation`: Player's orientation in the game world as a Quaternion

This structure is used to track player state and share it with other players in the same room.

### 7.4 Spawn Positions and Game Start (Updated - Bug Fixed)
When a host starts a game:
1. The server assigns a spawn position to each player in the room
2. **FIXED**: Race condition in spawn position assignment has been resolved
3. The spawn positions are sent to all players as part of the `GAME_STARTED` notification
4. Each player is assigned a unique garage position along the track (see predefined positions below)
5. Clients should place each player's vehicle at their assigned spawn position

**Previous Issue (Now Fixed)**: Players occasionally received "Spawn position data missing in game start message!" due to a race condition where spawn positions were assigned before players were fully added to the room collection.

**Current Behavior**: Spawn positions are now assigned AFTER successful player addition, ensuring all players receive their assigned positions.

Predefined spawn positions on the track:
```
Position 0:  (66, -2, 0.8)
Position 1:  (60, -2, 0.8)
Position 2:  (54, -2, 0.8)
Position 3:  (47, -2, 0.8)
Position 4:  (41, -2, 0.8)
Position 5:  (35, -2, 0.8)
Position 6:  (28, -2, 0.8)
Position 7:  (22, -2, 0.8)
Position 8:  (16, -2, 0.8)
Position 9:  (9, -2, 0.8)
Position 10: (3, -2, 0.8)
Position 11: (-3, -2, 0.8)
Position 12: (-9, -2, 0.8)
Position 13: (-15, -2, 0.8)
Position 14: (-22, -2, 0.8)
Position 15: (-28, -2, 0.8)
Position 16: (-34, -2, 0.8)
Position 17: (-41, -2, 0.8)
Position 18: (-47, -2, 0.8)
Position 19: (-54, -2, 0.8)
```

The order of assignment depends on the order in which players joined the room, with earlier players getting lower position indices.

### 7.4 Position and Rotation Updates
Position updates use the following data flow:
1. Client sends a position update via UDP
2. Server associates the update with a player session
3. Server updates the player's information in their current room
4. Server broadcasts the update to all other players in the same room
5. Clients receive updates for all other players and update their local game state

The position is represented as a 3D vector (x, y, z) and rotation as a quaternion (x, y, z, w).

### 7.4 Message Relay
- Players can relay messages to other players using their session ID
- Messages are delivered to the target player via TCP
- The receiving player gets a `RELAYED_MESSAGE` command with the sender's ID, name, and message
- This can be used for in-game chat, custom events, or game-specific commands

## 8. Host Transfer Mechanism
When a host player leaves a room:
- If the room is empty, it is automatically removed
- If other players remain in the room, host status transfers to another player
- The new host has the authority to start the game
- No specific notification is sent when host status changes (clients should track this if needed)

## 9. Real-time Data Synchronization
The server supports two types of real-time data that are synchronized between players:

### 9.1 Position/Rotation Updates
- Sent via UDP with the "UPDATE" command
- Contains full position (Vector3) and rotation (Quaternion) data
- Updated whenever the client sends a new position update
- Distributed to all other players in the same room

### 9.2 Input Controls
- Sent via UDP with the "INPUT" command
- Contains steering, throttle, brake values, and a timestamp
- Used to synchronize vehicle control inputs between players
- Can be used for physics prediction or deterministic simulation
- The timestamp helps with lag compensation and input ordering

## 10. Logging & Debug
- TCP events (connect, disconnect, commands) are logged at INFO level
- UDP packet receipt and processing are logged at DEBUG level
- JSON parsing errors are caught and logged
- Use console logger to trace flow:
  ```bash
  dotnet run --verbosity normal
  ```

## 11. Dashboard Web Interface

### 11.1 Overview
The server includes a web-based dashboard interface for monitoring and administering the server. The dashboard is accessible via HTTP on port 8080.

### 11.2 Dashboard Features
- **Real-time server monitoring**:
  - Server uptime and system status
  - Active player sessions with authentication status
  - Room count and status (lobby vs active games)
  - Player distribution across rooms
- **Advanced security monitoring**:
  - Live security event tracking with timestamps and severity levels
  - Player threat level monitoring (0-3 scale with color coding)
  - Security violation tracking (total and recent violations per player)
  - Rate limiting status with real-time utilization percentages
  - Security event type breakdown and statistics
  - Automatic threat assessment and color-coded warnings
- **Player management**:
  - Monitor active player connections with last activity timestamps
  - Check which room each player is in
  - Track authentication status and session details
  - View TCP/UDP rate utilization per player
- **Room management**:
  - View all active rooms with player counts
  - Monitor room age and activity status
  - See host assignments and game states

### 11.3 Admin Controls
The dashboard includes comprehensive administrative controls for server management. These controls do not require authentication as the dashboard is intended for LAN-only access and not exposed to the internet.

#### Security Management Controls
- **Player Banning**: Ban players with custom reasons for security violations
- **Real-time Monitoring**: Track security events, violations, and threat levels
- **Rate Limit Monitoring**: View real-time TCP/UDP utilization per player
- **Security Event Analysis**: Filter and analyze security events by type and severity

#### Room Admin Controls
- **Close Room**: Remove a specific room and return all players to the lobby
- **Close All Rooms**: Remove all rooms and reset all player room associations

#### Player Admin Controls
- **Disconnect Player**: Forcibly disconnect a specific player session
- **Disconnect All Players**: Disconnect all active player sessions
- **Ban Player**: Permanently ban a player with customizable reason

#### Security Dashboard Features
- **Multi-tab Security Interface**: 
  - Overview tab with security statistics and threat status indicators
  - Security Events tab with real-time event logging and filtering
  - Player Security tab with individual player threat assessments
  - Rate Limits tab with per-player utilization monitoring
- **Threat Level Visualization**: Color-coded threat levels (🟢 Low, 🟡 Medium, 🔴 High)
- **Real-time Updates**: Auto-refresh every 10 seconds with manual refresh option

#### Admin Actions Process
When an admin action is performed:
1. The action is processed on the server
2. Affected players receive appropriate notifications
3. Resources are cleaned up (players removed from rooms, etc.)
4. Host status is transferred if necessary
5. The dashboard refreshes to show updated state

### 11.4 Accessing the Dashboard
- The dashboard is available at `http://server-ip:8080`
- It auto-refreshes every 10 seconds, with an option for manual refresh
- No login is required as it's designed for LAN-only access

## 12. Next Steps
- Implement certificate pinning for enhanced security
- Add rate limiting for UDP packets
- Implement server-side physics validation
- Add race-specific features like lap counting and race timing
- Optimize UDP broadcast for large player counts
- Implement game state synchronization for deterministic physics
- Add comprehensive logging and monitoring

## 13. Security Implementation Details

### 13.1 TLS/SSL Configuration
- **Certificate Management**: Server automatically generates self-signed certificates if none provided
- **Certificate Storage**: Certificates are saved as `server.pfx` in the application directory
- **TLS Versions**: Supports TLS 1.2 and TLS 1.3
- **Cipher Suites**: Uses modern cipher suites with forward secrecy
- **Certificate Validation**: Clients should implement proper certificate validation for production use

### 13.2 UDP Encryption Details
- **Algorithm**: AES-256-CBC encryption
- **Key Derivation**: Session-specific keys derived from session ID + shared secret
- **Packet Format**: `[4-byte length][encrypted data]`
- **Backward Compatibility**: Server accepts both encrypted and plain-text UDP packets
- **Session Isolation**: Each authenticated player gets unique encryption keys

### 13.3 Password Security
- **Hashing**: SHA-256 (should be upgraded to bcrypt/scrypt for production)
- **Storage**: Only password hashes are stored, never plain text
- **First-time Setup**: First connection with a username sets the password
- **Verification**: Subsequent connections require correct password

### 13.4 Security Best Practices for Clients
1. **Certificate Validation**: Implement proper certificate validation for production
2. **Connection Security**: Always use TLS for TCP connections
3. **Key Management**: Securely store UDP encryption keys
4. **Error Handling**: Handle authentication failures gracefully
5. **Rate Limiting**: Implement client-side rate limiting for UDP packets

### 13.5 Server Security Features
- **Session Isolation**: Each player session is completely isolated
- **Automatic Cleanup**: Inactive sessions are automatically removed
- **Certificate Rotation**: Certificates can be replaced without server restart
- **Encryption Negotiation**: Server automatically detects encrypted vs plain UDP packets
- **Admin Interface**: Dashboard is designed for LAN-only access (no authentication required)

## 13.6 Certificate Handling with Unity Clients

### 13.6.1 Certificate Validation in Unity
Unity clients might encounter certificate validation issues when connecting to the server:
```
Certificate validation failed: RemoteCertificateNameMismatch, RemoteCertificateChainErrors
```

This happens because:
1. The server uses a self-signed certificate
2. The client attempts to validate the certificate against trusted roots (which fails)
3. The hostname or IP the client connects to might not match what's in the certificate

### 13.6.2 Solution Options

#### Option 1: Bypass Certificate Validation (Development Only)
For development and testing, you can bypass certificate validation by implementing a custom validation callback:

```csharp
// In your Unity NetworkManager class
public bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                      X509Chain chain, SslPolicyErrors sslPolicyErrors)
{
    Debug.LogWarning($"Certificate validation: {sslPolicyErrors}");
    
    // Accept any certificate from our server
    // IMPORTANT: Only use this for your specific racing server, not general web traffic
    return true;
}

// When creating your SslStream
var sslStream = new SslStream(networkStream, false, ValidateServerCertificate);
await sslStream.AuthenticateAsClientAsync("your-server-hostname-or-ip");
```

#### Option 2: Export and Include the Server Certificate
For better security, you can include the server's public certificate in your game:

1. Export the server's public certificate (without private key)
2. Include it as a resource in your Unity game
3. Load and verify against this specific certificate:

```csharp
public class CertificateHandler
{
    private readonly X509Certificate2 _serverCert;
    
    public CertificateHandler()
    {
        // Load server certificate from resources
        TextAsset certAsset = Resources.Load<TextAsset>("server-cert");
        _serverCert = new X509Certificate2(certAsset.bytes);
    }
    
    public bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                          X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // Only accept our specific server certificate
        return certificate.GetCertHashString() == _serverCert.GetCertHashString();
    }
}
```

### 13.6.3 Exporting the Server Certificate
To export the server's public certificate for client validation:

1. From the server machine:
```bash
# Export PFX to PEM (public part only)
openssl pkcs12 -in server.pfx -clcerts -nokeys -out server-public.pem
```

2. Copy the `server-public.pem` file to your Unity project's Resources folder

### 13.6.4 Certificate Generation Details
The server automatically generates a certificate with:
- The server's public IP (obtained from `SERVER_PUBLIC_IP` environment variable or defaults to "89.114.116.19")
- The server's internal IP addresses
- The server hostname (`SERVER_HOSTNAME` environment variable or "racing-server")
- All common network interfaces
- Common special IPs (loopback, Any)

You can customize the certificate by setting these environment variables before starting the server:

```bash
# Linux/macOS
export SERVER_HOSTNAME="my-racing-server"
export SERVER_PUBLIC_IP="your-public-ip"

# Windows
set SERVER_HOSTNAME=my-racing-server
set SERVER_PUBLIC_IP=your-public-ip
```

## 14. Authentication System

### 14.1 Purpose
The authentication system provides a simple identity protection mechanism where:
- Players can claim and protect their unique usernames
- Re-connecting with the same name requires password verification
- Only basic commands are available without authentication
- Game-related features require successful authentication

### 14.2 Password Handling
The server uses SHA-256 hashing for password storage:
- Passwords are never stored in plain text
- Each player name has its own associated password hash
- The first time a name is used, the provided password is stored
- Subsequent connections must use the same password

### 14.3 Client Implementation
Client applications should:
1. Store the player's name and password locally
2. Send the password with the NAME command on reconnection
3. Handle AUTH_FAILED responses appropriately:
   - Show an error message to the user
   - Request the correct password
   - Offer to use a different name

### 14.4 Authentication States
Players can be in one of two authentication states:
1. **Unauthenticated**: Limited to basic commands
2. **Authenticated**: Full access to all game features

### 14.5 Recommended Client Workflow
```
1. Connect to server → Receive session ID
2. Ask user for name and password
3. Send NAME command with credentials
4. If AUTH_FAILED, prompt user again or choose new name
5. Once authenticated (NAME_OK with authenticated=true), proceed with game
```

## 15. Unity Client Implementation Guide

### 15.1 Overview
This section provides a complete guide for implementing a secure Unity client that connects to the MP-Server. The implementation covers TLS-encrypted TCP for commands and AES-encrypted UDP for real-time updates.

### 15.2 Unity Project Setup

#### 15.2.1 Required Assemblies
Add these assemblies to your Unity project (in the Player Settings → Configuration):
```
System.Net.Security
System.Security.Cryptography
System.Text.Json
```

#### 15.2.2 Required Using Statements
```csharp
using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections;
using UnityEngine;
using System.Net;
```

### 15.3 Complete Unity NetworkManager Implementation

```csharp
using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections;
using UnityEngine;
using System.Net;
using System.IO;

public class RacingNetworkManager : MonoBehaviour
{
    [Header("Connection Settings")]
    public string serverHost = "localhost";
    public int serverPort = 443;
    
    [Header("Player Settings")]
    public string playerName = "Player";
    public string playerPassword = "secret123";
    
    [Header("Debug")]
    public bool enableDebugLogs = true;
    
    // Networking components
    private TcpClient _tcpClient;
    private SslStream _sslStream;
    private UdpClient _udpClient;
    private StreamReader _tcpReader;
    private bool _isConnected = false;
    private bool _isAuthenticated = false;
    
    // Session data
    private string _sessionId;
    private string _currentRoomId;
    private UdpEncryption _udpCrypto;
    
    // UDP endpoint
    private IPEndPoint _serverUdpEndpoint;
    
    // Events
    public System.Action<bool> OnConnectionChanged;
    public System.Action<bool> OnAuthenticationChanged;
    public System.Action<string> OnRoomJoined;
    public System.Action<PlayerUpdate> OnPlayerUpdate;
    public System.Action<string, string> OnMessageReceived;
    
    void Start()
    {
        DontDestroyOnLoad(gameObject);
        _serverUdpEndpoint = new IPEndPoint(IPAddress.Parse(serverHost), serverPort);
    }
    
    #region Connection Management
    
    public async Task<bool> ConnectToServer()
    {
        try
        {
            Log("Connecting to server...");
            
            // Create TCP connection
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(serverHost, serverPort);
            
            // Setup TLS encryption
            _sslStream = new SslStream(_tcpClient.GetStream(), false, ValidateServerCertificate);
            await _sslStream.AuthenticateAsClientAsync(serverHost);
            
            // Setup reader for responses
            _tcpReader = new StreamReader(_sslStream, Encoding.UTF8);
            
            // Read welcome message
            string welcome = await _tcpReader.ReadLineAsync();
            Log($"Server welcome: {welcome}");
            
            if (welcome != null && welcome.StartsWith("CONNECTED|"))
            {
                _sessionId = welcome.Split('|')[1];
                _isConnected = true;
                OnConnectionChanged?.Invoke(true);
                
                // Start listening for TCP messages
                StartCoroutine(ListenForTcpMessages());
                
                // Authenticate player
                await AuthenticatePlayer();
                
                return true;
            }
            
            throw new Exception("Invalid welcome message from server");
        }
        catch (Exception ex)
        {
            LogError($"Failed to connect: {ex.Message}");
            await Disconnect();
            return false;
        }
    }
    
    private bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                          X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // For development: Accept self-signed certificates
        // For production: Implement proper certificate validation
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;
            
        if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors ||
            sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
        {
            Log($"Accepting self-signed certificate: {sslPolicyErrors}");
            return true; // Accept for development
        }
        
        LogError($"Certificate validation failed: {sslPolicyErrors}");
        return false;
    }
    
    private async Task AuthenticatePlayer()
    {
        try
        {
            var nameCommand = new
            {
                command = "NAME",
                name = playerName,
                password = playerPassword
            };
            
            await SendTcpMessage(nameCommand);
        }
        catch (Exception ex)
        {
            LogError($"Authentication failed: {ex.Message}");
        }
    }
    
    public async Task Disconnect()
    {
        _isConnected = false;
        _isAuthenticated = false;
        
        try
        {
            if (_sslStream != null)
            {
                await SendTcpMessage(new { command = "BYE" });
                _sslStream.Close();
            }
            
            _tcpClient?.Close();
            _udpClient?.Close();
        }
        catch (Exception ex)
        {
            LogError($"Error during disconnect: {ex.Message}");
        }
        
        OnConnectionChanged?.Invoke(false);
        OnAuthenticationChanged?.Invoke(false);
    }
    
    #endregion
    
    #region TCP Communication
    
    private async Task SendTcpMessage<T>(T message)
    {
        if (!_isConnected || _sslStream == null)
        {
            LogError("Not connected to server");
            return;
        }
        
        try
        {
            string json = JsonSerializer.Serialize(message) + "\n";
            byte[] data = Encoding.UTF8.GetBytes(json);
            await _sslStream.WriteAsync(data, 0, data.Length);
            
            Log($"Sent: {json.Trim()}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to send TCP message: {ex.Message}");
        }
    }
    
    private IEnumerator ListenForTcpMessages()
    {
        while (_isConnected && _tcpReader != null)
        {
            Task<string> readTask = _tcpReader.ReadLineAsync();
            
            // Wait for the task to complete
            while (!readTask.IsCompleted)
            {
                yield return null;
            }
            
            if (readTask.Result != null)
            {
                ProcessTcpMessage(readTask.Result);
            }
            else
            {
                // Connection lost
                Log("TCP connection lost");
                break;
            }
        }
        
        // Connection ended
        _ = Disconnect();
    }
    
    private void ProcessTcpMessage(string message)
    {
        try
        {
            Log($"Received: {message}");
            
            var jsonDoc = JsonDocument.Parse(message);
            var root = jsonDoc.RootElement;
            
            if (root.TryGetProperty("command", out var commandElement))
            {
                string command = commandElement.GetString();
                
                switch (command)
                {
                    case "NAME_OK":
                        HandleNameOk(root);
                        break;
                        
                    case "AUTH_FAILED":
                        HandleAuthFailed(root);
                        break;
                        
                    case "ROOM_CREATED":
                    case "JOIN_OK":
                        HandleRoomJoined(root);
                        break;
                        
                    case "GAME_STARTED":
                        HandleGameStarted(root);
                        break;
                        
                    case "RELAYED_MESSAGE":
                        HandleRelayedMessage(root);
                        break;
                        
                    case "ERROR":
                        HandleError(root);
                        break;
                        
                    default:
                        Log($"Unhandled command: {command}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to process TCP message: {ex.Message}");
        }
    }
    
    private void HandleNameOk(JsonElement root)
    {
        if (root.TryGetProperty("authenticated", out var authElement) && authElement.GetBoolean())
        {
            _isAuthenticated = true;
            OnAuthenticationChanged?.Invoke(true);
            
            // Setup UDP encryption if available
            if (root.TryGetProperty("udpEncryption", out var udpElement) && udpElement.GetBoolean())
            {
                _udpCrypto = new UdpEncryption(_sessionId);
                SetupUdpClient();
            }
            
            Log("Successfully authenticated with UDP encryption enabled");
        }
    }
    
    private void HandleAuthFailed(JsonElement root)
    {
        if (root.TryGetProperty("message", out var messageElement))
        {
            LogError($"Authentication failed: {messageElement.GetString()}");
        }
        _isAuthenticated = false;
        OnAuthenticationChanged?.Invoke(false);
    }
    
    private void HandleRoomJoined(JsonElement root)
    {
        if (root.TryGetProperty("roomId", out var roomIdElement))
        {
            _currentRoomId = roomIdElement.GetString();
            OnRoomJoined?.Invoke(_currentRoomId);
            Log($"Joined room: {_currentRoomId}");
        }
    }
    
    private void HandleGameStarted(JsonElement root)
    {
        // Handle spawn positions and game start logic
        if (root.TryGetProperty("spawnPositions", out var spawnElement))
        {
            // Process spawn positions for each player
            Log("Game started! Processing spawn positions...");
            // Implementation depends on your game logic
        }
    }
    
    private void HandleRelayedMessage(JsonElement root)
    {
        if (root.TryGetProperty("senderName", out var senderElement) &&
            root.TryGetProperty("message", out var messageElement))
        {
            OnMessageReceived?.Invoke(senderElement.GetString(), messageElement.GetString());
        }
    }
    
    private void HandleError(JsonElement root)
    {
        if (root.TryGetProperty("message", out var messageElement))
        {
            LogError($"Server error: {messageElement.GetString()}");
        }
    }
    
    #endregion
    
    #region UDP Communication
    
    private void SetupUdpClient()
    {
        try
        {
            _udpClient = new UdpClient();
            StartCoroutine(ListenForUdpMessages());
            Log("UDP client setup complete");
        }
        catch (Exception ex)
        {
            LogError($"Failed to setup UDP client: {ex.Message}");
        }
    }
    
    public async Task SendPositionUpdate(Vector3 position, Quaternion rotation)
    {
        if (!_isAuthenticated || _udpClient == null || string.IsNullOrEmpty(_currentRoomId))
            return;
        
        try
        {
            var update = new
            {
                command = "UPDATE",
                sessionId = _sessionId,
                position = new { x = position.x, y = position.y, z = position.z },
                rotation = new { x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w }
            };
            
            byte[] data;
            
            if (_udpCrypto != null)
            {
                // Send encrypted packet
                data = _udpCrypto.CreatePacket(update);
            }
            else
            {
                // Fallback to plain text
                string json = JsonSerializer.Serialize(update);
                data = Encoding.UTF8.GetBytes(json);
            }
            
            await _udpClient.SendAsync(data, data.Length, _serverUdpEndpoint);
        }
        catch (Exception ex)
        {
            LogError($"Failed to send position update: {ex.Message}");
        }
    }
    
    public async Task SendInputUpdate(float steering, float throttle, float brake)
    {
        if (!_isAuthenticated || _udpClient == null || string.IsNullOrEmpty(_currentRoomId))
            return;
        
        try
        {
            var input = new
            {
                command = "INPUT",
                sessionId = _sessionId,
                roomId = _currentRoomId,
                input = new
                {
                    steering = steering,
                    throttle = throttle,
                    brake = brake,
                    timestamp = Time.time
                },
                client_id = _sessionId
            };
            
            byte[] data;
            
            if (_udpCrypto != null)
            {
                data = _udpCrypto.CreatePacket(input);
            }
            else
            {
                string json = JsonSerializer.Serialize(input);
                data = Encoding.UTF8.GetBytes(json);
            }
            
            await _udpClient.SendAsync(data, data.Length, _serverUdpEndpoint);
        }
        catch (Exception ex)
        {
            LogError($"Failed to send input update: {ex.Message}");
        }
    }
    
    private IEnumerator ListenForUdpMessages()
    {
        while (_isConnected && _udpClient != null)
        {
            Task<UdpReceiveResult> receiveTask = _udpClient.ReceiveAsync();
            
            while (!receiveTask.IsCompleted)
            {
                yield return null;
            }
            
            if (receiveTask.IsCompletedSuccessfully)
            {
                ProcessUdpMessage(receiveTask.Result.Buffer);
            }
        }
    }
    
    private void ProcessUdpMessage(byte[] data)
    {
        try
        {
            JsonElement update;
            
            // Try to decrypt if possible
            if (_udpCrypto != null && data.Length >= 4)
            {
                var parsedData = _udpCrypto.ParsePacket<JsonElement>(data);
                if (parsedData.ValueKind != JsonValueKind.Undefined)
                {
                    update = parsedData;
                }
                else
                {
                    // Fallback to plain text
                    string json = Encoding.UTF8.GetString(data);
                    update = JsonSerializer.Deserialize<JsonElement>(json);
                }
            }
            else
            {
                // Plain text packet
                string json = Encoding.UTF8.GetString(data);
                update = JsonSerializer.Deserialize<JsonElement>(json);
            }
            
            // Process the update
            if (update.TryGetProperty("command", out var cmdElement))
            {
                string command = cmdElement.GetString();
                
                if (command == "UPDATE")
                {
                    HandlePositionUpdate(update);
                }
                else if (command == "INPUT")
                {
                    HandleInputUpdate(update);
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to process UDP message: {ex.Message}");
        }
    }
    
    private void HandlePositionUpdate(JsonElement update)
    {
        try
        {
            var sessionId = update.GetProperty("sessionId").GetString();
            var position = update.GetProperty("position");
            var rotation = update.GetProperty("rotation");
            
            var playerUpdate = new PlayerUpdate
            {
                SessionId = sessionId,
                Position = new Vector3(
                    position.GetProperty("x").GetSingle(),
                    position.GetProperty("y").GetSingle(),
                    position.GetProperty("z").GetSingle()
                ),
                Rotation = new Quaternion(
                    rotation.GetProperty("x").GetSingle(),
                    rotation.GetProperty("y").GetSingle(),
                    rotation.GetProperty("z").GetSingle(),
                    rotation.GetProperty("w").GetSingle()
                )
            };
            
            OnPlayerUpdate?.Invoke(playerUpdate);
        }
        catch (Exception ex)
        {
            LogError($"Failed to handle position update: {ex.Message}");
        }
    }
    
    private void HandleInputUpdate(JsonElement update)
    {
        // Handle input updates from other players
        // Implementation depends on your game's input handling system
    }
    
    #endregion
    
    #region Room Management
    
    public async Task CreateRoom(string roomName)
    {
        if (!_isAuthenticated) return;
        
        await SendTcpMessage(new { command = "CREATE_ROOM", name = roomName });
    }
    
    public async Task JoinRoom(string roomId)
    {
        if (!_isAuthenticated) return;
        
        await SendTcpMessage(new { command = "JOIN_ROOM", roomId = roomId });
    }
    
    public async Task StartGame()
    {
        if (!_isAuthenticated || string.IsNullOrEmpty(_currentRoomId)) return;
        
        await SendTcpMessage(new { command = "START_GAME" });
    }
    
    public async Task SendMessage(string targetId, string message)
    {
        if (!_isAuthenticated) return;
        
        await SendTcpMessage(new { command = "RELAY_MESSAGE", targetId = targetId, message = message });
    }
    
    #endregion
    
    #region Logging
    
    private void Log(string message)
    {
        if (enableDebugLogs)
        {
            Debug.Log($"[RacingNetwork] {message}");
        }
    }
    
    private void LogError(string message)
    {
        Debug.LogError($"[RacingNetwork] {message}");
    }
    
    #endregion
    
    void OnDestroy()
    {
        _ = Disconnect();
    }
}

// Data structures
[System.Serializable]
public struct PlayerUpdate
{
    public string SessionId;
    public Vector3 Position;
    public Quaternion Rotation;
}
```

### 15.4 UDP Encryption Implementation for Unity

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class UdpEncryption
{
    private readonly byte[] _key;
    private readonly byte[] _iv;
    
    public UdpEncryption(string sessionId, string sharedSecret = "RacingServerUDP2024!")
    {
        // Generate deterministic key and IV from session ID and shared secret
        using var sha256 = SHA256.Create();
        var keySource = Encoding.UTF8.GetBytes(sessionId + sharedSecret);
        var keyHash = sha256.ComputeHash(keySource);
        
        _key = new byte[32]; // AES-256 key
        _iv = new byte[16];   // AES IV
        
        Array.Copy(keyHash, 0, _key, 0, 32);
        Array.Copy(keyHash, 16, _iv, 0, 16);
    }
    
    public byte[] Encrypt(string jsonData)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        
        var dataBytes = Encoding.UTF8.GetBytes(jsonData);
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
    }
    
    public string Decrypt(byte[] encryptedData)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            
            using var decryptor = aes.CreateDecryptor();
            var decryptedBytes = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    public byte[] CreatePacket(object data)
    {
        var json = JsonSerializer.Serialize(data);
        var encryptedData = Encrypt(json);
        
        // Create packet with length header
        var packet = new byte[4 + encryptedData.Length];
        BitConverter.GetBytes(encryptedData.Length).CopyTo(packet, 0);
        encryptedData.CopyTo(packet, 4);
        
        return packet;
    }
    
    public T ParsePacket<T>(byte[] packetData)
    {
        if (packetData.Length < 4)
            return default(T);
        
        var length = BitConverter.ToInt32(packetData, 0);
        if (length != packetData.Length - 4 || length <= 0)
            return default(T);
        
        var encryptedData = new byte[length];
        Array.Copy(packetData, 4, encryptedData, 0, length);
        
        var json = Decrypt(encryptedData);
        if (string.IsNullOrEmpty(json))
            return default(T);
        
        return JsonSerializer.Deserialize<T>(json);
    }
}
```

### 15.5 Unity Usage Example

```csharp
public class GameController : MonoBehaviour
{
    [Header("Network")]
    public RacingNetworkManager networkManager;
    
    [Header("Player")]
    public Transform playerCar;
    
    [Header("Other Players")]
    public GameObject playerCarPrefab;
    
    private Dictionary<string, GameObject> _otherPlayers = new Dictionary<string, GameObject>();
    
    async void Start()
    {
        // Setup network events
        networkManager.OnConnectionChanged += OnConnectionChanged;
        networkManager.OnAuthenticationChanged += OnAuthenticationChanged;
        networkManager.OnPlayerUpdate += OnPlayerUpdate;
        
        // Connect to server
        bool connected = await networkManager.ConnectToServer();
        if (connected)
        {
            Debug.Log("Connected to racing server!");
        }
    }
    
    void Update()
    {
        // Send position updates for our player
        if (networkManager._isAuthenticated && playerCar != null)
        {
            _ = networkManager.SendPositionUpdate(playerCar.position, playerCar.rotation);
            
            // Send input if the car is being controlled
            float steering = Input.GetAxis("Horizontal");
            float throttle = Input.GetAxis("Vertical");
            float brake = Input.GetKey(KeyCode.Space) ? 1.0f : 0.0f;
            
            _ = networkManager.SendInputUpdate(steering, throttle, brake);
        }
    }
    
    private void OnConnectionChanged(bool connected)
    {
        Debug.Log($"Connection status: {connected}");
    }
    
    private void OnAuthenticationChanged(bool authenticated)
    {
        Debug.Log($"Authentication status: {authenticated}");
        
        if (authenticated)
        {
            // Auto-create or join a room
            _ = networkManager.CreateRoom("My Race Room");
        }
    }
    
    private void OnPlayerUpdate(PlayerUpdate update)
    {
        // Update other player positions
        if (!_otherPlayers.ContainsKey(update.SessionId))
        {
            // Create new player car
            GameObject newPlayer = Instantiate(playerCarPrefab);
            _otherPlayers[update.SessionId] = newPlayer;
        }
        
        // Update position and rotation
        var playerObject = _otherPlayers[update.SessionId];
        playerObject.transform.position = update.Position;
        playerObject.transform.rotation = update.Rotation;
    }
}
```

### 15.6 Security Best Practices for Unity

#### 15.6.1 Certificate Handling
- **Development**: Use the provided certificate validation bypass for testing
- **Production**: Implement proper certificate validation or bundle the server's public certificate
- **Never** accept all certificates in production builds

#### 15.6.2 Password Security
- Store passwords securely using Unity's PlayerPrefs with encryption or a secure vault
- Consider implementing a proper registration/login UI
- Use strong passwords for player accounts

#### 15.6.3 Network Security
- Always validate incoming data before using it
- Implement client-side rate limiting for UDP packets
- Handle network errors gracefully

#### 15.6.4 Error Handling
```csharp
private void HandleNetworkError(Exception ex)
{
    Debug.LogError($"Network error: {ex.Message}");
    
    // Attempt reconnection
    StartCoroutine(ReconnectAfterDelay(5.0f));
}

private IEnumerator ReconnectAfterDelay(float delay)
{
    yield return new WaitForSeconds(delay);
    
    if (!networkManager._isConnected)
    {
        bool reconnected = await networkManager.ConnectToServer();
        if (!reconnected)
        {
            // Show error to user or try again
            StartCoroutine(ReconnectAfterDelay(10.0f));
        }
    }
}
```

### 15.7 Testing Your Unity Client

1. **Local Testing**: Connect to `localhost` or `127.0.0.1`
2. **LAN Testing**: Connect to the server's local IP address
3. **Internet Testing**: Connect to the server's public IP address

#### 15.7.1 Debug Console Commands
Add these debug commands to test your client:

```csharp
void OnGUI()
{
    if (GUI.Button(new Rect(10, 10, 100, 30), "Connect"))
    {
        _ = networkManager.ConnectToServer();
    }
    
    if (GUI.Button(new Rect(10, 50, 100, 30), "Create Room"))
    {
        _ = networkManager.CreateRoom("Test Room");
    }
    
    if (GUI.Button(new Rect(10, 90, 100, 30), "Start Game"))
    {
        _ = networkManager.StartGame();
    }
}
```

### 15.8 Performance Optimization

#### 15.8.1 Update Frequency
- Position updates: 10-20 Hz (every 50-100ms)
- Input updates: 30-60 Hz (every 16-33ms)
- Use Unity's `InvokeRepeating` or coroutines for consistent timing

#### 15.8.2 Network Interpolation
Implement client-side interpolation for smooth movement:

```csharp
public class NetworkPlayerController : MonoBehaviour
{
    private Vector3 _targetPosition;
    private Quaternion _targetRotation;
    private float _interpolationSpeed = 10f;
    
    public void UpdateNetworkTransform(Vector3 position, Quaternion rotation)
    {
        _targetPosition = position;
        _targetRotation = rotation;
    }
    
    void Update()
    {
        transform.position = Vector3.Lerp(transform.position, _targetPosition, Time.deltaTime * _interpolationSpeed);
        transform.rotation = Quaternion.Lerp(transform.rotation, _targetRotation, Time.deltaTime * _interpolationSpeed);
    }
}
```

---

With this complete Unity implementation guide, you can now create a secure racing game client that properly connects to the MP-Server with full TLS encryption for commands and AES encryption for real-time updates. The implementation handles all security aspects while providing a robust foundation for your multiplayer racing game. Happy racing!

## 16. Advanced Security System Architecture

MP-Server implements a comprehensive, multi-layered security architecture designed to prevent cheating, protect against abuse, and maintain fair gameplay across all connected clients.

### 16.1 Security Layer Overview

The security system operates on four primary layers:

1. **Network Security**: TLS encryption for TCP, AES-256-CBC for UDP
2. **Authentication Security**: SHA-256 password hashing with session management
3. **Behavioral Security**: Packet validation, rate limiting, and anti-cheat detection
4. **Administrative Security**: Real-time monitoring and control systems

### 16.2 Packet Validation System

#### 16.2.1 Physics-Based Validation
The server validates all incoming position and input data against realistic physics constraints:

**Position Validation**:
- Boundary checking against defined track limits
- Maximum velocity enforcement (configurable per game mode)
- Acceleration constraint validation
- Position delta validation between updates

**Input Validation**:
- Steering angle limits (-1.0 to 1.0)
- Throttle/brake range validation (0.0 to 1.0)
- Input rate-of-change validation
- Impossible input combination detection

**Temporal Validation**:
- Timestamp verification to prevent replay attacks
- Update frequency validation
- Chronological order enforcement

#### 16.2.2 Violation Tracking
```csharp
// Violation severity levels
public enum ViolationSeverity
{
    Minor = 0,    // Slight physics violations, possible network lag
    Moderate = 1, // Clear constraint violations, potential cheating
    Severe = 2,   // Obvious cheating attempts, impossible values
    Critical = 3  // Malicious activity, coordinated attacks
}
```

### 16.3 Rate Limiting Architecture

#### 16.3.1 Sliding Window Implementation
The rate limiter uses a sophisticated sliding window algorithm:

**TCP Rate Limiting**:
- Default: 10 messages/second per client
- Burst protection: Brief spikes allowed, sustained violations blocked
- Automatic cleanup of inactive client tracking data

**UDP Rate Limiting**:
- Default: 60 packets/second per client
- Real-time monitoring of packet frequency
- Graceful degradation for overloaded clients

#### 16.3.2 Adaptive Throttling
The system adapts to network conditions:
- Higher limits for authenticated, trusted clients
- Reduced limits for clients with security violations
- Dynamic adjustment based on server load

### 16.4 Anti-Cheat System

#### 16.4.1 Threat Assessment Algorithm
```csharp
public int CalculateThreatLevel(PlayerSecurity playerSecurity)
{
    var score = 0;
    score += Math.Min(playerSecurity.ViolationCount / 5, 10);
    score += playerSecurity.RecentViolations.Count(v => v.Timestamp > DateTime.UtcNow.AddMinutes(-5)) * 2;
    score += (int)playerSecurity.HighestSeverity;
    return Math.Min(score, 3); // Cap at level 3
}
```

#### 16.4.2 Automated Response System
- **Level 0 (Green)**: Normal player, no restrictions
- **Level 1 (Yellow)**: Minor violations, enhanced monitoring
- **Level 2 (Orange)**: Moderate violations, rate limiting applied
- **Level 3 (Red)**: High-risk player, auto-kick enabled

### 16.5 Real-Time Security Monitoring

#### 16.5.1 Dashboard Integration
The web dashboard provides comprehensive security oversight:

**Live Monitoring**:
- Real-time player threat levels with color coding
- Active security events with severity indicators
- Rate limiting utilization percentages per client
- Geographic distribution of security events

**Historical Analysis**:
- Security event trends and patterns
- Player behavior analysis over time
- Violation type distribution statistics
- Peak load and attack pattern identification

#### 16.5.2 Administrative Controls
**Immediate Response Tools**:
- One-click player disconnection
- Temporary and permanent ban management
- IP address blocking with CIDR support
- Emergency lockdown modes

**Security Configuration**:
- Real-time adjustment of validation thresholds
- Rate limiting parameter modification
- Threat level calculation tuning
- Auto-kick threshold configuration

### 16.6 Security Event Correlation

#### 16.6.1 Pattern Detection
The system correlates security events across multiple dimensions:

**Temporal Correlation**:
- Identifies coordinated attack patterns
- Detects suspicious timing in violations
- Recognizes repeated offense patterns

**Behavioral Correlation**:
- Links similar violation types across players
- Identifies potential botnet activity
- Detects unusual gameplay patterns

#### 16.6.2 Threat Intelligence
**Reputation System**:
- Player history tracking across sessions
- IP address reputation scoring
- Geographic risk assessment
- Device fingerprinting (where supported)

### 16.7 Performance Optimization

#### 16.7.1 Efficient Processing
Security operations are optimized for minimal performance impact:

**Parallel Processing**:
- Security checks run on separate threads
- Batch processing of validation results
- Asynchronous threat level calculations

**Memory Management**:
- Circular buffers for recent violation history
- Automatic cleanup of stale security data
- Efficient data structures for real-time access

#### 16.7.2 Scalability Considerations
The security system scales with server load:
- Configurable validation strictness based on player count
- Dynamic resource allocation for security processing
- Load balancing of security operations across CPU cores

### 16.8 Integration Points

#### 16.8.1 Game Logic Integration
Security validation integrates seamlessly with game mechanics:
- Pre-validation hooks before game state updates
- Post-validation callbacks for security event handling
- Game-mode-specific validation rule sets

#### 16.8.2 Logging and Alerting
Comprehensive security logging ensures full audit trails:
- Structured JSON logging for security events
- Configurable log retention periods
- Integration with external security monitoring systems
- Real-time alerting for critical security events

This advanced security architecture ensures MP-Server maintains the highest standards of fair play while providing administrators with the tools needed to quickly respond to security threats and maintain a positive gaming environment.

## 17. Troubleshooting Guide

### 16.1 Common UDP Issues

#### 16.1.1 JsonReaderException with '0xEF' Character
**Error**: `JsonReaderException: '0xEF' is an invalid start of a value`

**Cause**: This error occurred when the server received encrypted UDP packets but was trying to parse them as plain JSON. The '0xEF' byte is typically the first byte of AES-encrypted data.

**Solution**: This was a server-side bug that has been **RESOLVED**. If you still encounter this error:
1. Ensure your server is running the latest version with the UDP encryption fix
2. Verify that your client is properly implementing UDP encryption after authentication
3. Check that encrypted packets include the correct 4-byte length header

#### 16.1.2 "Spawn position data missing in game start message!"
**Error**: Unity clients report missing spawn position data when games start.

**Cause**: This was caused by a race condition in the server's spawn position assignment logic.

**Solution**: This bug has been **RESOLVED** in the server. If you still encounter this:
1. Update to the latest server version
2. Ensure all players are properly joined to the room before starting the game
3. Check that the host has the authority to start the game

#### 16.1.3 UDP Packets Not Being Received
**Symptoms**: 
- Position updates not reaching other players
- Input commands not being broadcast
- UDP communication appears to be broken

**Troubleshooting Steps**:
1. **Check Authentication**: UDP encryption only works for authenticated players
   ```
   Ensure client received: {"command":"NAME_OK","authenticated":true,"udpEncryption":true}
   ```

2. **Verify UDP Endpoint Registration**: Server needs at least one UDP packet to register the client's endpoint
   ```csharp
   // Send initial position update immediately after authentication
   await SendPositionUpdate(initialPosition, initialRotation);
   ```

3. **Check Encryption Status**: Debug whether packets are being sent encrypted or plain
   ```csharp
   // In your UDP send method
   Debug.Log($"Sending {(udpCrypto != null ? "encrypted" : "plain")} UDP packet");
   ```

4. **Firewall Issues**: Ensure UDP port 443 is not blocked
   ```bash
   # Test UDP connectivity
   nc -u server-ip 443
   ```

### 16.2 Authentication Issues

#### 16.2.1 Certificate Validation Failures
**Error**: `Certificate validation failed: RemoteCertificateNameMismatch, RemoteCertificateChainErrors`

**Solutions**:
1. **For Development**: Use certificate validation bypass
2. **For Production**: Export and bundle the server's public certificate
3. **Configure Server Hostname**: Set SERVER_HOSTNAME environment variable

#### 16.2.2 Authentication Failed
**Error**: `{"command":"AUTH_FAILED","message":"Invalid password for this player name."}`

**Troubleshooting**:
1. Check if this is a first-time connection (password gets set on first use)
2. Verify password is correct for existing player names
3. Try using a different player name for testing

### 16.3 Connection Issues

#### 16.3.1 TLS Handshake Failures
**Symptoms**: Client cannot establish TLS connection

**Solutions**:
1. Verify server is running on the correct port (443)
2. Check if server certificate is properly generated
3. Ensure client supports TLS 1.2/1.3
4. For Unity: Use the provided TLS validation bypass for development

#### 16.3.2 Session Timeout
**Error**: Connection drops after 60 seconds of inactivity

**Solution**: Implement periodic PING commands
```csharp
// Send periodic ping to keep connection alive
InvokeRepeating("SendPing", 30f, 30f);

private async void SendPing()
{
    await SendTcpMessage(new { command = "PING" });
}
```

### 16.4 Performance Issues

#### 16.4.1 High UDP Packet Loss
**Symptoms**: Jerky movement, missing position updates

**Solutions**:
1. Reduce UDP send frequency if overwhelming the network
2. Implement client-side interpolation
3. Check for network congestion
4. Optimize packet size (keep under 1200 bytes)

#### 16.4.2 High CPU Usage from Encryption
**Symptoms**: Server performance degradation with many players

**Solutions**:
1. Monitor encryption overhead with performance profiling
2. Consider adjusting UDP update frequencies
3. Implement UDP packet batching for multiple updates

### 16.5 Development Tips

#### 16.5.1 Debugging UDP Encryption
Enable detailed logging to debug UDP encryption issues:

**Server-side**:
```csharp
// In RacingServer.cs ProcessUdpPacketAsync (already implemented)
Console.WriteLine($"UDP packet from {endpoint}: {(isEncrypted ? "encrypted" : "plain")}");
```

**Client-side**:
```csharp
// In your UDP send method
Debug.Log($"Sending UDP: {JsonSerializer.Serialize(data)}");
Debug.Log($"Encryption: {(udpCrypto != null ? "enabled" : "disabled")}");
```

#### 16.5.2 Testing Encryption
To verify UDP encryption is working:

1. **Capture Network Traffic**: Use Wireshark to see if UDP packets are encrypted
2. **Server Logs**: Check for "Decrypted UDP packet" vs "Plain text UDP packet" messages
3. **Test Both Modes**: Test with authenticated and unauthenticated clients

#### 16.5.3 Common Client Implementation Mistakes
1. **Not waiting for authentication** before sending UDP packets
2. **Missing length headers** in encrypted UDP packets
3. **Incorrect session ID** in UDP packets
4. **Not handling certificate validation** properly
5. **Sending UDP before joining a room**

### 16.6 Error Code Reference

| Error Code | Message | Cause | Solution |
|------------|---------|-------|----------|
| AUTH_FAILED | Invalid password for this player name | Wrong password for existing player | Use correct password or different name |
| ERROR | Authentication required | Attempting restricted commands without auth | Authenticate first with NAME + password |
| ERROR | Cannot start game. Only the host can start | Non-host trying to start game | Only room host can start games |
| ERROR | Room not found | Invalid room ID in JOIN_ROOM | Use LIST_ROOMS to get valid room IDs |
| ERROR | Failed to join room | Room full or inactive | Try different room or wait |
| UNKNOWN_COMMAND | Invalid command | Malformed or unrecognized command | Check command spelling and format |

### 16.7 Quick Diagnostic Commands

Use these commands to diagnose issues:

#### Server Status Check
```json
{"command":"PING"}
```
Expected response: `{"command":"PONG"}`

#### Authentication Status Check  
```json
{"command":"PLAYER_INFO"}
```
Response includes authentication status and current room

#### Room Status Check
```json
{"command":"LIST_ROOMS"}
```
Shows all available rooms and player counts

#### Connection Test Sequence
1. Connect to TCP port 443 with TLS
2. Receive welcome message with session ID
3. Send NAME command with password
4. Verify NAME_OK response with authentication status
5. Send first UDP packet to register endpoint
6. Create or join a room
7. Start sending regular position updates

If any step fails, refer to the specific troubleshooting section above.

---

**Note**: Most critical server-side issues have been resolved in the latest version. If you encounter persistent problems, ensure you're running the updated server code with the UDP encryption and spawn position fixes.