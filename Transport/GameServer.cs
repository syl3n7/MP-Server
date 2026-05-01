using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Linq;
using System.IO;
using MP.Server;
using MP.Server.Diagnostics;
using MP.Server.Inventory;
using MP.Server.Protocol;
using MP.Server.Security;
using MP.Server.Services;
using MP.Server.Domain;

namespace MP.Server.Transport;

public sealed class GameServer : IHostedService, IDisposable, ITransportServer
{
    private readonly int _tcpPort;
    private readonly int _udpPort;
    private readonly ILogger<GameServer> _logger;
    internal ILogger<GameServer> Logger => _logger;
    private readonly CancellationTokenSource _cts = new();
    
    // Security system
    private readonly SecurityManager _securityManager;
    private readonly SecurityConfig _securityConfig;
    private readonly DatabaseLoggingService? _dbLoggingService;
    
    // Networking
    private Socket? _tcpListener;
    private Socket? _udpListener;
    private readonly ConcurrentDictionary<string, PlayerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();
    
    // Protocol command router
    private readonly CommandRouter _router;
    
    // TLS/SSL Configuration
    private readonly bool _useTls;
    private readonly X509Certificate2? _serverCertificate;
    
    // Public IP shown in logs and baked into the self-signed certificate SANs.
    // Set via SERVER_PUBLIC_IP env var or appsettings ServerSettings:PublicIP.
    private readonly string _publicIp;

    // Hostname baked into the self-signed certificate CN/SAN.
    // Set via SERVER_HOSTNAME env var or appsettings ServerSettings:Hostname.
    private readonly string _hostname;

    // Threading
    private Task? _tcpAcceptTask;
    private Task? _udpReceiveTask;
    private Task? _heartbeatTask;

    // Server info
    public DateTime StartTime { get; private set; }
    public int TcpPort => _tcpPort;
    public int UdpPort => _udpPort;
    public bool UseTls => _useTls;
    public string PublicIp => _publicIp;
    public string Hostname => _hostname;

    // Security system access
    public SecurityManager SecurityManager => _securityManager;
    public DatabaseLoggingService? DatabaseLoggingService => _dbLoggingService;
    
