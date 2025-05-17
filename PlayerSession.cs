using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Numerics;

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
                        if (_server.GetAllRooms().FirstOrDefault(r => r.Id == roomId) is GameRoom room)
                        {
                            var newPlayerInfo = new PlayerInfo(Id, PlayerName, null, new Vector3(), new Quaternion());
                            if (room.TryJoinRoom(Id, newPlayerInfo))
                            {
                                CurrentRoomId = roomId;
                                _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) joined room {RoomId}", Id, PlayerName, roomId);
                                await SendJsonAsync(new { command = "JOIN_OK", roomId }, ct);
                            }
                            else
                            {
                                await SendJsonAsync(new { command = "ERROR", message = "Failed to join room. Room may be full or inactive." }, ct);
                            }
                        }
                        else
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "Room not found." }, ct);
                        }
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
                    if (string.IsNullOrEmpty(CurrentRoomId))
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot start game. No room joined." }, ct);
                        break;
                    }
                    
                    var gameRoom = _server.GetAllRooms().FirstOrDefault(r => r.Id == CurrentRoomId);
                    if (gameRoom == null)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot start game. Room not found." }, ct);
                        break;
                    }
                    
                    if (!gameRoom.ContainsPlayer(Id))
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot start game. You are not in this room." }, ct);
                        CurrentRoomId = null; // Reset the room ID since it's invalid
                        break;
                    }
                    
                    if (gameRoom.HostId != Id)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot start game. Only the host can start the game." }, ct);
                        break;
                    }
                    
                    gameRoom.StartGame();
                    _server.Logger.LogInformation("🎮 Game started in room {RoomId} by host {HostId}", CurrentRoomId, Id);
                    await SendJsonAsync(new { command = "GAME_STARTED", roomId = CurrentRoomId }, ct);
                    break;
                    
                case "LEAVE_ROOM":
                    if (string.IsNullOrEmpty(CurrentRoomId))
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot leave room. No room joined." }, ct);
                        break;
                    }
                    
                    var room = _server.GetAllRooms().FirstOrDefault(r => r.Id == CurrentRoomId);
                    if (room == null)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot leave room. Room not found." }, ct);
                        CurrentRoomId = null; // Reset the room ID since it's invalid
                        break;
                    }
                    
                    if (!room.ContainsPlayer(Id))
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot leave room. You are not in this room." }, ct);
                        CurrentRoomId = null; // Reset the room ID since it's invalid
                        break;
                    }
                    
                    // If this player is the host and there are other players, we could transfer host status
                    // But for simplicity, we'll just remove the player and let the room continue if it's active
                    bool wasHost = room.HostId == Id;
                    string roomName = room.Name;
                    
                    // Remove player from room
                    room.TryRemovePlayer(Id);
                    string previousRoomId = CurrentRoomId;
                    CurrentRoomId = null;
                    
                    _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) left room '{RoomName}' ({RoomId})", 
                        Id, PlayerName, roomName, previousRoomId);
                    
                    await SendJsonAsync(new { command = "LEAVE_OK", roomId = previousRoomId }, ct);
                    break;

                case "BYE":
                    _server.Logger.LogInformation("👋 Player {SessionId} ({Name}) is disconnecting", Id, PlayerName);
                    await SendJsonAsync(new { command = "BYE_OK" }, ct);
                    await DisconnectAsync();
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