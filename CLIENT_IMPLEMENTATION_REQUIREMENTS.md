# MP-Server Client Implementation Requirements

This document defines the mandatory client-side requirements for connecting to MP-Server.

## ❌ **Common Client Mistakes**

1. **UDP Encryption Missing**: Client sending unencrypted UDP packets — server rejects all unencrypted UDP from authenticated sessions
2. **Malformed JSON**: Client sending invalid JSON or binary data as text
3. **UTF-8 BOM Issues**: UTF-8 Byte Order Mark contamination (`0xEF 0xBB 0xBF`) — use `new UTF8Encoding(false)` / `json.dumps` without BOM
4. **Incorrect Message Format**: UDP packets must follow the JSON structure below

---

## 🔧 **MANDATORY CLIENT IMPLEMENTATION REQUIREMENTS**

### 1. **TCP Connection with TLS**

```csharp
// ✅ CORRECT: TLS connection on port 443
var tcpClient = new TcpClient();
await tcpClient.ConnectAsync("server_ip", 443);
var sslStream = new SslStream(tcpClient.GetStream());
await sslStream.AuthenticateAsClientAsync("server_hostname", null, SslProtocols.Tls12 | SslProtocols.Tls13, false);

// ❌ WRONG: Plain TCP connection
var tcpClient = new TcpClient();
await tcpClient.ConnectAsync("server_ip", 443); // Missing TLS!
```

### 2. **Authentication Flow (CRITICAL)**

The server uses **persistent, DB-backed, token-based authentication**. There is no longer a `NAME+password` combo or a separate `AUTHENTICATE` command.

**First time — Register:**
```json
{"command":"REGISTER","username":"YourPlayerName","password":"YourPassword","email":"you@example.com"}
```
Server response:
```json
{"command":"REGISTER_OK","userId":123,"username":"YourPlayerName","token":"<base64-token>"}
```

**Returning player — Login:**
```json
{"command":"LOGIN","username":"YourPlayerName","password":"YourPassword"}
```
Server response:
```json
{"command":"LOGIN_OK","userId":123,"username":"YourPlayerName","token":"<base64-token>"}
```

**⚠️ CRITICAL**: After a successful `REGISTER_OK` or `LOGIN_OK` the server activates UDP encryption for the session. UDP packets MUST be encrypted from this point forward.

**Store the token and reuse it — Auto-auth on reconnect:**
```json
{"command":"AUTO_AUTH","token":"<base64-token>"}
```
Server response:
```json
{"command":"AUTO_AUTH_OK","userId":123,"username":"YourPlayerName"}
```
Tokens are valid for **30 days**. On expiry, fall back to `LOGIN`.

**Optional — Override display name (after auth):**
```json
{"command":"NAME","name":"SpeedyDriver"}
```
Response: `{"command":"NAME_OK","name":"SpeedyDriver"}`

### 3. **UDP Encryption Implementation (MANDATORY)**

The client MUST implement AES-256 encryption for all UDP packets after authentication:

```csharp
public class UdpEncryption
{
    private readonly byte[] _key;
    private readonly byte[] _iv;
    
    public UdpEncryption(string sessionId, string sharedSecret)
    {
        // sharedSecret must match SecurityConfig:UdpSharedSecret in appsettings.json
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
        var jsonBytes = Encoding.UTF8.GetBytes(jsonData);
        return encryptor.TransformFinalBlock(jsonBytes, 0, jsonBytes.Length);
    }
    
    public string Decrypt(byte[] encryptedData)
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
}
```

### 4. **Correct UDP Message Format**

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
  "input": {
    "throttle": 0.8,
    "brake": 0.0,
    "steering": -0.3
  }
}
```

### 5. **Complete UDP Implementation**

```csharp
public class RacingNetworkClient
{
    private UdpClient _udpClient;
    private UdpEncryption _udpEncryption;
    private string _sessionId;
    private bool _isAuthenticated = false;
    
    public async Task SendPositionUpdate(Vector3 position, Quaternion rotation)
    {
        if (!_isAuthenticated || _udpEncryption == null)
        {
            throw new InvalidOperationException("Must call REGISTER, LOGIN, or AUTO_AUTH before sending UDP packets");
        }
        
        var updateData = new
        {
            command = "UPDATE",
            sessionId = _sessionId,
            position = new { x = position.x, y = position.y, z = position.z },
            rotation = new { x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w }
        };
        
        // Convert to clean JSON (NO BOM!)
        string json = JsonSerializer.Serialize(updateData);
        
        // Encrypt the JSON
        byte[] encryptedData = _udpEncryption.Encrypt(json);
        
        // Send to server
        await _udpClient.SendAsync(encryptedData, encryptedData.Length, serverEndpoint);
    }
    
