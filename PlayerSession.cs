using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    public string PlayerName { get; set; } = "Anonymous";
    
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
            // Send welcome message
            var welcomeMessage = $"CONNECTED|{Id}\n";
            await SendTextAsync(welcomeMessage, ct);
            
            _server.Logger.LogInformation("👋 Welcome message sent to session {SessionId}", Id);
            
            while (!ct.IsCancellationRequested)
            {
                var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                
                while (TryParseMessage(ref buffer, out var message))
                {
                    LastActivity = DateTime.UtcNow;
                    _server.Logger.LogDebug("📨 Received message from {SessionId}: {MessageSize} bytes", 
                        Id, message.Length);
                    await ProcessMessageAsync(message, ct);
                }
                
                _reader.AdvanceTo(buffer.Start, buffer.End);
                
                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _server.Logger.LogError(ex, "❌ Error processing messages for session {SessionId}", Id);
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
            // Convert message to string
            var messageText = Encoding.UTF8.GetString(message.ToArray());
            _server.Logger.LogDebug("🔍 Processing message from {SessionId}: '{Message}'", Id, messageText);
            
            // Parse JSON message
            var jsonMessage = JsonSerializer.Deserialize<JsonElement>(messageText);
            if (!jsonMessage.TryGetProperty("command", out var commandElement))
                return;
                
            var command = commandElement.GetString()?.ToUpper();
            if (string.IsNullOrEmpty(command)) return;
            
            switch (command)
            {
                case "NAME":
                    if (jsonMessage.TryGetProperty("name", out var nameElement))
                    {
                        PlayerName = nameElement.GetString() ?? "Anonymous";
                        _server.Logger.LogInformation("👤 Player {SessionId} set name to '{Name}'", Id, PlayerName);
                        await SendJsonAsync(new { command = "NAME_OK", name = PlayerName }, ct);
                    }
                    break;
                    
                case "JOIN_ROOM":
                    if (jsonMessage.TryGetProperty("roomId", out var roomIdElement))
                    {
                        var roomId = roomIdElement.GetString() ?? string.Empty;
                        _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) attempting to join room {RoomId}", 
                            Id, PlayerName, roomId);
                        // TODO: Implement room joining logic
                        await SendJsonAsync(new { command = "JOIN_OK", roomId }, ct);
                    }
                    break;
                    
                case "CREATE_ROOM":
                    if (jsonMessage.TryGetProperty("name", out var roomNameElement))
                    {
                        var roomName = roomNameElement.GetString() ?? "Race Room";
                        var room = _server.CreateRoom(roomName, Id);
                        CurrentRoomId = room.Id;
                        _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) created room '{RoomName}' ({RoomId})", 
                            Id, PlayerName, roomName, room.Id);
                        await SendJsonAsync(new { command = "ROOM_CREATED", roomId = room.Id, name = roomName }, ct);
                    }
                    break;
                    
                case "PING":
                    await SendJsonAsync(new { command = "PONG" }, ct);
                    break;

                case "LIST_ROOMS":
                    var rooms = _server.GetAllRooms().Select(r => new { r.Id, r.Name, r.PlayerCount, r.IsActive });
                    await SendJsonAsync(new { command = "ROOM_LIST", rooms }, ct);
                    break;

                case "PLAYER_INFO":
                    var playerInfo = new { Id, PlayerName, CurrentRoomId };
                    await SendJsonAsync(new { command = "PLAYER_INFO", playerInfo }, ct);
                    break;

                case "START_GAME":
                    if (CurrentRoomId != null && _server.GetActiveRooms().FirstOrDefault(r => r.Id == CurrentRoomId) is GameRoom room)
                    {
                        room.StartGame();
                        await SendJsonAsync(new { command = "GAME_STARTED", roomId = room.Id }, ct);
                    }
                    else
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot start game. No room joined or room not found." }, ct);
                    }
                    break;
                    
                default:
                    _server.Logger.LogWarning("⚠️ Unknown command from {SessionId}: {Command}", Id, command);
                    await SendJsonAsync(new { command = "UNKNOWN_COMMAND", originalCommand = command }, ct);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _server.Logger.LogError(ex, "❌ JSON parsing error for session {SessionId}", Id);
            await SendJsonAsync(new { command = "ERROR", message = "Invalid JSON format" }, ct);
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "❌ Error processing message for session {SessionId}", Id);
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
            _server.Logger.LogError(ex, "❌ Error sending data to session {SessionId}", Id);
            throw;
        }
    }
    
    public async Task SendTextAsync(string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await SendAsync(bytes, ct);
    }

    public async Task SendJsonAsync<T>(T message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        json += "\n"; // Keep the newline delimiter for message framing
        await SendTextAsync(json, ct);
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
            _server.Logger.LogError(ex, "❌ Error disconnecting session {SessionId}", Id);
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