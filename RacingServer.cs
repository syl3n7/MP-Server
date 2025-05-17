using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    
    public RacingServer(int tcpPort, int udpPort, ILogger<RacingServer>? logger = null)
    {
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _logger = logger ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<RacingServer>();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
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
            // TODO: Parse and route UDP messages
            _logger.LogDebug("🔍 Processing UDP packet from {RemoteEndPoint}", remoteEndPoint);
            await Task.CompletedTask; // Add await to make this truly async
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error processing UDP packet from {RemoteEndPoint}", remoteEndPoint);
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
}