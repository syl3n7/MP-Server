# 🏁 Complete MP-Server Client Implementation Guide

## 📋 Table of Contents

1. [Overview](#overview)
2. [Server Architecture](#server-architecture)
3. [TCP Protocol Implementation](#tcp-protocol-implementation)
4. [UDP Protocol Implementation](#udp-protocol-implementation)
5. [Authentication Flow](#authentication-flow)
6. [Room Management](#room-management)
7. [JSON Message Formats](#json-message-formats)
8. [UDP Encryption](#udp-encryption)
9. [Complete Implementation Examples](#complete-implementation-examples)
10. [Error Handling](#error-handling)
11. [Testing and Debugging](#testing-and-debugging)

---

## 📖 Overview

The MP-Server is a secure multiplayer racing server that uses:
- **TLS-encrypted TCP** on port 443 for commands and room management
- **AES-encrypted UDP** on port 443 for real-time position/input updates
- **Password-based authentication** for player identity protection
- **Self-signed certificates** with automatic generation

### 🔑 Key Points for Client Developers

1. **ALWAYS** use TLS for TCP connections
2. **NEVER** send UDP packets before authentication
3. **ALWAYS** encrypt UDP packets after authentication
4. **CRITICAL**: Session ID must match between TCP and UDP
5. **MANDATORY**: Use exact shared secret `"RacingServerUDP2024!"`

---

## 🏗️ Server Architecture

### Connection Flow
```
1. Client connects to TCP port 443 with TLS
2. Server sends welcome message: "CONNECTED|{sessionId}\n"
3. Client sets name and password
4. Server authenticates and enables UDP encryption
5. Client can now send encrypted UDP packets
6. Client joins/creates rooms for gameplay
```

### Security Layers
- **TLS 1.2/1.3** for TCP communication
- **AES-256-CBC** for UDP encryption
- **SHA-256** password hashing (server-side)
- **Session-based** access control

---

## 📡 TCP Protocol Implementation

### 🔗 Connection Setup

```csharp
// CORRECT: TLS connection on port 443
var tcpClient = new TcpClient();
await tcpClient.ConnectAsync("server_ip", 443);

var sslStream = new SslStream(tcpClient.GetStream(), false, ValidateServerCertificate);
await sslStream.AuthenticateAsClientAsync("server_hostname");

// Setup message framing (newline-delimited JSON)
var reader = new StreamReader(sslStream);
var writer = new StreamWriter(sslStream);

// Read welcome message
string welcome = await reader.ReadLineAsync();
// Expected: "CONNECTED|{sessionId}"
string sessionId = welcome.Split('|')[1];
```

### 📨 Message Format

All TCP messages are **newline-delimited JSON**:
```
{"command":"COMMAND_NAME","param":"value"}\n
```

### 🔐 Certificate Validation

```csharp
private bool ValidateServerCertificate(object sender, X509Certificate certificate, 
    X509Chain chain, SslPolicyErrors sslPolicyErrors)
{
    // For development/LAN - accept self-signed certificates
    if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors ||
        sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
    {
        return true; // Accept self-signed for development
    }
    
    return sslPolicyErrors == SslPolicyErrors.None;
}
```

---

## 🚀 UDP Protocol Implementation

### 📦 Packet Format

**For Authenticated Players (MANDATORY):**
```
[4-byte length header][AES-encrypted JSON data]
```

**For Unauthenticated Players (Limited):**
```
Raw JSON string
```

### 🔒 UDP Encryption Setup

```csharp
public class UdpEncryption
{
    private readonly byte[] _key;
    private readonly byte[] _iv;
    
    public UdpEncryption(string sessionId, string sharedSecret = "RacingServerUDP2024!")
    {
        // CRITICAL: Must match server implementation exactly
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
        
        using var encryptor = aes.CreateEncryptor();
        var dataBytes = Encoding.UTF8.GetBytes(jsonData);
        return encryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
    }
    
    public byte[] CreatePacket(object data)
    {
        var json = JsonSerializer.Serialize(data);
        var encrypted = Encrypt(json);
        var packet = new byte[4 + encrypted.Length];
        
        // Add length header (little-endian)
        BitConverter.GetBytes(encrypted.Length).CopyTo(packet, 0);
        encrypted.CopyTo(packet, 4);
        
        return packet;
    }
}
```

---

## 🔐 Authentication Flow

### Step 1: Set Player Name with Password

**Client Request:**
```json
{
  "command": "NAME",
  "name": "PlayerName",
  "password": "YourPassword"
}
```

**Server Response (Success):**
```json
{
  "command": "NAME_OK",
  "name": "PlayerName",
  "authenticated": true,
  "udpEncryption": true
}
```

**Server Response (Failure):**
```json
{
  "command": "AUTH_FAILED",
  "message": "Invalid password for this player name."
}
```

### Step 2: Initialize UDP Encryption

```csharp
private async Task OnAuthenticationSuccess(string sessionId)
{
    _sessionId = sessionId;
    _isAuthenticated = true;
    
    // MANDATORY: Initialize UDP encryption
    _udpEncryption = new UdpEncryption(sessionId);
    
    Console.WriteLine("UDP encryption initialized for session: " + sessionId);
}
```

### Step 3: Alternative Authentication

If name is set without password first:

**Client Request:**
```json
{
  "command": "AUTHENTICATE",
  "password": "YourPassword"
}
```

**Server Response:**
```json
{
  "command": "AUTH_OK",
  "name": "PlayerName"
}
```

---

## 🏠 Room Management

### Commands That Require Authentication

**❌ Unauthenticated** players can only use:
- `NAME`
- `AUTHENTICATE`
- `PING`
- `BYE`
- `PLAYER_INFO`
- `LIST_ROOMS`

**✅ Authenticated** players can use all commands.

### Create Room

**Client Request:**
```json
{
  "command": "CREATE_ROOM",
  "name": "My Race Room"
}
```

**Server Response:**
```json
{
  "command": "ROOM_CREATED",
  "roomId": "generated_room_id",
  "name": "My Race Room"
}
```

### Join Room

**Client Request:**
```json
{
  "command": "JOIN_ROOM",
  "roomId": "target_room_id"
}
```

**Server Response (Success):**
```json
{
  "command": "JOIN_OK",
  "roomId": "target_room_id"
}
```

**Server Response (Failure):**
```json
{
  "command": "ERROR",
  "message": "Failed to join room. Room may be full or inactive."
}
```

### Start Game (Host Only)

**Client Request:**
```json
{
  "command": "START_GAME"
}
```

**Server Response:**
```json
{
  "command": "GAME_STARTED",
  "roomId": "room_id",
  "hostId": "host_session_id",
  "spawnPositions": {
    "player1_session_id": {"x": 66, "y": -2, "z": 0.8},
    "player2_session_id": {"x": 60, "y": -2, "z": 0.8}
  }
}
```

### List Rooms

**Client Request:**
```json
{
  "command": "LIST_ROOMS"
}
```

**Server Response:**
```json
{
  "command": "ROOM_LIST",
  "rooms": [
    {
      "id": "room_id",
      "name": "Room Name",
      "playerCount": 2,
      "isActive": false,
      "hostId": "host_session_id"
    }
  ]
}
```

---

## 📄 JSON Message Formats

### Complete TCP Command Reference

| Command | Direction | Request Format | Response Format | Auth Required |
|---------|-----------|----------------|-----------------|---------------|
| `NAME` | Client→Server | `{"command":"NAME","name":"player","password":"secret"}` | `{"command":"NAME_OK","name":"player","authenticated":true,"udpEncryption":true}` | No |
| `AUTHENTICATE` | Client→Server | `{"command":"AUTHENTICATE","password":"secret"}` | `{"command":"AUTH_OK","name":"player"}` | No |
| `CREATE_ROOM` | Client→Server | `{"command":"CREATE_ROOM","name":"roomName"}` | `{"command":"ROOM_CREATED","roomId":"id","name":"roomName"}` | Yes |
| `JOIN_ROOM` | Client→Server | `{"command":"JOIN_ROOM","roomId":"id"}` | `{"command":"JOIN_OK","roomId":"id"}` | Yes |
| `LEAVE_ROOM` | Client→Server | `{"command":"LEAVE_ROOM"}` | `{"command":"LEAVE_OK","roomId":"id"}` | Yes |
| `START_GAME` | Client→Server | `{"command":"START_GAME"}` | `{"command":"GAME_STARTED","roomId":"id","hostId":"hostId","spawnPositions":{...}}` | Yes |
| `LIST_ROOMS` | Client→Server | `{"command":"LIST_ROOMS"}` | `{"command":"ROOM_LIST","rooms":[...]}` | No |
| `GET_ROOM_PLAYERS` | Client→Server | `{"command":"GET_ROOM_PLAYERS"}` | `{"command":"ROOM_PLAYERS","roomId":"id","players":[...]}` | Yes |
| `RELAY_MESSAGE` | Client→Server | `{"command":"RELAY_MESSAGE","targetId":"playerId","message":"text"}` | `{"command":"RELAY_OK","targetId":"playerId"}` | Yes |
| `PLAYER_INFO` | Client→Server | `{"command":"PLAYER_INFO"}` | `{"command":"PLAYER_INFO","playerInfo":{"id":"id","name":"name","currentRoomId":"roomId"}}` | No |
| `PING` | Client→Server | `{"command":"PING"}` | `{"command":"PONG"}` | No |
| `BYE` | Client→Server | `{"command":"BYE"}` | `{"command":"BYE_OK"}` | No |

### UDP Message Formats

**Position Update (MUST BE ENCRYPTED):**
```json
{
  "command": "UPDATE",
  "sessionId": "your_tcp_session_id",
  "position": {
    "x": 10.5,
    "y": 2.0,
    "z": 15.3
  },
  "rotation": {
    "x": 0.0,
    "y": 0.707,
    "z": 0.0,
    "w": 0.707
  }
}
```

**Input Update (MUST BE ENCRYPTED):**
```json
{
  "command": "INPUT",
  "sessionId": "your_tcp_session_id",
  "roomId": "current_room_id",
  "input": {
    "steering": -0.3,
    "throttle": 0.8,
    "brake": 0.0,
    "timestamp": 123.456
  },
  "client_id": "your_tcp_session_id"
}
```

---

## 🔒 UDP Encryption

### Critical Implementation Details

1. **Key Derivation** must match server exactly:
   ```csharp
   var keyMaterial = sessionId + "RacingServerUDP2024!";
   var hash = SHA256.ComputeHash(Encoding.UTF8.GetBytes(keyMaterial));
   ```

2. **Packet Format** must include length header:
   ```csharp
   [4-byte length][encrypted JSON data]
   ```

3. **Session ID** in UDP packets must match TCP session

4. **Never send plain UDP** after authentication

### Sending Encrypted UDP

```csharp
public async Task SendPositionUpdate(Vector3 position, Quaternion rotation)
{
    if (!_isAuthenticated || _udpEncryption == null)
    {
        throw new InvalidOperationException("Must authenticate before sending UDP");
    }
    
    var updateData = new
    {
        command = "UPDATE",
        sessionId = _sessionId,
        position = new { x = position.x, y = position.y, z = position.z },
        rotation = new { x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w }
    };
    
    // Create encrypted packet
    var packet = _udpEncryption.CreatePacket(updateData);
    
    // Send to server
    await _udpClient.SendAsync(packet, packet.Length, _serverEndpoint);
}
```

---

## 💻 Complete Implementation Examples

### C# Console Client

```csharp
using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net;

public class RacingClient
{
    private TcpClient _tcpClient;
    private SslStream _sslStream;
    private StreamReader _reader;
    private StreamWriter _writer;
    private UdpClient _udpClient;
    private UdpEncryption _udpEncryption;
    private string _sessionId;
    private bool _isAuthenticated;
    private IPEndPoint _serverEndpoint;
    
    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);
            
            _sslStream = new SslStream(_tcpClient.GetStream(), false, ValidateServerCertificate);
            await _sslStream.AuthenticateAsClientAsync(host);
            
            _reader = new StreamReader(_sslStream);
            _writer = new StreamWriter(_sslStream) { AutoFlush = true };
            
            // Read welcome message
            string welcome = await _reader.ReadLineAsync();
            if (welcome?.StartsWith("CONNECTED|") == true)
            {
                _sessionId = welcome.Split('|')[1];
                _serverEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
                
                // Start listening for messages
                _ = Task.Run(ListenForMessages);
                
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> AuthenticateAsync(string username, string password)
    {
        try
        {
            var command = new { command = "NAME", name = username, password = password };
            await SendCommand(command);
            
            // Wait for authentication response (handled in ListenForMessages)
            await Task.Delay(1000);
            return _isAuthenticated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task SendPositionUpdate(float x, float y, float z, float rx, float ry, float rz, float rw)
    {
        if (!_isAuthenticated || _udpEncryption == null) return;
        
        var update = new
        {
            command = "UPDATE",
            sessionId = _sessionId,
            position = new { x, y, z },
            rotation = new { x = rx, y = ry, z = rz, w = rw }
        };
        
        var packet = _udpEncryption.CreatePacket(update);
        await _udpClient.SendAsync(packet, packet.Length, _serverEndpoint);
    }
    
    private async Task SendCommand(object command)
    {
        string json = JsonSerializer.Serialize(command);
        await _writer.WriteLineAsync(json);
        Console.WriteLine($"Sent: {json}");
    }
    
    private async Task ListenForMessages()
    {
        try
        {
            while (_tcpClient.Connected)
            {
                string message = await _reader.ReadLineAsync();
                if (message == null) break;
                
                Console.WriteLine($"Received: {message}");
                await ProcessMessage(message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Listen error: {ex.Message}");
        }
    }
    
    private async Task ProcessMessage(string message)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(message);
            string command = json.GetProperty("command").GetString();
            
            switch (command)
            {
                case "NAME_OK":
                    _isAuthenticated = json.GetProperty("authenticated").GetBoolean();
                    if (_isAuthenticated && json.TryGetProperty("udpEncryption", out var udpEl) && udpEl.GetBoolean())
                    {
                        SetupUdpEncryption();
                    }
                    Console.WriteLine($"Authentication successful: {_isAuthenticated}");
                    break;
                    
                case "AUTH_FAILED":
                    Console.WriteLine($"Authentication failed: {json.GetProperty("message").GetString()}");
                    break;
                    
                case "ROOM_CREATED":
                    string roomId = json.GetProperty("roomId").GetString();
                    Console.WriteLine($"Room created: {roomId}");
                    break;
                    
                case "GAME_STARTED":
                    Console.WriteLine("Game started!");
                    if (json.TryGetProperty("spawnPositions", out var spawnEl))
                    {
                        Console.WriteLine($"Spawn positions: {spawnEl}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Message processing error: {ex.Message}");
        }
    }
    
    private void SetupUdpEncryption()
    {
        _udpEncryption = new UdpEncryption(_sessionId);
        _udpClient = new UdpClient();
        Console.WriteLine("UDP encryption enabled");
    }
    
    private bool ValidateServerCertificate(object sender, X509Certificate certificate, 
        X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // Accept self-signed certificates for development
        return true;
    }
}
```

### Unity Implementation

```csharp
using UnityEngine;
using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net;

public class UnityRacingClient : MonoBehaviour
{
    [Header("Connection Settings")]
    public string serverHost = "localhost";
    public int serverPort = 443;
    
    [Header("Authentication")]
    public string playerName = "UnityPlayer";
    public string password = "password123";
    
    private TcpClient _tcpClient;
    private SslStream _sslStream;
    private UdpClient _udpClient;
    private UdpEncryption _udpEncryption;
    private string _sessionId;
    private bool _isAuthenticated;
    private bool _isConnected;
    
    public async void Connect()
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(serverHost, serverPort);
            
            _sslStream = new SslStream(_tcpClient.GetStream(), false, (sender, cert, chain, errors) => true);
            await _sslStream.AuthenticateAsClientAsync(serverHost);
            
            // Read welcome message
            byte[] buffer = new byte[1024];
            int bytesRead = await _sslStream.ReadAsync(buffer, 0, buffer.Length);
            string welcome = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            
            if (welcome.StartsWith("CONNECTED|"))
            {
                _sessionId = welcome.Split('|')[1];
                _isConnected = true;
                
                Debug.Log($"Connected with session ID: {_sessionId}");
                
                // Authenticate
                await Authenticate();
                
                // Start message listening
                _ = ListenForTcpMessages();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Connection failed: {ex.Message}");
        }
    }
    
    private async Task Authenticate()
    {
        var authCommand = new
        {
            command = "NAME",
            name = playerName,
            password = password
        };
        
        await SendTcpMessage(authCommand);
    }
    
    private async Task SendTcpMessage(object message)
    {
        if (!_isConnected) return;
        
        string json = JsonSerializer.Serialize(message) + "\n";
        byte[] data = Encoding.UTF8.GetBytes(json);
        await _sslStream.WriteAsync(data, 0, data.Length);
        
        Debug.Log($"Sent: {json.Trim()}");
    }
    
    public async void SendPositionUpdate()
    {
        if (!_isAuthenticated || _udpEncryption == null) return;
        
        var position = transform.position;
        var rotation = transform.rotation;
        
        var update = new
        {
            command = "UPDATE",
            sessionId = _sessionId,
            position = new { x = position.x, y = position.y, z = position.z },
            rotation = new { x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w }
        };
        
        var packet = _udpEncryption.CreatePacket(update);
        await _udpClient.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Parse(serverHost), serverPort));
    }
    
    private async Task ListenForTcpMessages()
    {
        byte[] buffer = new byte[4096];
        StringBuilder messageBuffer = new StringBuilder();
        
        try
        {
            while (_isConnected)
            {
                int bytesRead = await _sslStream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                
                string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuffer.Append(chunk);
                
                // Process complete messages (newline-delimited)
                string content = messageBuffer.ToString();
                string[] lines = content.Split('\n');
                
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        ProcessTcpMessage(lines[i]);
                    }
                }
                
                // Keep incomplete message in buffer
                messageBuffer.Clear();
                if (!string.IsNullOrEmpty(lines[lines.Length - 1]))
                {
                    messageBuffer.Append(lines[lines.Length - 1]);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"TCP listen error: {ex.Message}");
        }
    }
    
    private void ProcessTcpMessage(string message)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(message);
            string command = json.GetProperty("command").GetString();
            
            Debug.Log($"Received: {message}");
            
            switch (command)
            {
                case "NAME_OK":
                    _isAuthenticated = json.GetProperty("authenticated").GetBoolean();
                    if (_isAuthenticated && json.TryGetProperty("udpEncryption", out var udpEl) && udpEl.GetBoolean())
                    {
                        SetupUdpEncryption();
                    }
                    Debug.Log($"Authentication successful: {_isAuthenticated}");
                    break;
                    
                case "AUTH_FAILED":
                    Debug.LogError($"Authentication failed: {json.GetProperty("message").GetString()}");
                    break;
                    
                case "GAME_STARTED":
                    Debug.Log("Game started!");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Message processing error: {ex.Message}");
        }
    }
    
    private void SetupUdpEncryption()
    {
        _udpEncryption = new UdpEncryption(_sessionId);
        _udpClient = new UdpClient();
        Debug.Log("UDP encryption enabled");
    }
    
    void Update()
    {
        // Send position updates at ~20 FPS
        if (_isAuthenticated && Time.time % 0.05f < Time.deltaTime)
        {
            SendPositionUpdate();
        }
    }
    
    void OnDestroy()
    {
        _isConnected = false;
        _udpClient?.Close();
        _sslStream?.Close();
        _tcpClient?.Close();
    }
}
```

---

## ❌ Error Handling

### Common TCP Errors

| Error Response | Cause | Solution |
|---------------|-------|----------|
| `{"command":"AUTH_FAILED","message":"Invalid password for this player name."}` | Wrong password | Use correct password or different username |
| `{"command":"ERROR","message":"Authentication required. Please use NAME command with password."}` | Trying restricted command without auth | Authenticate first |
| `{"command":"ERROR","message":"Cannot start game. Only the host can start the game."}` | Non-host trying to start | Only room host can start games |
| `{"command":"ERROR","message":"Room not found."}` | Invalid room ID | Use LIST_ROOMS to get valid room IDs |
| `{"command":"UNKNOWN_COMMAND","originalCommand":"cmd"}` | Invalid command | Check command spelling and format |

### UDP Debugging

```csharp
public async Task SendUdpWithLogging(object data)
{
    try
    {
        string json = JsonSerializer.Serialize(data);
        Debug.Log($"Sending UDP: {json}");
        
        if (_udpEncryption != null)
        {
            var packet = _udpEncryption.CreatePacket(data);
            Debug.Log($"Packet size: {packet.Length} bytes (encrypted)");
            await _udpClient.SendAsync(packet, packet.Length, _serverEndpoint);
        }
        else
        {
            Debug.LogWarning("UDP encryption not available - data not sent");
        }
    }
    catch (Exception ex)
    {
        Debug.LogError($"UDP send error: {ex.Message}");
    }
}
```

---

## 🧪 Testing and Debugging

### Client Implementation Checklist

- [ ] **TLS/SSL TCP connection on port 443**
- [ ] **Complete authentication flow (NAME → AUTH_OK)**
- [ ] **UDP encryption implementation with AES-256**
- [ ] **Session ID tracking after authentication**
- [ ] **Proper JSON serialization without BOM**
- [ ] **Encrypted UDP packet transmission**
- [ ] **Error handling for authentication failures**
- [ ] **Room creation and joining**
- [ ] **Game start handling with spawn positions**
- [ ] **Position update broadcasting**

### Testing UDP Encryption

```csharp
// Test encryption/decryption locally
var encryption = new UdpEncryption("test_session_id");
string testJson = "{\"command\":\"UPDATE\",\"sessionId\":\"test\"}";
byte[] encrypted = encryption.Encrypt(testJson);
byte[] packet = encryption.CreatePacket(new { command = "UPDATE", sessionId = "test" });

Debug.Log($"Original: {testJson}");
Debug.Log($"Packet size: {packet.Length} bytes");
// Verify packet format: first 4 bytes should be length
```

### Server-Side Logging

The server logs will show:
- `🔓 Successfully decrypted UDP packet` for proper encryption
- `🔍 Processing plain UDP packet` for unencrypted packets
- `❌ Error parsing UDP JSON message` for malformed packets

### Network Capture

Use Wireshark to verify:
1. TCP traffic is TLS-encrypted
2. UDP packets are binary (encrypted) not plain text
3. UDP packet structure: [4 bytes][encrypted data]

---

## 🚨 Critical Security Notes

1. **Never send unencrypted UDP packets after authentication**
2. **Always validate authentication success before UDP transmission**
3. **Use the exact shared secret: `"RacingServerUDP2024!"`**
4. **Session ID must match TCP session ID exactly**
5. **All UDP communication must be encrypted**
6. **Implement proper certificate validation for production**

---

## 📞 Support and Troubleshooting

If you encounter issues:

1. **Check server logs** for detailed error messages
2. **Verify TLS connection** is established properly
3. **Confirm authentication** before sending UDP
4. **Test UDP encryption** with known test data
5. **Ensure JSON formatting** is correct (no BOM)
6. **Validate packet format** for UDP (length header + encrypted data)
7. **Use the dashboard** at `http://server-ip:8080` for real-time monitoring

### Dashboard Administration

The server provides a comprehensive web dashboard with user management capabilities:

**User Management Features:**
- View all user accounts with pagination and search
- Monitor user login activity and account status
- Administrative actions: ban/unban users, force password resets, delete accounts
- View detailed user audit logs and activity history
- Track online users and active sessions

**Dashboard API Endpoints for Integration:**
```
GET /Dashboard/GetUserStats          - User statistics overview
GET /Dashboard/GetAllUsers           - Paginated user accounts list  
GET /Dashboard/GetUserAuditLog       - User activity audit trails
POST /Dashboard/BanUserAccount       - Administrative user banning
POST /Dashboard/UnbanUserAccount     - Remove user account bans
POST /Dashboard/ForcePasswordReset   - Force user password resets
DELETE /Dashboard/DeleteUserAccount  - Delete user accounts permanently
```

The server has been thoroughly tested and debugged. Most client issues stem from:
- Missing UDP encryption implementation
- Incorrect session ID usage
- Sending UDP before authentication
- Malformed JSON or packet structure

Follow this guide exactly, and your client will work correctly with the MP-Server.
