using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Numerics;
using MP.Server;

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
    public bool IsAuthenticated { get; set; } = false;
    
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
            
            // Check if command requires authentication
            if (RequiresAuthentication(command) && !IsAuthenticated)
            {
                await SendJsonAsync(new { command = "ERROR", message = "Authentication required. Please use NAME command with password." }, ct);
                return;
            }
            
            switch (command)
            {
                case "NAME":
                    if (jsonMessage.TryGetProperty("name", out var nameElement))
                    {
                        bool authenticationSuccessful = true;
                        string rawPassword = string.Empty;
                        
                        // Check if password is provided for authentication
                        if (jsonMessage.TryGetProperty("password", out var passwordElement))
                        {
                            rawPassword = passwordElement.GetString() ?? string.Empty;
                            
                            // Check if this player name already exists in the server
                            var existingPlayer = _server.GetPlayerByName(nameElement.GetString() ?? string.Empty);
                            
                            if (existingPlayer != null && existingPlayer.Id != Id)
                            {
                                // Verify the password against stored hash
                                authenticationSuccessful = _server.VerifyPlayerPassword(
                                    nameElement.GetString() ?? string.Empty, 
                                    rawPassword
                                );
                                
                                if (!authenticationSuccessful)
                                {
                                    await SendJsonAsync(new { command = "AUTH_FAILED", message = "Invalid password for this player name." }, ct);
                                    break;
                                }
                            }
                            else
                            {
                                // New player or same player reconnecting - register the password
                                _server.RegisterPlayerPassword(nameElement.GetString() ?? string.Empty, rawPassword);
                            }
                        }
                        
                        PlayerName = nameElement.GetString() ?? "Anonymous";
                        IsAuthenticated = authenticationSuccessful;
                        
                        _server.Logger.LogInformation("👤 Player {SessionId} set name to '{Name}' and {AuthStatus}", 
                            Id, PlayerName, IsAuthenticated ? "authenticated" : "is not authenticated");
                        
                        await SendJsonAsync(new { 
                            command = "NAME_OK", 
                            name = PlayerName, 
                            authenticated = IsAuthenticated 
                        }, ct);
                    }
                    break;
                    
                case "GET_ROOM_PLAYERS":
                    if (string.IsNullOrEmpty(CurrentRoomId))
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot get players. No room joined." }, ct);
                        break;
                    }
                    
                    var currentRoom = _server.GetAllRooms().FirstOrDefault(r => r.Id == CurrentRoomId);
                    if (currentRoom == null)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Cannot get players. Room not found." }, ct);
                        break;
                    }

                    var playerList = currentRoom.Players.Select(p => new { 
                        id = p.Id, 
                        name = p.Name
                    }).ToList();

                    await SendJsonAsync(new { 
                        command = "ROOM_PLAYERS", 
                        roomId = CurrentRoomId,
                        players = playerList
                    }, ct);
                    break;
                    
                case "RELAY_MESSAGE":
                    if (jsonMessage.TryGetProperty("message", out var messageElement) &&
                        jsonMessage.TryGetProperty("targetId", out var targetElement))
                    {
                        var messageContent = messageElement.GetString();
                        var targetId = targetElement.GetString();
                        
                        if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(messageContent))
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "Invalid message relay request. Missing target or message." }, ct);
                            break;
                        }

                        var targetPlayer = _server.GetPlayerSession(targetId);
                        if (targetPlayer == null)
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "Target player not found." }, ct);
                            break;
                        }

                        // Create the relay message
                        var relayData = new { 
                            command = "RELAYED_MESSAGE", 
                            senderId = Id,
                            senderName = PlayerName,
                            message = messageContent
                        };

                        // Send to target player
                        await targetPlayer.SendJsonAsync(relayData, ct);
                        
                        // Acknowledge to the sender
                        await SendJsonAsync(new { command = "RELAY_OK", targetId }, ct);

                        _server.Logger.LogInformation("✉️ Message relayed from {SenderId} to {TargetId}", Id, targetId);
                    }
                    else
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Invalid message relay request." }, ct);
                    }
                    break;

                case "JOIN_ROOM":
                    if (jsonMessage.TryGetProperty("roomId", out var roomIdElement))
                    {
                        var roomId = roomIdElement.GetString() ?? string.Empty;
                        if (_server.GetAllRooms().FirstOrDefault(r => r.Id == roomId) is GameRoom targetRoom)
                        {
                            var newPlayerInfo = new PlayerInfo(Id, PlayerName, null, new Vector3(), new Quaternion());
                            if (targetRoom.TryAddPlayer(newPlayerInfo))
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
                        var newRoomName = roomNameElement.GetString() ?? "Race Room";
                        var newRoom = _server.CreateRoom(newRoomName, Id);
                        CurrentRoomId = newRoom.Id;
                        
                        // Add the player to the room they created
                        var playerInfo = new PlayerInfo(Id, PlayerName, null, new Vector3(), new Quaternion());
                        newRoom.TryAddPlayer(playerInfo);
                        
                        _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) created room '{RoomName}' ({RoomId})", 
                            Id, PlayerName, newRoomName, newRoom.Id);
                        await SendJsonAsync(new { command = "ROOM_CREATED", roomId = newRoom.Id, name = newRoomName }, ct);
                    }
                    break;
                    
                case "PING":
                    await SendJsonAsync(new { command = "PONG" }, ct);
                    break;

                case "LIST_ROOMS":
                    var rooms = _server.GetAllRooms().Select(r => new { 
                        id = r.Id, 
                        name = r.Name, 
                        playerCount = r.PlayerCount, 
                        isActive = r.IsActive,
                        hostId = r.HostId
                    });
                    await SendJsonAsync(new { command = "ROOM_LIST", rooms }, ct);
                    _server.Logger.LogDebug("🏠 Sent room list to player {SessionId}, found {RoomCount} rooms", Id, rooms.Count());
                    break;

                case "PLAYER_INFO":
                    var playerDetails = new { Id, PlayerName, CurrentRoomId };
                    await SendJsonAsync(new { command = "PLAYER_INFO", playerInfo = playerDetails }, ct);
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
                    
                    // Collect spawn positions for all players
                    var playerSpawnPositions = new Dictionary<string, object>();
                    foreach (var player in gameRoom.Players)
                    {
                        var spawnPos = gameRoom.GetPlayerSpawnPosition(player.Id);
                        playerSpawnPositions[player.Id] = new { 
                            x = spawnPos.X, 
                            y = spawnPos.Y, 
                            z = spawnPos.Z 
                        };
                    }
                    
                    // Broadcast game start to all players in the room with spawn positions
                    var gameStartedMessage = new { 
                        command = "GAME_STARTED", 
                        roomId = CurrentRoomId, 
                        hostId = Id,
                        spawnPositions = playerSpawnPositions
                    };
                    await _server.BroadcastToRoomAsync(CurrentRoomId, gameStartedMessage, ct);
                    
                    // Still send a direct response to the host
                    await SendJsonAsync(new { 
                        command = "GAME_STARTED", 
                        roomId = CurrentRoomId,
                        spawnPositions = playerSpawnPositions
                    }, ct);
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
                    string previousRoomId = CurrentRoomId;
                    
                    // Remove player from room
                    room.TryRemovePlayer(Id);
                    CurrentRoomId = null;
                    
                    // If the player was the host and the room is now empty, remove the room
                    if (wasHost && room.PlayerCount == 0 && !room.IsActive)
                    {
                        _server.RemoveRoom(previousRoomId);
                        _server.Logger.LogInformation("🏁 Room '{RoomName}' ({RoomId}) was removed as the host left and it was empty", 
                            roomName, previousRoomId);
                    }
                    // If the player was the host but there are still players, transfer host status
                    else if (wasHost && room.PlayerCount > 0)
                    {
                        // Transfer host status to the first remaining player
                        var newHost = room.Players.FirstOrDefault();
                        if (newHost != null)
                        {
                            room.HostId = newHost.Id;
                            _server.Logger.LogInformation("👑 Host status transferred from {OldHostId} to {NewHostId} in room '{RoomName}' ({RoomId})",
                                Id, newHost.Id, roomName, previousRoomId);
                        }
                    }
                    
                    _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) left room '{RoomName}' ({RoomId})", 
                        Id, PlayerName, roomName, previousRoomId);
                    
                    await SendJsonAsync(new { command = "LEAVE_OK", roomId = previousRoomId }, ct);
                    break;

                case "BYE":
                    _server.Logger.LogInformation("👋 Player {SessionId} ({Name}) is disconnecting", Id, PlayerName);
                    await SendJsonAsync(new { command = "BYE_OK" }, ct);
                    await DisconnectAsync();
                    break;
                    
                case "AUTHENTICATE":
                    if (jsonMessage.TryGetProperty("password", out var authPasswordElement))
                    {
                        var password = authPasswordElement.GetString() ?? string.Empty;
                        
                        if (string.IsNullOrEmpty(PlayerName) || PlayerName == "Anonymous")
                        {
                            await SendJsonAsync(new { 
                                command = "AUTH_FAILED", 
                                message = "Please set your name first with the NAME command."
                            }, ct);
                            break;
                        }
                        
                        bool authResult = _server.VerifyPlayerPassword(PlayerName, password);
                        IsAuthenticated = authResult;
                        
                        if (authResult)
                        {
                            _server.Logger.LogInformation("🔐 Player {SessionId} ({Name}) authenticated successfully", Id, PlayerName);
                            await SendJsonAsync(new { command = "AUTH_OK", name = PlayerName }, ct);
                        }
                        else
                        {
                            _server.Logger.LogInformation("🔒 Player {SessionId} ({Name}) authentication failed", Id, PlayerName);
                            await SendJsonAsync(new { command = "AUTH_FAILED", message = "Invalid password." }, ct);
                        }
                    }
                    else
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Password is required for authentication." }, ct);
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

    private bool RequiresAuthentication(string command)
    {
        // List of commands that require authentication
        switch (command)
        {
            case "NAME":
            case "AUTHENTICATE": 
            case "PING":
            case "BYE":
            case "PLAYER_INFO":
            case "LIST_ROOMS":
                // These commands are allowed without authentication
                return false;
            default:
                // All other commands require authentication
                return true;
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