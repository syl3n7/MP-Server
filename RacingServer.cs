using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Numerics;
using MP.Server;

public sealed class RacingServer : IHostedService, IDisposable
{
    private readonly int _tcpPort;
    private readonly int _udpPort;
    private readonly ILogger<RacingServer> _logger;
    internal ILogger<RacingServer> Logger => _logger;
    private readonly CancellationTokenSource _cts = new();
    
    // Networking
    private Socket? _tcpListener;
    private Socket? _udpListener;
    private readonly ConcurrentDictionary<string, PlayerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();
    
    // Threading
    private Task? _tcpAcceptTask;
    private Task? _udpReceiveTask;
    private Task? _heartbeatTask;

    // Server info
    public DateTime StartTime { get; private set; }
    
    public RacingServer(int tcpPort, int udpPort, ILogger<RacingServer>? logger = null)
    {
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _logger = logger ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<RacingServer>();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Set the start time
        StartTime = DateTime.UtcNow;
        
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
        
        _logger.LogInformation("✅ Server started on TCP:{TcpPort} UDP:{UdpPort}", _tcpPort, _udpPort);
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

    public void Dispose() => _cts.Dispose();

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
        using (var session = new PlayerSession(socket, this))
        {
            try
            {
                _sessions.TryAdd(session.Id, session);
                _logger.LogInformation("👤 Player session {SessionId} created from {ClientIP}:{ClientPort}", 
                    session.Id, endpoint?.Address, endpoint?.Port);
                    
                await session.ProcessMessagesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "❌ Error processing TCP connection for session {SessionId}", session.Id);
            }
            finally
            {
                _sessions.TryRemove(session.Id, out _);
                await session.DisconnectAsync().ConfigureAwait(false);
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
        try
        {
            string message = Encoding.UTF8.GetString(data.Span).TrimEnd('\n');
            _logger.LogDebug("🔍 Processing UDP packet from {RemoteEndPoint}: {Message}", remoteEndPoint, message);
            
            // Parse the JSON message
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            
            // Check if this is an UPDATE command
            if (root.TryGetProperty("command", out JsonElement commandElement))
            {
                string? commandType = commandElement.GetString();
                
                if (commandType == "UPDATE" && root.TryGetProperty("sessionId", out JsonElement sessionIdElement))
                {
                    string? sessionId = sessionIdElement.GetString();
                    
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        _logger.LogWarning("⚠️ Received UDP update with empty sessionId");
                        return;
                    }
                    
                    // Update player UDP endpoint if it's our first packet from this player
                    if (_sessions.TryGetValue(sessionId, out PlayerSession? session) && session != null && session.CurrentRoomId != null)
                    {
                        // Create player info with UDP endpoint
                        var playerInfo = new PlayerInfo(
                            session.Id,
                            session.PlayerName,
                            remoteEndPoint as IPEndPoint,
                            ParseVector3(root, "position"),
                            ParseQuaternion(root, "rotation")
                        );
                        
                        // Find the room and update player info
                        if (_rooms.TryGetValue(session.CurrentRoomId, out GameRoom? room) && room != null)
                        {
                            // Update player in room with new position info
                            room.TryRemovePlayer(sessionId);
                            room.TryAddPlayer(playerInfo);
                            
                            // Broadcast to all other players in the same room
                            await BroadcastPositionUpdateAsync(playerInfo, session.CurrentRoomId, sessionId);
                        }
                    }
                }
                else if (commandType == "INPUT" && root.TryGetProperty("sessionId", out JsonElement inputSessionIdElement))
                {
                    // Process INPUT command (will be implemented)
                    string? sessionId = inputSessionIdElement.GetString();
                    if (!string.IsNullOrEmpty(sessionId) && root.TryGetProperty("roomId", out JsonElement roomIdElement))
                    {
                        string? roomId = roomIdElement.GetString();
                        if (!string.IsNullOrEmpty(roomId) && _rooms.TryGetValue(roomId, out GameRoom? room) && room != null)
                        {
                            await ProcessInputCommandAsync(root, sessionId, roomId);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("⚠️ Received malformed UDP packet: {Message}", message);
                }
            }
            else
            {
                _logger.LogWarning("⚠️ Received malformed UDP packet: {Message}", message);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "❌ Error parsing UDP JSON message from {RemoteEndPoint}", remoteEndPoint);
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
                    // Create the input message - forward exactly what we received
                    var inputMsg = JsonSerializer.Serialize(root) + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(inputMsg);
                    
                    // Send the input to the player
                    await _udpListener!.SendToAsync(bytes, player.UdpEndpoint);
                    
                    _logger.LogDebug("📤 Broadcast input from {SenderId} to {ReceiverId}", sessionId, player.Id);
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
        if (_rooms.TryGetValue(roomId, out GameRoom room))
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
                    
                    string json = JsonSerializer.Serialize(updateMsg) + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    
                    // Send the update to the player
                    await _udpListener.SendToAsync(bytes, player.UdpEndpoint);
                    
                    _logger.LogDebug("📤 Broadcast position update from {SenderId} to {ReceiverId}", senderId, player.Id);
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
    
    public GameRoom CreateRoom(string name, string hostId)
    {
        var room = new GameRoom { Name = name, HostId = hostId };
        _rooms.TryAdd(room.Id, room);
        _logger.LogInformation("🏁 New game room created: {RoomName} (ID: {RoomId}) by host {HostId}", 
            name, room.Id, hostId);
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

    public PlayerSession? GetPlayerSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public IReadOnlyCollection<PlayerSession> GetAllSessions()
    {
        return _sessions.Values.ToList().AsReadOnly();
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
}