    private async Task OnAuthSuccess(int userId, string username, string token, string sessionId)
    {
        _sessionId = sessionId;
        _isAuthenticated = true;
        
        // Initialize UDP encryption with session ID
        _udpEncryption = new UdpEncryption(sessionId);
        
        // Persist the token for AUTO_AUTH on next connect
        SaveTokenLocally(token);

        Debug.Log($"Session active: userId={userId} username={username}");
    }
}
```

### 6. **JSON Serialization Requirements**

```csharp
// ✅ CORRECT: Clean JSON without BOM
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};
string json = JsonSerializer.Serialize(data, options);

// ❌ WRONG: Using ToString() or manual string building
string json = $"{{\"command\":\"UPDATE\",\"sessionId\":\"{sessionId}\"}}"; // Don't do this!
```

### 7. **Room Management Flow**

1. **Create Room:**
```json
{"command":"CREATE_ROOM","name":"My Race Room"}
```

2. **Join Room:**
```json
{"command":"JOIN_ROOM","roomId":"room_id_here"}
```

3. **Start Game (Host Only):**
```json
{"command":"START_GAME"}
```

4. **Handle Game Started:**
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

---

## 🛠️ **DEBUGGING THE CURRENT CLIENT**

### Issue Analysis ✅ **RESOLVED**
The error `'0xEF' is an invalid start of a value` was caused by **BOTH**:

1. **❌ CLIENT: Sending unencrypted UDP packets** after authentication
2. **❌ SERVER: Not properly handling UDP encryption** (FIXED)

### ✅ **SERVER-SIDE FIX COMPLETED**

**The server UDP processing bug has been fixed!** The server now properly:

1. **Attempts UDP decryption first** for authenticated sessions
2. **Falls back to plain JSON parsing** if decryption fails
3. **Logs detailed encryption status** for debugging

**Before (buggy):**
```csharp
string message = Encoding.UTF8.GetString(data.Span); // Always plain text
JsonDocument.Parse(message); // FAILS for encrypted packets
```

**After (fixed):**
```csharp
// Try decryption with each authenticated session's crypto
if (session.UdpCrypto != null) {
    var decrypted = session.UdpCrypto.ParsePacket<JsonElement>(data);
    // Success! Use decrypted data
}
// Fallback to plain text parsing if needed
```

### ⚠️ **CLIENT STILL NEEDS UDP ENCRYPTION**

The client team must still implement proper UDP encryption:

### Testing UDP Encryption

```csharp
// Test encryption/decryption locally first
var encryption = new UdpEncryption("test_session_id");
string testJson = "{\"command\":\"UPDATE\",\"sessionId\":\"test\"}";
byte[] encrypted = encryption.Encrypt(testJson);
string decrypted = encryption.Decrypt(encrypted);
Debug.Log($"Original: {testJson}");
Debug.Log($"Decrypted: {decrypted}");
// Should match exactly
```

---

## 📋 **CLIENT IMPLEMENTATION CHECKLIST**

- [ ] **TLS/SSL TCP connection on port 443**
- [ ] **REGISTER new account (first run) or LOGIN (returning player)**
- [ ] **Store token locally; use AUTO_AUTH on subsequent connects**
- [ ] **UDP encryption implementation with AES-256**
- [ ] **Session ID tracking after authentication**
- [ ] **Proper JSON serialization without BOM**
- [ ] **Encrypted UDP packet transmission**
- [ ] **Error handling for auth failures (REGISTER_FAILED, LOGIN_FAILED, AUTO_AUTH_FAILED)**
- [ ] **Fallback from AUTO_AUTH → LOGIN when token expires (30 days)**
- [ ] **Room creation and joining**
- [ ] **Game start handling with spawn positions**
- [ ] **Position update broadcasting**

---

## 🚨 **CRITICAL SECURITY NOTES**

1. **Never send unencrypted UDP packets after authentication — server will reject them**
2. **Always validate auth success (REGISTER_OK / LOGIN_OK / AUTO_AUTH_OK) before UDP transmission**
3. **Use the shared secret configured in `SecurityConfig:UdpSharedSecret` in `appsettings.json`**
4. **Session ID must match TCP session ID exactly**
5. **Store the auth token securely; do not expose it in logs**
6. **After 3 failed LOGIN attempts the account is locked for 30 minutes**

---

## 💡 **COMMON PITFALLS TO AVOID**

❌ **Don't:**
- Send UDP packets before authentication
- Use plain text UDP after authentication  
- Include UTF-8 BOM in JSON strings
- Manually build JSON strings
- Ignore authentication error responses
- Reuse a token after it has expired (30 days)
- Try to use `NAME+password` or `AUTHENTICATE` — those commands are removed

✅ **Do:**
- Register once, then persist the token for AUTO_AUTH
- Encrypt all UDP packets with session-specific keys
- Use proper JSON serialization libraries
- Handle all error responses from server
- Test encryption/decryption locally first
- Implement token refresh: on `AUTO_AUTH_FAILED`, call `LOGIN` to get a new token

---

This implementation guide ensures proper communication with MP-Server. UDP encryption is **mandatory** — the server rejects all unencrypted UDP packets from authenticated sessions.
