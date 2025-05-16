using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Numerics;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole();
var host = builder.Build();

var server = new RacingServer(7777, 7778);
await server.StartAsync();

await host.RunAsync();

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
        
        _logger.LogInformation("Server started on TCP:{TcpPort} UDP:{UdpPort}", _tcpPort, _udpPort);
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
        
        _logger.LogInformation("Server stopped");
    }

    public void Dispose() => _cts.Dispose();

    private async Task AcceptTcpConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = await _tcpListener!.AcceptAsync(ct).ConfigureAwait(false);
                
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
                _logger.LogError(ex, "Error accepting TCP connection");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleTcpConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        using (var session = new PlayerSession(socket, this))
        {
            try
            {
                _sessions.TryAdd(session.Id, session);
                await session.ProcessMessagesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error processing TCP connection");
            }
            finally
            {
                _sessions.TryRemove(session.Id, out _);
                await session.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveUdpPacketsAsync(CancellationToken ct)
    {
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
                    await ProcessUdpPacketAsync(result.RemoteEndPoint, data, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error receiving UDP packet");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ProcessUdpPacketAsync(EndPoint remoteEndPoint, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            // TODO: Parse and route UDP messages
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing UDP packet");
        }
    }

    private async Task HeartbeatMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30000, ct).ConfigureAwait(false);
                
                var now = DateTime.UtcNow;
                foreach (var session in _sessions.Values)
                {
                    if (now - session.LastActivity > TimeSpan.FromSeconds(60))
                    {
                        _logger.LogInformation("Disconnecting inactive session {SessionId}", session.Id);
                        await session.DisconnectAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat monitor");
            }
        }
    }
}

public sealed class PlayerSession : IDisposable
{
    private readonly Socket _socket;
    private readonly RacingServer _server;
    private readonly NetworkStream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public string? CurrentRoomId { get; set; }
    
    public PlayerSession(Socket socket, RacingServer server)
    {
        _socket = socket;
        _server = server;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);
    }

    public async Task ProcessMessagesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                
                while (TryParseMessage(ref buffer, out var message))
                {
                    LastActivity = DateTime.UtcNow;
                    _ = ProcessMessageAsync(message, ct);
                }
                
                _reader.AdvanceTo(buffer.Start, buffer.End);
                
                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _server.Logger.LogError(ex, "Error processing messages for session {SessionId}", Id);
        }
    }

    private bool TryParseMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> message)
    {
        // Simple newline-delimited protocol
        var position = buffer.PositionOf((byte)'\n');
        if (position == null)
        {
            message = default;
            return false;
        }
        
        message = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private async Task ProcessMessageAsync(ReadOnlySequence<byte> message, CancellationToken ct)
    {
        try
        {
            // TODO: Implement message processing
            // Use System.Text.Json for parsing JSON messages
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "Error processing message for session {SessionId}", Id);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try
        {
            await _writer.WriteAsync(data, ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "Error sending data to session {SessionId}", Id);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
                await _socket.DisconnectAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "Error disconnecting session {SessionId}", Id);
        }
    }

    public void Dispose()
    {
        _reader.Complete();
        _writer.Complete();
        _stream.Dispose();
        _socket.Dispose();
    }
}

public sealed class GameRoom
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Race Room";
    public string? HostId { get; set; }
    public int MaxPlayers { get; set; } = 20;
    public bool IsActive { get; set; }
    
    private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();
    
    public IReadOnlyCollection<PlayerInfo> Players => _players.Values.ToList().AsReadOnly();
    
    public bool TryAddPlayer(PlayerInfo player)
    {
        if (_players.Count >= MaxPlayers)
            return false;
            
        return _players.TryAdd(player.Id, player);
    }
    
    public bool TryRemovePlayer(string playerId)
    {
        return _players.TryRemove(playerId, out _);
    }
}

public record PlayerInfo(
    string Id,
    string Name,
    IPEndPoint? UdpEndpoint,
    Vector3 Position,
    Quaternion Rotation
);