    public GameServer(int tcpPort, int udpPort, ILogger<GameServer>? logger = null, CommandRouter? router = null, bool useTls = true, X509Certificate2? certificate = null, SecurityConfig? securityConfig = null, DatabaseLoggingService? dbLoggingService = null, string? publicIp = null, string? hostname = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _logger = logger ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<GameServer>();
        _useTls = useTls;
        _publicIp = publicIp
            ?? Environment.GetEnvironmentVariable("SERVER_PUBLIC_IP")
            ?? "0.0.0.0";
        _hostname = hostname
            ?? Environment.GetEnvironmentVariable("SERVER_HOSTNAME")
            ?? "mp-server";
        _serverCertificate = certificate ?? GenerateOrLoadCertificate();
        _dbLoggingService = dbLoggingService;
        
        // Initialize security system
        _securityConfig = securityConfig ?? new SecurityConfig();
        _securityManager = new SecurityManager(_securityConfig, _logger, _dbLoggingService);
        _securityManager.KickCallback = clientId =>
        {
            if (_sessions.TryGetValue(clientId, out var sessionToKick))
            {
                _logger.LogWarning("🦵 Auto-kicking session {SessionId} ({Name}) for excessive security violations",
                    sessionToKick.Id, sessionToKick.PlayerName);
                _ = Task.Run(async () =>
                {
                    try { await sessionToKick.DisconnectAsync().ConfigureAwait(false); }
                    catch { /* ignore disconnect errors */ }
                });
            }
        };

        if (_useTls && _serverCertificate == null)
        {
            _logger.LogWarning("⚠️ TLS enabled but no certificate available. Falling back to plain text.");
            _useTls = false;
        }
        
        _logger.LogInformation("🔒 Server initialized with {Security} mode", _useTls ? "TLS/SSL" : "Plain text");
        _logger.LogInformation("🛡️ Security system initialized with comprehensive protection");
        
        // Log server initialization to database
        _ = Task.Run(async () =>
        {
            try
            {
                if (_dbLoggingService != null)
                {
                    await _dbLoggingService.LogServerEventAsync(
                        level: "Info",
                        category: "GameServer",
                        message: $"Server initialized with {(_useTls ? "TLS/SSL" : "Plain text")} mode on ports TCP:{tcpPort}, UDP:{udpPort}"
                    );
                }
            }
            catch { /* Ignore logging errors during startup */ }
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Set the start time
        StartTime = DateTime.UtcNow;
        
        // Print diagnostic information
        _logger.LogInformation("🚀 Starting Game Server...");
        MP.Server.Diagnostics.NetworkDiagnostics.PrintNetworkInfo(_logger);
        MP.Server.Diagnostics.NetworkDiagnostics.PrintCertificateInfo(_serverCertificate, _logger);
        
        // TCP Listener
        _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _tcpListener.Bind(new IPEndPoint(IPAddress.Any, _tcpPort));
        _tcpListener.Listen(1000);
        _tcpAcceptTask = Task.Run(() => AcceptTcpConnectionsAsync(_cts.Token));
        
        // UDP Listener
        _udpListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpListener.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
        _udpReceiveTask = Task.Run(() => ReceiveUdpPacketsAsync(_cts.Token));
        
        // Maintenance tasks
        _heartbeatTask = Task.Run(() => HeartbeatMonitorAsync(_cts.Token));

        // Inventory system
        var itemsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "items.json");
        ItemRegistry.Instance.LoadFromJson(itemsPath, _logger);
        InventoryManager.Instance.Initialise(new GameServerAdapter(this), _logger);

        _logger.LogInformation("✅ Server started on TCP:{TcpPort} UDP:{UdpPort}", _tcpPort, _udpPort);
        _logger.LogInformation("🔗 Server binding: 0.0.0.0:{Port} ({Security})", _tcpPort, _useTls ? "TLS/SSL" : "Plain");
        _logger.LogInformation("📡 Clients should connect to: {PublicIP}:{Port}", _publicIp, _tcpPort);
        
        // Log server start to database
        _ = Task.Run(async () =>
        {
            try
            {
                if (_dbLoggingService != null)
                {
                    await _dbLoggingService.LogServerEventAsync(
                        level: "Info",
                        category: "GameServer",
                        message: $"Server started successfully on TCP:{_tcpPort} UDP:{_udpPort}"
                    );
                }
            }
            catch { /* Ignore logging errors */ }
        });
        
        await Task.CompletedTask; // Add await to make this truly async
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();
        
        _tcpListener?.Dispose();
        _udpListener?.Dispose();
        
        await Task.WhenAll(
            _tcpAcceptTask ?? Task.CompletedTask,
            _udpReceiveTask ?? Task.CompletedTask,
            _heartbeatTask ?? Task.CompletedTask
        ).ConfigureAwait(false);
        
        _logger.LogInformation("🛑 Server stopped");
    }

    public void Dispose() 
    {
        _securityManager?.Dispose();
        _cts.Dispose();
    }

    private async Task AcceptTcpConnectionsAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔄 TCP connection listener started");
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = await _tcpListener!.AcceptAsync(ct).ConfigureAwait(false);
                var endpoint = socket.RemoteEndPoint as IPEndPoint;
                
                _logger.LogInformation("🔌 New connection from {ClientIP}:{ClientPort}", 
                    endpoint?.Address, endpoint?.Port);
                
                // Log connection to database
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_dbLoggingService != null)
                        {
                            await _dbLoggingService.LogConnectionEventAsync(
                                eventType: "Connect",
                                sessionId: "pending" // Will be updated when session is created
                                , ipAddress: endpoint?.Address?.ToString() ?? "unknown",
                                connectionType: "TCP",
                                usedTls: _useTls
                            );
                        }
                    }
                    catch { /* Ignore logging errors */ }
                });
                
                // Create a separate task but properly await it with ConfigureAwait(false)
                // This allows the server to continue accepting new connections while handling this one
                _ = HandleTcpConnectionAsync(socket, ct)
                    .ConfigureAwait(false); // Prevent context capturing
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error accepting TCP connection");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        
        _logger.LogInformation("🔄 TCP connection listener stopped");
    }

    private async Task HandleTcpConnectionAsync(Socket socket, CancellationToken ct)
    {
        var endpoint = socket.RemoteEndPoint as IPEndPoint;
        
        using (socket)
        using (var session = new PlayerSession(socket, this, _router, _useTls, _serverCertificate, _securityConfig.UdpSharedSecret))
        {
            try
            {
                _sessions.TryAdd(session.Id, session);
                _logger.LogInformation("👤 Player session {SessionId} created from {ClientIP}:{ClientPort} ({Security})", 
                    session.Id, endpoint?.Address, endpoint?.Port, _useTls ? "TLS" : "Plain");
                
                // Log session creation to database
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_dbLoggingService != null)
                        {
                            await _dbLoggingService.LogConnectionEventAsync(
                                eventType: "Connect",
                                sessionId: session.Id,
                                ipAddress: endpoint?.Address?.ToString() ?? "unknown",
                                connectionType: "TCP",
                                usedTls: _useTls
                            );
                        }
                    }
                    catch { /* Ignore logging errors */ }
                });
                    
                await session.ProcessMessagesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "❌ Error processing TCP connection for session {SessionId}", session.Id);
                
                // Log error to database
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_dbLoggingService != null)
                        {
                            await _dbLoggingService.LogServerEventAsync(
                                level: "Error",
                                category: "PlayerSession",
                                message: $"Error processing TCP connection: {ex.Message}",
                                sessionId: session.Id,
                                ipAddress: endpoint?.Address?.ToString(),
                                exception: ex
                            );
                        }
                    }
                    catch { /* Ignore logging errors */ }
                });
            }
            finally
            {
                InventoryManager.Instance.OnPlayerLeft(session.Id);
                _sessions.TryRemove(session.Id, out _);
                _securityManager.RemoveClient(session.Id);
                await session.DisconnectAsync().ConfigureAwait(false);
                
                // Log disconnection to database
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_dbLoggingService != null)
                        {
                            await _dbLoggingService.LogConnectionEventAsync(
                                eventType: "Disconnect",
                                sessionId: session.Id,
                                ipAddress: endpoint?.Address?.ToString() ?? "unknown",
                                playerName: session.PlayerName,
                                connectionType: "TCP",
                                usedTls: _useTls
                            );
                        }
                    }
                    catch { /* Ignore logging errors */ }
                });
                
                _logger.LogInformation("👋 Player session {SessionId} disconnected", session.Id);
            }
        }
    }

    private async Task ReceiveUdpPacketsAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔄 UDP packet listener started");
        
        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpListener!.ReceiveFromAsync(
                        new ArraySegment<byte>(buffer), endpoint, ct).ConfigureAwait(false);
                    var data = buffer.AsMemory(0, result.ReceivedBytes);
                    
                    _logger.LogDebug("📦 Received UDP packet of {Size} bytes from {ClientIP}:{ClientPort}",
                        result.ReceivedBytes, 
                        (result.RemoteEndPoint as IPEndPoint)?.Address,
                        (result.RemoteEndPoint as IPEndPoint)?.Port);
                        
                    await ProcessUdpPacketAsync(result.RemoteEndPoint, data, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error receiving UDP packet");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _logger.LogInformation("🔄 UDP packet listener stopped");
        }
    }

    private async Task ProcessUdpPacketAsync(EndPoint remoteEndPoint, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        PlayerSession? senderSession = null;
        string? jsonToProcess = null;
        
        try
        {
            // ALL UDP packets MUST be encrypted - no plaintext allowed
            if (data.Length < 4)
            {
                _logger.LogWarning("🚫 Rejected UDP packet from {RemoteEndPoint}: too short (length: {Length})", remoteEndPoint, data.Length);
                return;
            }
            
            var packetData = data.ToArray();
            var expectedLength = BitConverter.ToInt32(packetData, 0);
            
            _logger.LogDebug("🔍 UDP packet from {RemoteEndPoint}: totalLength={TotalLength}, headerLength={HeaderLength}, firstBytes={FirstBytes}", 
                remoteEndPoint, data.Length, expectedLength, Convert.ToHexString(packetData.Take(8).ToArray()));
            
            // Validate packet structure for encrypted format
            if (expectedLength <= 0 || expectedLength > 1024 || expectedLength != packetData.Length - 4)
            {
                _logger.LogWarning("� Rejected UDP packet from {RemoteEndPoint}: invalid length header (expected: {ExpectedLength}, actual: {ActualLength})", 
                    remoteEndPoint, expectedLength, packetData.Length - 4);
                return;
            }
            
            // Try to decrypt with each authenticated session's crypto
            bool decryptionSuccessful = false;
            foreach (var session in _sessions.Values)
            {
                if (session?.IsAuthenticated == true && session.UdpCrypto != null)
                {
                    try
                    {
                        // Extract encrypted data (skip 4-byte length header)
                        var encryptedData = new byte[expectedLength];
                        Array.Copy(packetData, 4, encryptedData, 0, expectedLength);
                        
                        // Try to decrypt with this session's key
                        var decryptedJson = session.UdpCrypto.Decrypt(encryptedData);
                        if (!string.IsNullOrEmpty(decryptedJson))
                        {
                            senderSession = session;
                            jsonToProcess = decryptedJson;
                            decryptionSuccessful = true;
                            _logger.LogDebug("🔓 Successfully decrypted UDP packet from {RemoteEndPoint} for session {SessionId}: {DecryptedJson}", 
                                remoteEndPoint, session.Id, decryptedJson);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Not encrypted with this session's key, continue trying others
                        _logger.LogDebug("❌ Failed to decrypt UDP packet with session {SessionId}: {Error}", 
                            session.Id, ex.Message);
                    }
                }
            }
            
            // If we couldn't decrypt the packet, reject it completely
            if (!decryptionSuccessful || string.IsNullOrEmpty(jsonToProcess))
            {
                _logger.LogWarning("🚫 Rejected UDP packet from {RemoteEndPoint}: could not decrypt with any session key. All UDP packets must be encrypted.", remoteEndPoint);
                return;
            }

            // Parse and process JSON within using block
            using JsonDocument document = JsonDocument.Parse(jsonToProcess);
            var root = document.RootElement;
            
            // 🛡️ SECURITY VALIDATION - Validate packet AFTER decryption
            string clientId = "unknown";
            if (root.TryGetProperty("sessionId", out JsonElement sessionIdElement))
            {
                clientId = sessionIdElement.GetString() ?? "unknown";
            }
            
            // For security validation, use the decrypted JSON (not raw encrypted data)
            // This ensures rate limiting and security checks work on the actual commands
            var validationResult = _securityManager.ValidateUdpPacket(clientId, Encoding.UTF8.GetBytes(jsonToProcess), DateTime.UtcNow);
            if (!validationResult.IsValid)
            {
                _logger.LogWarning("🚫 Security validation failed for UDP packet from {RemoteEndPoint}: {Reason}", 
                    remoteEndPoint, validationResult.Reason);
                
                // If this was a successfully decrypted packet, we know which session sent it
                if (senderSession != null)
                {
                    _logger.LogWarning("🚫 Kicking session {SessionId} for security violation: {Reason}", 
                        senderSession.Id, validationResult.Reason);
                    
                    // Log security event
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_dbLoggingService != null)
                            {
                                await _dbLoggingService.LogSecurityEventAsync(
                                    "UDP_SECURITY_VIOLATION",
                                    remoteEndPoint.ToString() ?? "unknown",
                                    3, // High severity
                                    $"Session {senderSession.Id} kicked for UDP security violation: {validationResult.Reason}",
                                    senderSession.Id
                                );
                            }
                        }
                        catch { /* Ignore logging errors */ }
                    });
                }
                return; // Reject the packet
            }
            
            // ── Routing: envelope (action) or legacy (command) ────────────────
            // Determine the action/command type and whether this is an envelope packet.
            string? udpAction = null;
            bool isEnvelope = false;
            JsonElement payloadEl = default;

            if (root.TryGetProperty("action", out JsonElement actionElement))
            {
                udpAction = actionElement.GetString()?.ToLower();
                isEnvelope = true;
                root.TryGetProperty("payload", out payloadEl);
            }
            else if (root.TryGetProperty("command", out JsonElement commandElement))
            {
                // Legacy: map known UDP commands to canonical action names
                udpAction = commandElement.GetString()?.ToUpper() switch
                {
                    "UPDATE" => "move",
                    "INPUT"  => "input",
                    var other => other?.ToLower()
                };
            }

            // Session ID is always at the root for both formats
            root.TryGetProperty("sessionId", out JsonElement sessionIdRootEl);
            string? rootSessionId = sessionIdRootEl.ValueKind != JsonValueKind.Undefined
                ? sessionIdRootEl.GetString() : null;

            // 🛡️ SECURITY: The payload's sessionId must match the session whose key
            // successfully decrypted this packet. A mismatch means a client is claiming
            // to be a different player — reject immediately.
            if (rootSessionId != null && senderSession != null && rootSessionId != senderSession.Id)
            {
                _logger.LogWarning(
                    "🚫 UDP sessionId spoofing attempt: packet decrypted by session {ActualId} but claims to be session {ClaimedId} — rejected",
                    senderSession.Id, rootSessionId);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_dbLoggingService != null)
                        {
                            await _dbLoggingService.LogSecurityEventAsync(
                                "UDP_SESSION_SPOOF",
                                remoteEndPoint.ToString() ?? "unknown",
                                3,
                                $"Session {senderSession.Id} sent a packet claiming sessionId={rootSessionId}",
                                senderSession.Id
                            );
                        }
                    }
                    catch { /* ignore logging errors */ }
                });
                return;
            }

            if (udpAction == "move" && rootSessionId != null)
            {
                string? sessionId = rootSessionId;

                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger.LogWarning("⚠️ Received UDP move/update with empty sessionId");
                    return;
                }

                // Update player UDP endpoint if it's our first packet from this player
                if (_sessions.TryGetValue(sessionId, out PlayerSession? session) && session != null && session.CurrentRoomId != null)
                {
                    // Envelope: position lives under payload; legacy: position lives at root
                    var posSource = isEnvelope && payloadEl.ValueKind != JsonValueKind.Undefined ? payloadEl : root;

                    var playerInfo = new PlayerInfo(
                        session.Id,
                        session.PlayerName,
                        remoteEndPoint as IPEndPoint,
                        ParseVector3(posSource, "position"),
                        ParseQuaternion(posSource, "rotation")
                    );

                    if (_rooms.TryGetValue(session.CurrentRoomId, out GameRoom? room) && room != null)
                    {
                        if (!room.ContainsPlayer(sessionId))
                        {
                            room.TryAddPlayer(playerInfo);
                            _logger.LogDebug("🔄 Added player {PlayerId} to room {RoomId} via UDP move", sessionId, session.CurrentRoomId);
                        }
                        else
                        {
                            room.UpdatePlayerPosition(playerInfo);
                        }

                        await BroadcastPositionUpdateAsync(playerInfo, session.CurrentRoomId, sessionId);
                    }
                }
            }
            else if (udpAction == "input" && rootSessionId != null)
            {
                string? sessionId = rootSessionId;
                // Envelope: roomId may be in root or payload; legacy: roomId at root
                var inputSource = isEnvelope && payloadEl.ValueKind != JsonValueKind.Undefined ? payloadEl : root;
                if (!string.IsNullOrEmpty(sessionId) && root.TryGetProperty("roomId", out JsonElement roomIdElement))
                {
                    string? roomId = roomIdElement.GetString();
                    if (!string.IsNullOrEmpty(roomId) && _rooms.TryGetValue(roomId, out GameRoom? room) && room != null)
                    {
                        await ProcessInputCommandAsync(root, sessionId, roomId);
                    }
                }
            }
            else if (udpAction != null)
            {
                _logger.LogWarning("⚠️ Unhandled UDP action '{Action}' from {RemoteEndPoint}", udpAction, remoteEndPoint);
            }
            else
            {
                _logger.LogWarning("⚠️ Received malformed UDP packet from {RemoteEndPoint}", remoteEndPoint);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "❌ Error parsing UDP JSON message from {RemoteEndPoint}", remoteEndPoint);
            
            // Log raw packet data for debugging (first 100 bytes)
            var rawData = data.ToArray();
            var debugData = rawData.Length > 100 ? rawData[..100] : rawData;
            _logger.LogDebug("🔍 Raw packet data (first 100 bytes): {RawData}", 
                Convert.ToHexString(debugData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error processing UDP packet from {RemoteEndPoint}", remoteEndPoint);
        }
    }

    // Add new method to process INPUT commands
    private async Task ProcessInputCommandAsync(JsonElement root, string sessionId, string roomId)
    {
        if (!root.TryGetProperty("input", out JsonElement inputElement))
            return;

        // Broadcast input to all other players in the room
        if (_rooms.TryGetValue(roomId, out GameRoom? room) && room != null)
        {
            foreach (var player in room.Players)
            {
                // Don't send the input back to the sender
                if (player.Id == sessionId) continue;
                
                // Skip players without a UDP endpoint
                if (player.UdpEndpoint == null) continue;
                
                try
                {
                    // Create input message object (not string)
                    var inputMsg = JsonSerializer.Deserialize<object>(root.GetRawText());
                    
                    // Find the receiving player's session to check if they use UDP encryption
                    if (_sessions.TryGetValue(player.Id, out PlayerSession? receiverSession) && 
                        receiverSession?.UdpCrypto != null && receiverSession.IsAuthenticated && inputMsg != null)
                    {
                        // Send encrypted UDP packet
                        byte[] encryptedPacket = receiverSession.UdpCrypto.CreatePacket(inputMsg);
                        
                        await _udpListener!.SendToAsync(encryptedPacket, player.UdpEndpoint);
                        
                        _logger.LogDebug("📤🔐 Broadcast encrypted input from {SenderId} to {ReceiverId}", 
                            sessionId, player.Id);
                    }
                    else
                    {
                        // Send plain text UDP packet for non-encrypted clients
                        var inputMsgStr = JsonSerializer.Serialize(inputMsg) + "\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(inputMsgStr);
                        
                        await _udpListener!.SendToAsync(bytes, player.UdpEndpoint);
                        
                        _logger.LogDebug("📤 Broadcast plain input from {SenderId} to {ReceiverId}", 
                            sessionId, player.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error broadcasting input update to {PlayerId}", player.Id);
                }
            }
        }
    }

    private Vector3 ParseVector3(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out JsonElement vectorElement))
        {
            float x = vectorElement.TryGetProperty("x", out JsonElement xElement) ? xElement.GetSingle() : 0f;
            float y = vectorElement.TryGetProperty("y", out JsonElement yElement) ? yElement.GetSingle() : 0f;
            float z = vectorElement.TryGetProperty("z", out JsonElement zElement) ? zElement.GetSingle() : 0f;
            
            return new Vector3(x, y, z);
        }
        
        return Vector3.Zero;
    }

    private Quaternion ParseQuaternion(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out JsonElement quatElement))
        {
            float x = quatElement.TryGetProperty("x", out JsonElement xElement) ? xElement.GetSingle() : 0f;
            float y = quatElement.TryGetProperty("y", out JsonElement yElement) ? yElement.GetSingle() : 0f;
            float z = quatElement.TryGetProperty("z", out JsonElement zElement) ? zElement.GetSingle() : 0f;
            float w = quatElement.TryGetProperty("w", out JsonElement wElement) ? wElement.GetSingle() : 1f;
            
            return new Quaternion(x, y, z, w);
        }
        
        return Quaternion.Identity;
    }

    private async Task BroadcastPositionUpdateAsync(PlayerInfo playerInfo, string roomId, string senderId)
    {
        if (_rooms.TryGetValue(roomId, out GameRoom? room) && room != null)
        {
            foreach (var player in room.Players)
            {
                // Don't send the update back to the sender
                if (player.Id == senderId) continue;
                
                // Skip players without a UDP endpoint
                if (player.UdpEndpoint == null) continue;
                
                try
                {
                    // Create the update message
                    var updateMsg = new 
                    {
                        command = "UPDATE",
                        sessionId = playerInfo.Id,
                        position = new { x = playerInfo.Position.X, y = playerInfo.Position.Y, z = playerInfo.Position.Z },
                        rotation = new { x = playerInfo.Rotation.X, y = playerInfo.Rotation.Y, z = playerInfo.Rotation.Z, w = playerInfo.Rotation.W }
                    };
                    
                    // Find the receiving player's session to check if they use UDP encryption
                    if (_sessions.TryGetValue(player.Id, out PlayerSession? receiverSession) && 
                        receiverSession?.UdpCrypto != null && receiverSession.IsAuthenticated)
                    {
                        // Send encrypted UDP packet
                        byte[] encryptedPacket = receiverSession.UdpCrypto.CreatePacket(updateMsg);
                        
                        if (_udpListener != null)
                        {
                            await _udpListener.SendToAsync(encryptedPacket, player.UdpEndpoint);
                        }
                        
                        _logger.LogDebug("📤🔐 Broadcast encrypted position update from {SenderId} to {ReceiverId}", 
                            senderId, player.Id);
                    }
                    else
                    {
                        // Send plain text UDP packet for non-encrypted clients
                        string json = JsonSerializer.Serialize(updateMsg) + "\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        
                        if (_udpListener != null)
                        {
                            await _udpListener.SendToAsync(bytes, player.UdpEndpoint);
                        }
                        
                        _logger.LogDebug("📤 Broadcast plain position update from {SenderId} to {ReceiverId}", 
                            senderId, player.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error broadcasting position update to {PlayerId}", player.Id);
                }
            }
        }
    }

    private async Task HeartbeatMonitorAsync(CancellationToken ct)
    {
        _logger.LogInformation("💓 Heartbeat monitor started");
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30000, ct).ConfigureAwait(false);
                
                var now = DateTime.UtcNow;
                var inactiveSessions = 0;
                
                foreach (var session in _sessions.Values)
                {
                    if (now - session.LastActivity > TimeSpan.FromSeconds(60))
                    {
                        _logger.LogInformation("⏰ Disconnecting inactive session {SessionId}", session.Id);
                        await session.DisconnectAsync().ConfigureAwait(false);
                        inactiveSessions++;
                    }
                }
                
                _logger.LogInformation("💓 Heartbeat: {ActiveSessions} active sessions, {InactiveSessions} removed",
                    _sessions.Count, inactiveSessions);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in heartbeat monitor");
            }
        }
        
        _logger.LogInformation("💓 Heartbeat monitor stopped");
    }
    
    public GameRoom CreateRoom(string name, string hostId, int maxPlayers = 20)
    {
        var room = new GameRoom(_dbLoggingService, _logger) { Name = name, HostId = hostId, MaxPlayers = maxPlayers };
        _rooms.TryAdd(room.Id, room);
        _logger.LogInformation("🏁 New game room created: {RoomName} (ID: {RoomId}) by host {HostId}, maxPlayers={MaxPlayers}", 
            name, room.Id, hostId, maxPlayers);
        return room;
    }
    
    public bool RemoveRoom(string roomId)
    {
        var result = _rooms.TryRemove(roomId, out var room);
        if (result)
        {
            _logger.LogInformation("🏁 Game room removed: {RoomName} (ID: {RoomId})", room?.Name, roomId);
        }
        return result;
    }
    
    public IReadOnlyCollection<GameRoom> GetActiveRooms()
    {
        return _rooms.Values.Where(r => r.IsActive).ToList().AsReadOnly();
    }

    public IReadOnlyCollection<GameRoom> GetAllRooms()
    {
        return _rooms.Values.ToList().AsReadOnly();
    }

    public IPlayerSession? GetPlayerSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public IReadOnlyCollection<IPlayerSession> GetAllSessions()
    {
        return _sessions.Values.Cast<IPlayerSession>().ToList().AsReadOnly();
    }

    public async Task BroadcastToRoomAsync<T>(string roomId, T message, CancellationToken ct = default)
    {
        if (_rooms.TryGetValue(roomId, out GameRoom? room) && room != null)
        {
            foreach (var player in room.Players)
            {
                if (_sessions.TryGetValue(player.Id, out PlayerSession? session) && session != null)
                {
                    try
                    {
                        await session.SendJsonAsync(message, ct);
                        _logger.LogDebug("📤 Broadcast message to player {PlayerId} in room {RoomId}", player.Id, roomId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error broadcasting message to player {PlayerId}", player.Id);
                    }
                }
            }
        }
    }

    // Certificate Management Methods
    private X509Certificate2? GenerateOrLoadCertificate()
    {
        try
        {
            string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.pfx");
            string certPassword = "MPServer2024!";
            
            // Try to load existing certificate
            if (File.Exists(certPath))
            {
                _logger.LogInformation("🔐 Loading existing certificate from {CertPath}", certPath);
                return ServerCertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
            }
            
            // Generate new self-signed certificate
            _logger.LogInformation("🔐 Generating new self-signed certificate...");
            
            // Use the resolved public IP and optional hostname override
            string publicIp = _publicIp;
            string hostname = _hostname;
            
            var cert = GenerateSelfSignedCertificate(hostname, publicIp, certPassword);
            
            // Save certificate to disk
            File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, certPassword));
            _logger.LogInformation("🔐 Certificate saved to {CertPath}", certPath);
            
            return cert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to generate or load certificate");
            return null;
        }
    }
    
    private X509Certificate2 GenerateSelfSignedCertificate(string subjectName, string publicIp, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        
        // Add extensions for server authentication
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server Authentication
        
        // Add multiple Subject Alternative Names to increase compatibility
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(subjectName);                 // Primary hostname
        sanBuilder.AddDnsName("localhost");                 // Local development
        sanBuilder.AddDnsName("*." + subjectName);          // Wildcard for subdomains
        sanBuilder.AddDnsName(Environment.MachineName);     // Machine name
        
        // Add the public IP
        sanBuilder.AddDnsName(publicIp);                    // Public IP as DNS name (helps with some clients)
        sanBuilder.AddIpAddress(IPAddress.Parse(publicIp)); // Public IP as IP address
        
        try {
            // Try to add the current machine's hostname and IP addresses
            string hostname = Dns.GetHostName();
            sanBuilder.AddDnsName(hostname);
            
            var hostAddresses = Dns.GetHostAddresses(hostname);
            foreach (var ip in hostAddresses)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork || 
                    ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    sanBuilder.AddIpAddress(ip);
                }
            }
        }
        catch (Exception ex) {
            _logger.LogWarning("Could not add all host IPs to certificate: {Message}", ex.Message);
        }
        
        // Always add these IPs
        sanBuilder.AddIpAddress(IPAddress.Loopback);        // 127.0.0.1
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);    // ::1
        sanBuilder.AddIpAddress(IPAddress.Any);             // 0.0.0.0
        sanBuilder.AddIpAddress(IPAddress.IPv6Any);         // ::
        
        // Try to add common LAN IPs (192.168.x.x)
        try {
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var netInterface in networkInterfaces)
            {
                if (netInterface.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    var properties = netInterface.GetIPProperties();
                    foreach (var ipInfo in properties.UnicastAddresses)
                    {
                        if (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            sanBuilder.AddIpAddress(ipInfo.Address);
                            _logger.LogDebug("🔐 Added interface IP to certificate: {IP}", ipInfo.Address);
                        }
                    }
                }
            }
        }
        catch (Exception ex) {
            _logger.LogWarning("Could not add network interface IPs: {Message}", ex.Message);
        }
        
        request.CertificateExtensions.Add(sanBuilder.Build());
        
        // Create certificate valid for 5 years (long validity for development)
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        
        // Export and re-import with exportable private key
        var pfxBytes = certificate.Export(X509ContentType.Pfx, password);
        
        // Use our own certificate loader to create the certificate with proper flags
        var secureCertificate = ServerCertificateLoader.LoadPkcs12(pfxBytes, password);
        
        _logger.LogInformation("🔐 Generated certificate with subject: {Subject}, public IP: {IP}", subjectName, publicIp);
        return secureCertificate;
    }

    /// <summary>
    /// Broadcasts a chat message to all players in a specific room
    /// </summary>
    public async Task BroadcastChatMessageAsync(string roomId, string senderName, string message, string senderId)
    {
        try
        {
            if (!_rooms.TryGetValue(roomId, out var room) || room == null)
            {
                _logger.LogWarning("⚠️ Attempted to broadcast chat message to non-existent room {RoomId}", roomId);
                return;
            }

            var chatMessage = new
            {
                command = "CHAT",
                roomId = roomId,
                senderName = senderName,
                message = message,
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var tasks = new List<Task>();
            
            foreach (var session in _sessions.Values)
            {
                if (session?.CurrentRoomId == roomId && session.Id != senderId)
                {
                    tasks.Add(session.SendJsonAsync(chatMessage, CancellationToken.None));
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                _logger.LogDebug("💬 Broadcasted chat message from {SenderName} to {PlayerCount} players in room {RoomId}", 
                    senderName, tasks.Count, roomId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error broadcasting chat message in room {RoomId}", roomId);
        }
    }
}