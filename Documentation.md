# MP-Server Protocol Documentation

## 1. Overview
MP-Server is a secure TCP/UDP racing‐game server with TLS/SSL encryption.  
Clients connect over TLS-encrypted TCP (for commands, room management, chat) and send/receive encrypted UDP     await udp.SendAsync(bytes, bytes.Length, serverHost, 443);ackets (for real‐time position updates).

Ports (defaults):
- TCP: 443 (TLS/SSL encrypted) - Uses standard HTTPS port for firewall traversal
- UDP: 443 (AES encrypted for authenticated users) - Same port as TCP 
- Dashboard Web UI: 8080

## 1.1 Security Features
- **TLS/SSL TCP encryption**: All command traffic is encrypted using TLS 1.2/1.3
- **Self-signed certificate generation**: Server automatically generates certificates if none provided
- **UDP AES encryption**: Position and input data encrypted with session-specific keys
- **Player authentication**: Password-based authentication with hashed storage
- **Session isolation**: Each player gets unique encryption keys

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

### 4.2 Encryption
**For Authenticated Players:**
- UDP packets are automatically encrypted using AES-256-CBC
- Each session gets unique encryption keys derived from the session ID
- Packet format: `[4-byte length header][encrypted JSON data]`
- The server handles encryption/decryption transparently

**For Unauthenticated Players:**
- UDP packets are sent as plain-text JSON (backward compatibility)
- Limited functionality compared to authenticated users

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

### 7.4 Spawn Positions and Game Start
When a host starts a game:
1. The server assigns a spawn position to each player in the room
2. The spawn positions are sent to all players as part of the `GAME_STARTED` notification
3. Each player is assigned a unique garage position along the track (see predefined positions below)
4. Clients should place each player's vehicle at their assigned spawn position

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
- Real-time monitoring of server statistics:
  - Server uptime
  - Active player sessions
  - Room count and status
  - Player counts
- Room management:
  - View all active rooms
  - View player distribution across rooms
  - See room status (lobby or active game)
  - Age of each room
- Player session management:
  - Monitor active player connections
  - Check which room each player is in
  - Track player activity

### 11.3 Admin Controls
The dashboard includes administrative controls for server management. These controls do not require authentication as the dashboard is intended for LAN-only access and not exposed to the internet.

#### Room Admin Controls
- **Close Room**: Remove a specific room and return all players to the lobby
- **Close All Rooms**: Remove all rooms and reset all player room associations

#### Player Admin Controls
- **Disconnect Player**: Forcibly disconnect a specific player session
- **Disconnect All Players**: Disconnect all active player sessions

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

---

With this guide and the samples above, you can implement a client in your language of choice, manage TCP commands, and stream position updates over UDP. Happy racing!