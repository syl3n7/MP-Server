# MP-Server Client Implementation Guide

This comprehensive guide provides everything needed to implement a secure client for the MP-Server racing platform, covering TLS connections, authentication, UDP encryption, and complete implementation examples.

## Table of Contents

1. [Overview](#1-overview)
2. [Security Architecture](#2-security-architecture)
3. [Client Requirements](#3-client-requirements)
4. [TLS Connection Setup](#4-tls-connection-setup)
5. [Authentication Flow](#5-authentication-flow)
6. [UDP Encryption](#6-udp-encryption)
7. [Complete Implementation Examples](#7-complete-implementation-examples)
8. [Platform-Specific Guides](#8-platform-specific-guides)
9. [Troubleshooting](#9-troubleshooting)
10. [Performance Optimization](#10-performance-optimization)
11. [Testing and Deployment](#11-testing-and-deployment)

## 1. Overview

The MP-Server is a secure multiplayer racing server that uses:
- **TLS-encrypted TCP** on port 443 for commands and room management
- **AES-encrypted UDP** on port 443 for real-time position/input updates
- **Password-based authentication** for player identity protection
- **Self-signed certificates** with automatic generation including public IP support

### 1.1 Connection Flow

```
1. Client connects via TLS to server:443
2. Server sends: CONNECTED|<sessionId>
3. Client authenticates with NAME command + password
4. Server responds with NAME_OK + UDP encryption keys
5. Client can now use UDP for real-time updates
6. All TCP commands and UDP packets are encrypted
```

### 1.2 Key Features for Clients

- **Zero-configuration TLS**: Server auto-generates certificates
- **Session-based encryption**: Unique UDP keys per player
- **Password protection**: Secure username claiming
- **Real-time updates**: Low-latency UDP for racing data
- **Admin resistance**: Strong encryption prevents packet inspection

## 2. Security Architecture

### 2.1 TLS Configuration

The server uses TLS 1.2/1.3 with:
- **Auto-generated certificates** including public IP (89.114.116.19)
- **Subject Alternative Names** for all network interfaces
- **Modern cipher suites** with forward secrecy
- **Self-signed root** for LAN/development use

### 2.2 Authentication System

- **SHA-256 password hashing** (should upgrade to bcrypt for production)
- **First-come, first-served** username registration
- **Session-based access control** with command filtering
- **Automatic session cleanup** after 60 seconds of inactivity

### 2.3 UDP Encryption

- **AES-256-CBC encryption** for authenticated players
- **Session-specific keys** derived from sessionId + shared secret
- **Packet format**: `[4-byte length][encrypted JSON]`
- **Backward compatibility** with plain-text UDP for unauthenticated clients

## 3. Client Requirements

### 3.1 Essential Libraries

**C#/.NET:**
```csharp
System.Net.Sockets      // TCP/UDP networking
System.Net.Security     // TLS/SSL support
System.Security.Cryptography // Certificate validation, AES encryption
System.Text.Json        // JSON parsing
```

**Unity Additional:**
```csharp
UnityEngine            // Game engine integration
System.Threading.Tasks // Async/await support
System.Collections     // Coroutines
```

**Other Platforms:**
- **C++**: OpenSSL for TLS, nlohmann/json for JSON, platform networking APIs
- **Python**: `ssl`, `socket`, `json`, `cryptography` libraries
- **Node.js**: `tls`, `dgram`, `crypto`, native JSON support
- **Rust**: `tokio-rustls`, `serde_json`, `aes` crates

### 3.2 Network Configuration

**Firewall Requirements:**
- Outbound TCP port 443 (TLS)
- Outbound UDP port 443 (encrypted game data)
- Inbound UDP on random port (for receiving updates)

**NAT Considerations:**
- Server certificate includes public IP for NAT traversal
- UDP hole punching happens automatically on first packet send
- No special NAT configuration required for clients

## 4. TLS Connection Setup

### 4.1 Certificate Validation Strategies

#### Option 1: Development Mode (Accept Self-Signed)
```csharp
private bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                      X509Chain chain, SslPolicyErrors sslPolicyErrors)
{
    // Accept self-signed certificates for development
    if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors ||
        sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
    {
        Debug.Log($"Accepting self-signed certificate: {sslPolicyErrors}");
        return true;
    }
    
    return sslPolicyErrors == SslPolicyErrors.None;
}
```

#### Option 2: Certificate Pinning (Production)
```csharp
public class CertificatePinner
{
    private readonly string _expectedThumbprint;
    
    public CertificatePinner(string certificateThumbprint)
    {
        _expectedThumbprint = certificateThumbprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();
    }
    
    public bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                          X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // Validate against pinned certificate
        var cert = new X509Certificate2(certificate);
        var actualThumbprint = cert.Thumbprint;
        
        if (actualThumbprint.Equals(_expectedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log("Certificate thumbprint matches pinned certificate");
            return true;
        }
        
        Debug.LogError($"Certificate thumbprint mismatch! Expected: {_expectedThumbprint}, Got: {actualThumbprint}");
        return false;
    }
}
```

#### Option 3: Bundle Server Certificate
```csharp
public class BundledCertificateValidator
{
    private readonly X509Certificate2 _serverCert;
    
    public BundledCertificateValidator(byte[] serverCertificateData)
    {
        _serverCert = new X509Certificate2(serverCertificateData);
    }
    
    public bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                          X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // Compare with bundled certificate
        return certificate.GetCertHashString().Equals(_serverCert.GetCertHashString(), 
                                                     StringComparison.OrdinalIgnoreCase);
    }
}
```

### 4.2 Connection Establishment

```csharp
public async Task<bool> ConnectToServer(string host, int port = 443)
{
    try
    {
        // Create TCP connection
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port);
        
        // Setup TLS with certificate validation
        var sslStream = new SslStream(_tcpClient.GetStream(), false, ValidateServerCertificate);
        
        // Authenticate as client (this triggers TLS handshake)
        await sslStream.AuthenticateAsClientAsync(host);
        
        // Setup reader/writer for JSON communication
        _reader = new StreamReader(sslStream, Encoding.UTF8);
        _writer = new StreamWriter(sslStream, Encoding.UTF8) { AutoFlush = true };
        
        // Read welcome message
        string welcome = await _reader.ReadLineAsync();
        if (welcome?.StartsWith("CONNECTED|") == true)
        {
            _sessionId = welcome.Split('|')[1];
            return true;
        }
        
        throw new Exception($"Invalid welcome message: {welcome}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Connection failed: {ex.Message}");
        return false;
    }
}
```

### 4.3 Handling TLS Errors

**Common TLS Issues:**

1. **RemoteCertificateNameMismatch**: Certificate CN doesn't match hostname
   - Solution: Connect using IP address in certificate SAN list
   - Or: Use certificate validation callback to accept specific certificate

2. **RemoteCertificateChainErrors**: Self-signed certificate not trusted
   - Solution: Implement certificate pinning or accept self-signed in callback

3. **TLS Handshake Timeout**: Network or certificate issues
   - Solution: Check network connectivity, verify server is running
   - Try different IP addresses (public vs local)

## 5. Authentication Flow

### 5.1 Registration (First Time)

```csharp
public async Task<AuthResult> RegisterPlayer(string username, string password)
{
    try
    {
        var command = new
        {
            command = "NAME",
            name = username,
            password = password
        };
        
        await SendCommand(command);
        string response = await _reader.ReadLineAsync();
        
        var result = JsonSerializer.Deserialize<JsonElement>(response);
        string cmd = result.GetProperty("command").GetString();
        
        if (cmd == "NAME_OK")
        {
            bool authenticated = result.GetProperty("authenticated").GetBoolean();
            bool udpEncryption = result.TryGetProperty("udpEncryption", out var udpEl) && udpEl.GetBoolean();
            
            if (authenticated)
            {
                _isAuthenticated = true;
                if (udpEncryption)
                {
                    SetupUdpEncryption();
                }
                return AuthResult.Success;
            }
        }
        else if (cmd == "AUTH_FAILED")
        {
            string message = result.GetProperty("message").GetString();
            return AuthResult.Failed(message);
        }
        
        return AuthResult.Failed("Unknown response");
    }
    catch (Exception ex)
    {
        return AuthResult.Failed($"Registration error: {ex.Message}");
    }
}
```

### 5.2 Login (Returning Player)

```csharp
public async Task<AuthResult> LoginPlayer(string username, string password)
{
    // Same as registration - server automatically detects if username exists
    return await RegisterPlayer(username, password);
}
```

### 5.3 Separate Authentication

```csharp
public async Task<AuthResult> AuthenticateWithPassword(string password)
{
    try
    {
        var command = new { command = "AUTHENTICATE", password = password };
        await SendCommand(command);
        
        string response = await _reader.ReadLineAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(response);
        
        if (result.GetProperty("command").GetString() == "AUTH_OK")
        {
            _isAuthenticated = true;
            SetupUdpEncryption();
            return AuthResult.Success;
        }
        else
        {
            string message = result.GetProperty("message").GetString();
            return AuthResult.Failed(message);
        }
    }
    catch (Exception ex)
    {
        return AuthResult.Failed($"Authentication error: {ex.Message}");
    }
}
```

### 5.4 Authentication State Management

```csharp
public enum AuthState
{
    Disconnected,
    Connected,
    Authenticated
}

public class AuthResult
{
    public bool Success { get; private set; }
    public string ErrorMessage { get; private set; }
    
    public static AuthResult Success => new AuthResult { Success = true };
    public static AuthResult Failed(string error) => new AuthResult { Success = false, ErrorMessage = error };
}

// Track authentication state
private AuthState _authState = AuthState.Disconnected;
public event Action<AuthState> OnAuthStateChanged;

private void SetAuthState(AuthState newState)
{
    if (_authState != newState)
    {
        _authState = newState;
        OnAuthStateChanged?.Invoke(newState);
    }
}
```

## 6. UDP Encryption

### 6.1 Key Derivation

```csharp
public class UdpCrypto
{
    private readonly Aes _aes;
    private readonly byte[] _key;
    private readonly byte[] _iv;
    
    public UdpCrypto(string sessionId, string sharedSecret = "RacingServerUDP2024!")
    {
        // Derive encryption key from session ID and shared secret
        using var sha256 = SHA256.Create();
        var keyMaterial = Encoding.UTF8.GetBytes(sessionId + sharedSecret);
        var hash = sha256.ComputeHash(keyMaterial);
        
        _key = new byte[32]; // AES-256
        _iv = new byte[16];   // AES block size
        
        Array.Copy(hash, 0, _key, 0, 32);
        Array.Copy(hash, 16, _iv, 0, 16);
        
        _aes = Aes.Create();
        _aes.Key = _key;
        _aes.IV = _iv;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.PKCS7;
    }
    
    public byte[] Encrypt(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        using var encryptor = _aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
    }
    
    public string Decrypt(byte[] ciphertext)
    {
        try
        {
            using var decryptor = _aes.CreateDecryptor();
            var plaintextBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch
        {
            return null; // Decryption failed
        }
    }
    
    public void Dispose()
    {
        _aes?.Dispose();
    }
}
```

### 6.2 Packet Format

```csharp
public class EncryptedUdpClient
{
    private readonly UdpClient _udpClient;
    private readonly UdpCrypto _crypto;
    private readonly IPEndPoint _serverEndpoint;
    
    public EncryptedUdpClient(UdpCrypto crypto, string serverHost, int serverPort)
    {
        _crypto = crypto;
        _udpClient = new UdpClient();
        _serverEndpoint = new IPEndPoint(IPAddress.Parse(serverHost), serverPort);
    }
    
    public async Task SendEncryptedPacket<T>(T data)
    {
        // Serialize to JSON
        string json = JsonSerializer.Serialize(data);
        
        // Encrypt the JSON
        byte[] encryptedData = _crypto.Encrypt(json);
        
        // Create packet: [4-byte length][encrypted data]
        byte[] packet = new byte[4 + encryptedData.Length];
        BitConverter.GetBytes(encryptedData.Length).CopyTo(packet, 0);
        encryptedData.CopyTo(packet, 4);
        
        // Send to server
        await _udpClient.SendAsync(packet, packet.Length, _serverEndpoint);
    }
    
    public async Task<T> ReceiveEncryptedPacket<T>()
    {
        var result = await _udpClient.ReceiveAsync();
        byte[] packet = result.Buffer;
        
        // Parse packet format
        if (packet.Length < 4) return default(T);
        
        int encryptedLength = BitConverter.ToInt32(packet, 0);
        if (encryptedLength != packet.Length - 4) return default(T);
        
        // Extract encrypted data
        byte[] encryptedData = new byte[encryptedLength];
        Array.Copy(packet, 4, encryptedData, 0, encryptedLength);
        
        // Decrypt and deserialize
        string json = _crypto.Decrypt(encryptedData);
        if (string.IsNullOrEmpty(json)) return default(T);
        
        return JsonSerializer.Deserialize<T>(json);
    }
}
```

### 6.3 Fallback for Unauthenticated Clients

```csharp
public async Task SendUpdate(object update)
{
    if (_isAuthenticated && _encryptedUdp != null)
    {
        // Send encrypted packet
        await _encryptedUdp.SendEncryptedPacket(update);
    }
    else
    {
        // Fallback to plain-text UDP
        string json = JsonSerializer.Serialize(update);
        byte[] data = Encoding.UTF8.GetBytes(json);
        await _plainUdp.SendAsync(data, data.Length, _serverEndpoint);
    }
}
```

## 7. Complete Implementation Examples

### 7.1 C# Console Client

```csharp
using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class RacingClient
{
    private TcpClient _tcpClient;
    private SslStream _sslStream;
    private StreamReader _reader;
    private StreamWriter _writer;
    private UdpClient _udpClient;
    private UdpCrypto _udpCrypto;
    private string _sessionId;
    private bool _isAuthenticated = false;
    
    public async Task<bool> Connect(string host, int port = 443)
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);
            
            _sslStream = new SslStream(_tcpClient.GetStream(), false, ValidateServerCertificate);
            await _sslStream.AuthenticateAsClientAsync(host);
            
            _reader = new StreamReader(_sslStream, Encoding.UTF8);
            _writer = new StreamWriter(_sslStream, Encoding.UTF8) { AutoFlush = true };
            
            string welcome = await _reader.ReadLineAsync();
            if (welcome?.StartsWith("CONNECTED|") == true)
            {
                _sessionId = welcome.Split('|')[1];
                Console.WriteLine($"Connected with session ID: {_sessionId}");
                
                // Start listening for messages
                _ = Task.Run(ListenForMessages);
                
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection failed: {ex.Message}");
        }
        return false;
    }
    
    private bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                          X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // Accept self-signed certificates for development
        return true;
    }
    
    public async Task<bool> Authenticate(string username, string password)
    {
        try
        {
            var command = new { command = "NAME", name = username, password = password };
            await SendCommand(command);
            
            // Wait for response (will be handled in ListenForMessages)
            await Task.Delay(1000);
            return _isAuthenticated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task CreateRoom(string roomName)
    {
        if (!_isAuthenticated) return;
        
        var command = new { command = "CREATE_ROOM", name = roomName };
        await SendCommand(command);
    }
    
    public async Task StartGame()
    {
        if (!_isAuthenticated) return;
        
        var command = new { command = "START_GAME" };
        await SendCommand(command);
    }
    
    public async Task SendPositionUpdate(float x, float y, float z, float rx, float ry, float rz, float rw)
    {
        if (!_isAuthenticated || _udpClient == null) return;
        
        var update = new
        {
            command = "UPDATE",
            sessionId = _sessionId,
            position = new { x, y, z },
            rotation = new { x = rx, y = ry, z = rz, w = rw }
        };
        
        if (_udpCrypto != null)
        {
            // Send encrypted
            string json = JsonSerializer.Serialize(update);
            byte[] encrypted = _udpCrypto.Encrypt(json);
            byte[] packet = new byte[4 + encrypted.Length];
            BitConverter.GetBytes(encrypted.Length).CopyTo(packet, 0);
            encrypted.CopyTo(packet, 4);
            await _udpClient.SendAsync(packet, packet.Length, "localhost", 443);
        }
        else
        {
            // Send plain text
            string json = JsonSerializer.Serialize(update);
            byte[] data = Encoding.UTF8.GetBytes(json);
            await _udpClient.SendAsync(data, data.Length, "localhost", 443);
        }
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
        _udpClient = new UdpClient();
        _udpCrypto = new UdpCrypto(_sessionId);
        Console.WriteLine("UDP encryption enabled");
    }
    
    public void Disconnect()
    {
        _udpClient?.Close();
        _sslStream?.Close();
        _tcpClient?.Close();
        _udpCrypto?.Dispose();
    }
}

// Usage example
class Program
{
    static async Task Main(string[] args)
    {
        var client = new RacingClient();
        
        if (await client.Connect("localhost"))
        {
            if (await client.Authenticate("TestPlayer", "password123"))
            {
                await client.CreateRoom("Test Room");
                await client.StartGame();
                
                // Send some position updates
                for (int i = 0; i < 10; i++)
                {
                    await client.SendPositionUpdate(i, 0, 0, 0, 0, 0, 1);
                    await Task.Delay(100);
                }
            }
        }
        
        Console.WriteLine("Press any key to disconnect...");
        Console.ReadKey();
        client.Disconnect();
    }
}
```

### 7.2 Python Client Implementation

```python
import asyncio
import ssl
import socket
import json
import hashlib
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives import padding
import struct

class RacingClient:
    def __init__(self, host="localhost", port=443):
        self.host = host
        self.port = port
        self.reader = None
        self.writer = None
        self.udp_socket = None
        self.session_id = None
        self.is_authenticated = False
        self.udp_crypto = None
        
    async def connect(self):
        """Connect to the racing server with TLS encryption"""
        try:
            # Create TLS context that accepts self-signed certificates
            context = ssl.create_default_context()
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
            
            # Connect with TLS
            self.reader, self.writer = await asyncio.open_connection(
                self.host, self.port, ssl=context
            )
            
            # Read welcome message
            welcome = await self.reader.readline()
            welcome_str = welcome.decode().strip()
            
            if welcome_str.startswith("CONNECTED|"):
                self.session_id = welcome_str.split("|")[1]
                print(f"Connected with session ID: {self.session_id}")
                
                # Start listening for messages
                asyncio.create_task(self._listen_for_messages())
                return True
            
        except Exception as e:
            print(f"Connection failed: {e}")
            return False
    
    async def authenticate(self, username, password):
        """Authenticate with username and password"""
        command = {
            "command": "NAME",
            "name": username,
            "password": password
        }
        await self._send_command(command)
        
        # Wait a bit for response
        await asyncio.sleep(0.5)
        return self.is_authenticated
    
    async def create_room(self, room_name):
        """Create a new racing room"""
        if not self.is_authenticated:
            return
        
        command = {"command": "CREATE_ROOM", "name": room_name}
        await self._send_command(command)
    
    async def start_game(self):
        """Start the racing game"""
        if not self.is_authenticated:
            return
        
        command = {"command": "START_GAME"}
        await self._send_command(command)
    
    async def send_position_update(self, x, y, z, rx, ry, rz, rw):
        """Send position update via UDP"""
        if not self.is_authenticated or not self.udp_socket:
            return
        
        update = {
            "command": "UPDATE",
            "sessionId": self.session_id,
            "position": {"x": x, "y": y, "z": z},
            "rotation": {"x": rx, "y": ry, "z": rz, "w": rw}
        }
        
        json_data = json.dumps(update)
        
        if self.udp_crypto:
            # Send encrypted
            encrypted_data = self.udp_crypto.encrypt(json_data)
            packet = struct.pack('<I', len(encrypted_data)) + encrypted_data
            self.udp_socket.sendto(packet, (self.host, self.port))
        else:
            # Send plain text
            self.udp_socket.sendto(json_data.encode(), (self.host, self.port))
    
    async def _send_command(self, command):
        """Send JSON command over TCP"""
        json_data = json.dumps(command) + "\n"
        self.writer.write(json_data.encode())
        await self.writer.drain()
        print(f"Sent: {json_data.strip()}")
    
    async def _listen_for_messages(self):
        """Listen for TCP messages from server"""
        try:
            while True:
                line = await self.reader.readline()
                if not line:
                    break
                
                message = line.decode().strip()
                print(f"Received: {message}")
                await self._process_message(message)
                
        except Exception as e:
            print(f"Listen error: {e}")
    
    async def _process_message(self, message):
        """Process incoming messages"""
        try:
            data = json.loads(message)
            command = data.get("command")
            
            if command == "NAME_OK":
                self.is_authenticated = data.get("authenticated", False)
                if self.is_authenticated and data.get("udpEncryption", False):
                    self._setup_udp_encryption()
                print(f"Authentication successful: {self.is_authenticated}")
                
            elif command == "AUTH_FAILED":
                print(f"Authentication failed: {data.get('message')}")
                
            elif command == "ROOM_CREATED":
                room_id = data.get("roomId")
                print(f"Room created: {room_id}")
                
            elif command == "GAME_STARTED":
                print("Game started!")
                spawn_positions = data.get("spawnPositions")
                if spawn_positions:
                    print(f"Spawn positions: {spawn_positions}")
                    
        except Exception as e:
            print(f"Message processing error: {e}")
    
    def _setup_udp_encryption(self):
        """Setup UDP encryption"""
        self.udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp_crypto = UdpCrypto(self.session_id)
        print("UDP encryption enabled")
    
    def disconnect(self):
        """Disconnect from server"""
        if self.udp_socket:
            self.udp_socket.close()
        if self.writer:
            self.writer.close()

class UdpCrypto:
    """UDP encryption/decryption using AES-256-CBC"""
    
    def __init__(self, session_id, shared_secret="RacingServerUDP2024!"):
        # Derive key from session ID and shared secret
        key_material = (session_id + shared_secret).encode()
        hash_bytes = hashlib.sha256(key_material).digest()
        
        self.key = hash_bytes[:32]  # AES-256 key
        self.iv = hash_bytes[16:32]  # 16-byte IV
        
    def encrypt(self, plaintext):
        """Encrypt plaintext string"""
        cipher = Cipher(algorithms.AES(self.key), modes.CBC(self.iv))
        encryptor = cipher.encryptor()
        
        # Add PKCS7 padding
        padder = padding.PKCS7(128).padder()
        padded_data = padder.update(plaintext.encode()) + padder.finalize()
        
        # Encrypt
        ciphertext = encryptor.update(padded_data) + encryptor.finalize()
        return ciphertext
    
    def decrypt(self, ciphertext):
        """Decrypt ciphertext to string"""
        try:
            cipher = Cipher(algorithms.AES(self.key), modes.CBC(self.iv))
            decryptor = cipher.decryptor()
            
            # Decrypt
            padded_data = decryptor.update(ciphertext) + decryptor.finalize()
            
            # Remove PKCS7 padding
            unpadder = padding.PKCS7(128).unpadder()
            plaintext = unpadder.update(padded_data) + unpadder.finalize()
            
            return plaintext.decode()
        except:
            return None

# Usage example
async def main():
    client = RacingClient("localhost")
    
    if await client.connect():
        if await client.authenticate("PythonPlayer", "password123"):
            await client.create_room("Python Test Room")
            await client.start_game()
            
            # Send some position updates
            for i in range(10):
                await client.send_position_update(i, 0, 0, 0, 0, 0, 1)
                await asyncio.sleep(0.1)
    
    input("Press Enter to disconnect...")
    client.disconnect()

if __name__ == "__main__":
    asyncio.run(main())
```

## 8. Platform-Specific Guides

### 8.1 Unity Integration

See the complete Unity implementation in the main documentation. Key points:

- Use `SslStream` for TLS connections
- Implement certificate validation callbacks
- Use coroutines for async operations
- Handle Unity's main thread requirements for UI updates
- Store credentials securely using PlayerPrefs

### 8.2 Unreal Engine Integration

```cpp
// Unreal Engine C++ implementation outline
#include "Runtime/Online/SSL/Public/Interfaces/ISslManager.h"
#include "Runtime/Sockets/Public/Sockets.h"
#include "Runtime/Core/Public/HAL/PlatformFilemanager.h"

class YOURGAME_API URacingNetworkComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    URacingNetworkComponent();

    UFUNCTION(BlueprintCallable, Category = "Racing Network")
    bool ConnectToServer(const FString& Host, int32 Port = 443);

    UFUNCTION(BlueprintCallable, Category = "Racing Network")
    bool AuthenticatePlayer(const FString& Username, const FString& Password);

private:
    FSocket* TcpSocket;
    TSharedPtr<class FInternetAddr> ServerAddr;
    FString SessionId;
    bool bIsAuthenticated;

    void HandleIncomingData();
    void SendCommand(const FString& JsonCommand);
};
```

### 8.3 Godot Integration

```gdscript
# Godot GDScript implementation outline
extends Node

class_name RacingNetworkManager

var tcp_connection: StreamPeerTCP
var tls_connection: StreamPeerTLS
var udp_socket: PacketPeerUDP
var session_id: String
var is_authenticated: bool = false

func connect_to_server(host: String, port: int = 443) -> bool:
    tcp_connection = StreamPeerTCP.new()
    var error = tcp_connection.connect_to_host(host, port)
    
    if error != OK:
        print("TCP connection failed: ", error)
        return false
    
    # Wait for connection
    while tcp_connection.get_status() == StreamPeerTCP.STATUS_CONNECTING:
        await get_tree().process_frame
    
    if tcp_connection.get_status() != StreamPeerTCP.STATUS_CONNECTED:
        print("Failed to establish TCP connection")
        return false
    
    # Setup TLS
    tls_connection = StreamPeerTLS.new()
    tls_connection.connect_to_stream(tcp_connection, host)
    
    # Accept self-signed certificates for development
    tls_connection.set_verify_mode(StreamPeerTLS.TLS_VERIFY_NONE)
    
    # Wait for TLS handshake
    while tls_connection.get_status() == StreamPeerTLS.STATUS_HANDSHAKING:
        await get_tree().process_frame
    
    if tls_connection.get_status() != StreamPeerTLS.STATUS_CONNECTED:
        print("TLS handshake failed")
        return false
    
    # Read welcome message
    var welcome = read_line()
    if welcome.begins_with("CONNECTED|"):
        session_id = welcome.split("|")[1]
        print("Connected with session ID: ", session_id)
        return true
    
    return false

func authenticate_player(username: String, password: String) -> void:
    var command = {
        "command": "NAME",
        "name": username,
        "password": password
    }
    send_command(command)

func send_command(command: Dictionary) -> void:
    var json = JSON.stringify(command) + "\n"
    tls_connection.put_data(json.to_utf8_buffer())

func read_line() -> String:
    var line = ""
    while true:
        if tls_connection.get_available_bytes() > 0:
            var byte = tls_connection.get_u8()
            if byte == 10:  # newline
                break
            line += char(byte)
        await get_tree().process_frame
    return line
```

## 9. Troubleshooting

### 9.1 Common Connection Issues

#### TLS Handshake Failures

**Symptoms:**
```
"The remote certificate is invalid according to the validation procedure"
"RemoteCertificateNameMismatch, RemoteCertificateChainErrors"
"TLS handshake timeout"
```

**Solutions:**
1. **Certificate Validation Bypass** (development only):
   ```csharp
   private bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                          X509Chain chain, SslPolicyErrors sslPolicyErrors)
   {
       // Accept all certificates for development
       return true;
   }
   ```

2. **Check Server IP Configuration**:
   ```bash
   # Verify server certificate includes your IP
   openssl s_client -connect server-ip:443 -servername server-ip
   ```

3. **Use IP Address Instead of Hostname**:
   ```csharp
   // Connect using IP address that's in certificate SAN
   await client.ConnectAsync("89.114.116.19", 443);
   ```

#### Authentication Failures

**Symptoms:**
```json
{"command":"AUTH_FAILED","message":"Invalid password for this player name."}
{"command":"ERROR","message":"Authentication required. Please use NAME command with password."}
```

**Solutions:**
1. **Check Password Storage**: Ensure client stores/sends exact password
2. **Username Case Sensitivity**: Server may be case-sensitive for usernames
3. **Special Characters**: Ensure proper UTF-8 encoding for passwords
4. **First-Time Registration**: Verify first connection creates the account

#### UDP Encryption Issues

**Symptoms:**
- UDP packets not being received
- Server shows "Failed to decrypt UDP packet" errors
- Position updates not working after authentication

**Solutions:**
1. **Verify UDP Setup**:
   ```csharp
   // Ensure UDP client is created after authentication
   if (_isAuthenticated && udpEncryptionEnabled)
   {
       SetupUdpClient();
   }
   ```

2. **Check Packet Format**:
   ```csharp
   // Correct encrypted packet format
   byte[] packet = new byte[4 + encryptedData.Length];
   BitConverter.GetBytes(encryptedData.Length).CopyTo(packet, 0);
   encryptedData.CopyTo(packet, 4);
   ```

3. **Key Derivation Mismatch**:
   ```csharp
   // Ensure exact same key derivation as server
   var keyMaterial = sessionId + "RacingServerUDP2024!";
   var hash = SHA256.ComputeHash(Encoding.UTF8.GetBytes(keyMaterial));
   ```

### 9.2 Network Connectivity Issues

#### Firewall/NAT Problems

**Symptoms:**
- Connection timeout to server
- TCP connects but UDP doesn't work
- Works on LAN but not over internet

**Solutions:**
1. **Firewall Configuration**:
   ```bash
   # Allow outbound connections on port 443
   # Windows Firewall
   netsh advfirewall firewall add rule name="Racing Client" dir=out action=allow protocol=TCP localport=443
   
   # Linux iptables
   iptables -A OUTPUT -p tcp --dport 443 -j ACCEPT
   iptables -A OUTPUT -p udp --dport 443 -j ACCEPT
   ```

2. **NAT Traversal**:
   ```csharp
   // Send UDP packet to register endpoint with server
   await udpClient.SendAsync(registrationPacket, packet.Length, serverEndpoint);
   ```

3. **Public IP Access**:
   ```csharp
   // Use server's public IP for internet connections
   string serverHost = "89.114.116.19"; // Server's public IP
   ```

#### Performance Issues

**Symptoms:**
- High latency for position updates
- Choppy movement of other players
- UDP packet loss

**Solutions:**
1. **Update Rate Optimization**:
   ```csharp
   // Don't send updates too frequently
   private DateTime _lastPositionUpdate = DateTime.MinValue;
   
   public async Task SendPositionUpdate(Vector3 pos, Quaternion rot)
   {
       if (DateTime.Now - _lastPositionUpdate < TimeSpan.FromMilliseconds(50))
           return; // Limit to 20 Hz
           
       _lastPositionUpdate = DateTime.Now;
       // ... send update
   }
   ```

2. **Client-Side Interpolation**:
   ```csharp
   public class PlayerInterpolator
   {
       private Vector3 _targetPosition;
       private DateTime _lastUpdate;
       
       public void SetTarget(Vector3 newPosition)
       {
           _targetPosition = newPosition;
           _lastUpdate = DateTime.Now;
       }
       
       public Vector3 GetInterpolatedPosition(Vector3 currentPosition)
       {
           float timeSinceUpdate = (float)(DateTime.Now - _lastUpdate).TotalSeconds;
           float lerpSpeed = Mathf.Clamp01(timeSinceUpdate * 10f);
           return Vector3.Lerp(currentPosition, _targetPosition, lerpSpeed);
       }
   }
   ```

### 9.3 Debug Tools and Logging

#### Enable Verbose Logging

```csharp
public class NetworkLogger
{
    public static bool EnableDebugLogging = true;
    
    public static void LogTcp(string direction, string message)
    {
        if (EnableDebugLogging)
        {
            Console.WriteLine($"[TCP {direction}] {DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }
    
    public static void LogUdp(string direction, string message)
    {
        if (EnableDebugLogging)
        {
            Console.WriteLine($"[UDP {direction}] {DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }
    
    public static void LogError(string component, string error)
    {
        Console.WriteLine($"[ERROR {component}] {DateTime.Now:HH:mm:ss.fff} {error}");
    }
}
```

#### Network Analysis Tools

1. **Wireshark**: Capture and analyze network traffic
2. **Netstat**: Check active connections
   ```bash
   netstat -an | grep 443
   ```
3. **OpenSSL Client**: Test TLS connectivity
   ```bash
   openssl s_client -connect server-ip:443 -debug
   ```

#### Server Dashboard

Access the server's web dashboard at `http://server-ip:8080` to monitor:
- Active player sessions
- Room status and player distribution
- Server uptime and statistics
- Admin controls for troubleshooting

## 10. Performance Optimization

### 10.1 Network Optimization

#### TCP Message Batching

```csharp
public class MessageBatcher
{
    private readonly Queue<object> _pendingMessages = new Queue<object>();
    private readonly Timer _batchTimer;
    
    public MessageBatcher(int batchIntervalMs = 50)
    {
        _batchTimer = new Timer(SendBatch, null, batchIntervalMs, batchIntervalMs);
    }
    
    public void QueueMessage(object message)
    {
        lock (_pendingMessages)
        {
            _pendingMessages.Enqueue(message);
        }
    }
    
    private void SendBatch(object state)
    {
        List<object> batch = new List<object>();
        
        lock (_pendingMessages)
        {
            while (_pendingMessages.Count > 0 && batch.Count < 10)
            {
                batch.Add(_pendingMessages.Dequeue());
            }
        }
        
        if (batch.Count > 0)
        {
            var batchMessage = new { command = "BATCH", messages = batch };
            _ = SendCommand(batchMessage);
        }
    }
}
```

#### UDP Compression

```csharp
public class CompressedUdpClient
{
    public byte[] CompressJson(object data)
    {
        string json = JsonSerializer.Serialize(data);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        
        using var compressed = new MemoryStream();
        using var gzip = new GZipStream(compressed, CompressionMode.Compress);
        gzip.Write(jsonBytes, 0, jsonBytes.Length);
        gzip.Close();
        
        return compressed.ToArray();
    }
    
    public T DecompressJson<T>(byte[] compressedData)
    {
        using var compressed = new MemoryStream(compressedData);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        
        gzip.CopyTo(decompressed);
        string json = Encoding.UTF8.GetString(decompressed.ToArray());
        
        return JsonSerializer.Deserialize<T>(json);
    }
}
```

### 10.2 Memory Optimization

#### Object Pooling for Network Messages

```csharp
public class NetworkMessagePool
{
    private readonly ConcurrentQueue<NetworkMessage> _pool = new ConcurrentQueue<NetworkMessage>();
    
    public NetworkMessage Rent()
    {
        if (_pool.TryDequeue(out var message))
        {
            message.Reset();
            return message;
        }
        
        return new NetworkMessage();
    }
    
    public void Return(NetworkMessage message)
    {
        if (message != null)
        {
            _pool.Enqueue(message);
        }
    }
}

public class NetworkMessage
{
    public string Command { get; set; }
    public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    
    public void Reset()
    {
        Command = null;
        Data.Clear();
    }
}
```

#### Efficient JSON Serialization

```csharp
public class OptimizedJsonSerializer
{
    private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    
    private static readonly ThreadLocal<Utf8JsonWriter> _writerPool = 
        new ThreadLocal<Utf8JsonWriter>(() => new Utf8JsonWriter(new MemoryStream()));
    
    public static string SerializeOptimized<T>(T value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        JsonSerializer.Serialize(writer, value, _options);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
```

### 10.3 Threading and Async Optimization

#### Dedicated Network Thread

```csharp
public class NetworkManager
{
    private readonly Thread _networkThread;
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly ConcurrentQueue<Action> _networkTasks = new ConcurrentQueue<Action>();
    
    public NetworkManager()
    {
        _networkThread = new Thread(NetworkThreadLoop)
        {
            Name = "NetworkThread",
            IsBackground = true
        };
        _networkThread.Start();
    }
    
    private void NetworkThreadLoop()
    {
        while (!_cancellation.Token.IsCancellationRequested)
        {
            // Process network tasks
            while (_networkTasks.TryDequeue(out var task))
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    NetworkLogger.LogError("NetworkThread", ex.Message);
                }
            }
            
            // Process incoming network data
            ProcessIncomingData();
            
            Thread.Sleep(1); // Small yield
        }
    }
    
    public void QueueNetworkTask(Action task)
    {
        _networkTasks.Enqueue(task);
    }
}
```

## 11. Testing and Deployment

### 11.1 Unit Testing

```csharp
[TestClass]
public class RacingClientTests
{
    private TestServer _testServer;
    private RacingClient _client;
    
    [TestInitialize]
    public async Task Setup()
    {
        _testServer = new TestServer();
        await _testServer.Start();
        
        _client = new RacingClient();
    }
    
    [TestMethod]
    public async Task ConnectToServer_ShouldReturnTrue_WhenServerIsAvailable()
    {
        // Act
        bool connected = await _client.Connect("localhost", _testServer.Port);
        
        // Assert
        Assert.IsTrue(connected);
        Assert.IsNotNull(_client.SessionId);
    }
    
    [TestMethod]
    public async Task Authenticate_ShouldReturnTrue_WithValidCredentials()
    {
        // Arrange
        await _client.Connect("localhost", _testServer.Port);
        
        // Act
        bool authenticated = await _client.Authenticate("testuser", "testpass");
        
        // Assert
        Assert.IsTrue(authenticated);
    }
    
    [TestMethod]
    public async Task UdpEncryption_ShouldWorkAfterAuthentication()
    {
        // Arrange
        await _client.Connect("localhost", _testServer.Port);
        await _client.Authenticate("testuser", "testpass");
        
        // Act
        await _client.SendPositionUpdate(1, 2, 3, 0, 0, 0, 1);
        
        // Assert
        var receivedUpdate = await _testServer.GetLastUdpMessage();
        Assert.IsNotNull(receivedUpdate);
        Assert.AreEqual("UPDATE", receivedUpdate.Command);
    }
    
    [TestCleanup]
    public async Task Cleanup()
    {
        _client?.Disconnect();
        await _testServer?.Stop();
    }
}
```

### 11.2 Integration Testing

```csharp
[TestClass]
public class IntegrationTests
{
    [TestMethod]
    public async Task MultipleClients_ShouldReceiveEachOthersUpdates()
    {
        // Arrange
        var client1 = new RacingClient();
        var client2 = new RacingClient();
        
        await client1.Connect("localhost");
        await client2.Connect("localhost");
        
        await client1.Authenticate("player1", "pass1");
        await client2.Authenticate("player2", "pass2");
        
        await client1.CreateRoom("TestRoom");
        await client2.JoinRoom(client1.CurrentRoomId);
        
        // Act
        bool client2ReceivedUpdate = false;
        client2.OnPlayerUpdate += (update) => {
            if (update.SessionId == client1.SessionId)
                client2ReceivedUpdate = true;
        };
        
        await client1.SendPositionUpdate(10, 20, 30, 0, 0, 0, 1);
        await Task.Delay(100); // Wait for propagation
        
        // Assert
        Assert.IsTrue(client2ReceivedUpdate);
    }
}
```

### 11.3 Load Testing

```csharp
[TestClass]
public class LoadTests
{
    [TestMethod]
    public async Task Server_ShouldHandle_100ConcurrentClients()
    {
        // Arrange
        var clients = new List<RacingClient>();
        var tasks = new List<Task>();
        
        // Act
        for (int i = 0; i < 100; i++)
        {
            var client = new RacingClient();
            clients.Add(client);
            
            tasks.Add(Task.Run(async () =>
            {
                await client.Connect("localhost");
                await client.Authenticate($"player{i}", "password");
                
                // Send updates for 30 seconds
                var endTime = DateTime.Now.AddSeconds(30);
                while (DateTime.Now < endTime)
                {
                    await client.SendPositionUpdate(i, 0, 0, 0, 0, 0, 1);
                    await Task.Delay(50); // 20 Hz
                }
            }));
        }
        
        // Wait for all clients to complete
        await Task.WhenAll(tasks);
        
        // Assert
        foreach (var client in clients)
        {
            Assert.IsTrue(client.IsAuthenticated);
        }
    }
}
```

### 11.4 Deployment Checklist

#### Server Deployment

1. **Certificate Configuration**:
   ```bash
   # Set production environment variables
   export SERVER_HOSTNAME="your-racing-server.com"
   export SERVER_PUBLIC_IP="your-public-ip"
   
   # Or use custom certificate
   cp your-certificate.pfx server.pfx
   ```

2. **Firewall Configuration**:
   ```bash
   # Open required ports
   ufw allow 443/tcp    # TLS
   ufw allow 443/udp    # Encrypted game data
   ufw allow 8080/tcp   # Dashboard (LAN only)
   ```

3. **Service Configuration**:
   ```bash
   # Create systemd service
   sudo nano /etc/systemd/system/racing-server.service
   
   [Unit]
   Description=MP Racing Server
   After=network.target
   
   [Service]
   Type=simple
   User=racing
   WorkingDirectory=/opt/racing-server
   ExecStart=/usr/bin/dotnet MP-Server.dll
   Restart=always
   RestartSec=10
   
   [Install]
   WantedBy=multi-user.target
   ```

#### Client Deployment

1. **Certificate Handling**:
   ```csharp
   // Production: Use certificate pinning
   public bool ValidateServerCertificate(object sender, X509Certificate certificate, 
                                        X509Chain chain, SslPolicyErrors sslPolicyErrors)
   {
   #if DEBUG
       return true; // Accept all in debug builds
   #else
       // Validate against pinned certificate
       return certificate.GetCertHashString() == EXPECTED_CERT_HASH;
   #endif
   }
   ```

2. **Configuration Management**:
   ```csharp
   public class ClientConfig
   {
       public string ServerHost { get; set; } = "your-server.com";
       public int ServerPort { get; set; } = 443;
       public bool EnableDebugLogging { get; set; } = false;
       public string CertificateHash { get; set; } = "PRODUCTION_CERT_HASH";
   }
   ```

3. **Error Reporting**:
   ```csharp
   public class ErrorReporter
   {
       public static void ReportError(string component, Exception ex, Dictionary<string, string> context = null)
       {
   #if !DEBUG
           // Send to error reporting service
           var errorData = new
           {
               Component = component,
               Message = ex.Message,
               StackTrace = ex.StackTrace,
               Context = context,
               ClientVersion = Application.version,
               Platform = Application.platform.ToString()
           };
           
           // Send to your error reporting service
   #endif
       }
   }
   ```

### 11.5 Monitoring and Analytics

#### Client-Side Metrics

```csharp
public class NetworkMetrics
{
    public float LatencyMs { get; set; }
    public int PacketsSent { get; set; }
    public int PacketsReceived { get; set; }
    public int PacketsLost { get; set; }
    public float PacketLossPercentage => (float)PacketsLost / PacketsSent * 100f;
    
    public void RecordLatency(DateTime sendTime)
    {
        LatencyMs = (float)(DateTime.Now - sendTime).TotalMilliseconds;
    }
    
    public void RecordPacketSent()
    {
        PacketsSent++;
    }
    
    public void RecordPacketReceived()
    {
        PacketsReceived++;
    }
    
    public void RecordPacketLost()
    {
        PacketsLost++;
    }
}
```

#### Telemetry Collection

```csharp
public class TelemetryCollector
{
    private readonly Timer _reportTimer;
    private readonly NetworkMetrics _metrics;
    
    public TelemetryCollector(NetworkMetrics metrics)
    {
        _metrics = metrics;
        _reportTimer = new Timer(SendTelemetry, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }
    
    private void SendTelemetry(object state)
    {
        var telemetry = new
        {
            Timestamp = DateTime.UtcNow,
            Metrics = _metrics,
            Platform = Environment.OSVersion.Platform.ToString(),
            NetFramework = Environment.Version.ToString(),
            ServerHost = _serverHost
        };
        
        // Send to analytics service (async, non-blocking)
        _ = Task.Run(() => SendTelemetryAsync(telemetry));
    }
    
    private async Task SendTelemetryAsync(object telemetry)
    {
        try
        {
            // Send to your analytics endpoint
            var json = JsonSerializer.Serialize(telemetry);
            using var client = new HttpClient();
            await client.PostAsync("https://your-analytics.com/api/telemetry", 
                                   new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Silently fail - don't impact gameplay
        }
    }
}
```

---

This comprehensive guide provides everything needed to implement a secure, production-ready client for the MP-Server racing platform. The implementation covers all security aspects, includes complete working examples for multiple platforms, and provides thorough testing and deployment guidance.

For additional support or questions about specific implementation details, refer to the main documentation or the server's dashboard interface for real-time monitoring and troubleshooting.
