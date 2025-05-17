# MP-Server Protocol Documentation

## 1. Overview
MP-Server is a simple TCP/UDP racing‐game server.  
Clients connect over TCP (for commands, room management, chat) and send/receive UDP packets (for real‐time position updates).

Ports (defaults):
- TCP: 7777
- UDP: 7778

## 2. Prerequisites
- .NET 9.0 runtime
- A TCP‐capable socket library (e.g. `System.Net.Sockets.TcpClient` in .NET, `net` module in Node.js, or BSD sockets in C/C++)
- A UDP socket library for state updates
- UTF-8 support for text commands
- JSON parser (server uses System.Text.Json for all command processing)

## 3. TCP Protocol

### 3.1 Connection & Framing
1. Client opens a TCP connection to server:  
   `tcpClient.Connect("server.address", 7777)`
2. Server immediately responds with a welcome message terminated by `\n`:  
   ```
   CONNECTED|<sessionId>\n
   ```
3. All subsequent messages are **newline‐delimited JSON**:
   ```
   {"command":"COMMAND_NAME","param1":"value1","param2":"value2"}\n
   ```

### 3.2 Supported Commands

| Command        | Direction    | Payload (JSON)                                    | Response (JSON)                         |
| -------------- | ------------ | ------------------------------------------------- | --------------------------------------- |
| `NAME`         | Client → Srv | `{"command":"NAME","name":"playerName"}`        | `{"command":"NAME_OK","name":"playerName"}` |
| `CREATE_ROOM`  | Client → Srv | `{"command":"CREATE_ROOM","name":"roomName"}`   | `{"command":"ROOM_CREATED","roomId":"id","name":"roomName"}` |
| `JOIN_ROOM`    | Client → Srv | `{"command":"JOIN_ROOM","roomId":"id"}`         | `{"command":"JOIN_OK","roomId":"id"}` or `{"command":"ERROR","message":"Failed to join room. Room may be full or inactive."}` |
| `LEAVE_ROOM`   | Client → Srv | `{"command":"LEAVE_ROOM"}`                      | `{"command":"LEAVE_OK","roomId":"id"}` or `{"command":"ERROR","message":"Cannot leave room. No room joined."}` |
| `PING`         | Client → Srv | `{"command":"PING"}`                            | `{"command":"PONG"}`                  |
| `LIST_ROOMS`   | Client → Srv | `{"command":"LIST_ROOMS"}`                     | `{"command":"ROOM_LIST","rooms":[{"id":"id","name":"roomName","playerCount":0,"isActive":false}]}` |
| `PLAYER_INFO`  | Client → Srv | `{"command":"PLAYER_INFO"}`                    | `{"command":"PLAYER_INFO","playerInfo":{"id":"id","name":"playerName","currentRoomId":"roomId"}}` |
| `START_GAME`   | Client → Srv | `{"command":"START_GAME"}`                     | `{"command":"GAME_STARTED","roomId":"roomId"}` or `{"command":"ERROR","message":"Cannot start game. No room joined or room not found."}` |
| `BYE`          | Client → Srv | `{"command":"BYE"}`                            | `{"command":"BYE_OK"}` |
| Any other      | Client → Srv | e.g. `{"command":"FOO"}`                        | `{"command":"UNKNOWN_COMMAND","originalCommand":"FOO"}` |

#### Error Handling
- Malformed JSON commands return `{"command":"ERROR","message":"Invalid JSON format"}`.
- Unrecognized commands return `{"command":"UNKNOWN_COMMAND","originalCommand":"cmd"}`.
- If server detects inactivity (>60 s without messages), it will close the TCP socket.

## 4. UDP Protocol

### 4.1 Purpose
Use UDP for low‐latency position & rotation updates once the client has joined or created a room.

### 4.2 Packet Format (JSON-based)
The server accepts and broadcasts JSON packets for position updates:

```
{"command":"UPDATE","sessionId":"id","position":{"x":0,"y":0,"z":0},"rotation":{"x":0,"y":0,"z":0,"w":1}}\n
```

- `sessionId`: your TCP session ID  
- `position`: Vector3 with x, y, z coordinates
- `rotation`: Quaternion with x, y, z, w components

Server echoes or broadcasts state to other clients in the same room.

### 4.3 UDP Broadcasting
- Position updates are automatically broadcast to all players in the same room (except the sender)
- The server maintains player positions with their UDP endpoints in each room
- Each player needs to send at least one UDP packet to register their endpoint with the server
- Server manages the mapping between player sessions and their UDP endpoints

### 4.4 Example (C# send)
```csharp
using var udp = new UdpClient();
var posUpdate = new { 
    command = "UPDATE", 
    sessionId = sessionId, 
    position = new { x = posX, y = posY, z = posZ },
    rotation = new { x = rotX, y = rotY, z = rotZ, w = rotW }
};
var json = JsonSerializer.Serialize(posUpdate) + "\n";
var bytes = Encoding.UTF8.GetBytes(json);
await udp.SendAsync(bytes, bytes.Length, serverHost, 7778);
```

## 5. Room Management

### 5.1 Room Properties
- Each room has a unique ID, name, and a host player
- Rooms have a maximum player limit (default: 20)
- Rooms can be active (game started) or inactive (lobby)
- Creation timestamp is recorded

### 5.2 Room Operations
- Create room: A player can create a new room and becomes its host
- Join room: Players can join rooms that are not active and not full
- Start game: The host can start the game, which marks the room as active
- List rooms: Get all available rooms with their basic information

## 6. Example TCP Client (C#)

```csharp
using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class RacingClient
{
    private TcpClient _tcp;
    private NetworkStream _stream;
    private string _sessionId;

    public async Task RunAsync(string host, int port)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);
        _stream = _tcp.GetStream();

        // Read welcome
        var reader = new StreamReader(_stream, Encoding.UTF8);
        var welcome = await reader.ReadLineAsync();
        Console.WriteLine(welcome); // "CONNECTED|<sessionId>"
        _sessionId = welcome.Split('|')[1];

        // Set name
        await SendJsonAsync(new { command = "NAME", name = "Speedy" });
        var response = await reader.ReadLineAsync();
        Console.WriteLine(response); // {"command":"NAME_OK","name":"Speedy"}

        // Create a room
        await SendJsonAsync(new { command = "CREATE_ROOM", name = "FastTrack" });
        response = await reader.ReadLineAsync();
        Console.WriteLine(response); // {"command":"ROOM_CREATED","roomId":"<id>","name":"FastTrack"}

        // ... then start your UDP updates …
    }

    private async Task SendJsonAsync<T>(T data)
    {
        var json = JsonSerializer.Serialize(data) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await _stream.WriteAsync(bytes, 0, bytes.Length);
    }
}
```

## 7. Player Session Management

### 7.1 Session Lifecycle
- Sessions are created when a client connects via TCP
- Each session has a unique ID that's shared with the client
- Inactivity timeout: Sessions with no activity for > 60 seconds are disconnected
- Sessions track player name, current room, and last activity time

### 7.2 Position Updates
- Player position and rotation are tracked via the PlayerInfo record
- Position is represented as Vector3 (x, y, z)
- Rotation is represented as Quaternion (x, y, z, w)

## 8. Logging & Debug
- TCP events (connect, disconnect, commands) are logged at INFO level
- UDP packet receipt and processing are logged at DEBUG level
- JSON parsing errors are caught and logged
- Use console logger to trace flow:
  ```bash
  dotnet run --verbosity normal
  ```

## 9. Next Steps
- Add authentication/tokens for secure player identification
- Implement chat functionality
- Add race-specific features like lap counting and race timing
- Add server admin commands for room management
- Optimize UDP broadcast for large player counts

---

With this guide and the samples above, you can implement a client in your language of choice, manage TCP commands, and stream position updates over UDP. Happy racing!