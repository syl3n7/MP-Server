using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Numerics;
using MP.Server;
using MP.Server.Inventory;
using MP.Server.Security;
using MP.Server.Services;
using MP.Server.Domain;

namespace MP.Server.Transport;

public sealed class PlayerSession : IDisposable, IPlayerSession
{
    private readonly Socket _socket;
    private readonly GameServer _server;
    private readonly AuthService? _authService;
    private readonly string _udpSharedSecret;
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly bool _useTls;
    
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public string? CurrentRoomId { get; set; }
    public string PlayerName { get; set; } = "Anonymous";
    public bool IsAuthenticated { get; set; } = false;
    public int? AuthenticatedUserId { get; private set; }
    public string? AuthenticatedUsername { get; private set; }
    
    // UDP Encryption
    public UdpEncryption? UdpCrypto { get; private set; }

    // Idempotency: maps messageId → receipt timestamp (ms). Prevents duplicate processing.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _recentMessageIds = new();
    private const int MessageIdWindowMs = 30_000;
    
    public PlayerSession(Socket socket, GameServer server, AuthService? authService = null, bool useTls = false, X509Certificate2? certificate = null, string udpSharedSecret = "change-me-in-appsettings")
    {
        _socket = socket;
        _server = server;
        _authService = authService;
        _udpSharedSecret = udpSharedSecret;
        _useTls = useTls;
        
        // Create network stream
        var networkStream = new NetworkStream(socket, ownsSocket: false);
        
        if (_useTls && certificate != null)
        {
            // Create SSL stream
            var sslStream = new SslStream(networkStream, false);
            
            try
            {
                // Create SSL server authentication options
                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                };
                
                // Authenticate as server with timeout
                sslStream.AuthenticateAsServer(certificate, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                _stream = sslStream;
                _server.Logger.LogInformation("🔒 TLS handshake completed for session {SessionId}", Id);
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                _server.Logger.LogWarning("⚠️ TLS handshake failed for session {SessionId} - Client may be using outdated protocol: {Error}", 
                                         Id, ex.Message);
                
                // Log the client endpoint for debugging
                var clientEndpoint = socket.RemoteEndPoint?.ToString() ?? "unknown";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_server.DatabaseLoggingService != null)
                        {
                            await _server.DatabaseLoggingService.LogSecurityEventAsync(
                                "TLS_HANDSHAKE_FAILED", 
                                clientEndpoint,
                                2, // Medium severity
                                $"TLS handshake failed - client may be using outdated protocol: {ex.Message}",
                                Id
                            );
                        }
                    }
                    catch
                    {
                        // Ignore logging errors in constructor
                    }
                });
                
                sslStream.Dispose();
                _stream = networkStream; // Fallback to plain TCP
                _useTls = false; // Update flag to reflect actual state
            }
            catch (Exception ex)
            {
                _server.Logger.LogError(ex, "❌ Unexpected TLS error for session {SessionId}", Id);
                sslStream.Dispose();
                _stream = networkStream; // Fallback to plain TCP
                _useTls = false;
            }
        }
        else
        {
            _stream = networkStream;
        }
        
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);
    }

    public async Task ProcessMessagesAsync(CancellationToken ct)
    {
        try
        {
            // Send welcome message as JSON so clients can parse it the same way as every other message
            await SendJsonAsync(new { command = "CONNECTED", sessionId = Id }, ct);
            
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

            // ─── Envelope fields (present in both formats) ───────────────────
            string? messageId = jsonMessage.TryGetProperty("messageId", out var midEl) ? midEl.GetString() : null;

            // Idempotency: drop duplicates within the 30-second window
            if (messageId != null)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!_recentMessageIds.TryAdd(messageId, nowMs))
                {
                    _server.Logger.LogDebug("⏭️ Duplicate messageId {MessageId} from {SessionId}, skipping", messageId, Id);
                    return;
                }
                // Lazy cleanup of expired entries
                foreach (var kvp in _recentMessageIds)
                    if (nowMs - kvp.Value > MessageIdWindowMs)
                        _recentMessageIds.TryRemove(kvp.Key, out _);
            }

            // ─── Routing: envelope (action) vs legacy (command) ──────────────
            if (jsonMessage.TryGetProperty("action", out var actionElement))
            {
                if (!IsAuthenticated)
                {
                    await SendJsonAsync(new { command = "ERROR", message = "Authentication required.", ackFor = messageId }, ct);
                    return;
                }
                await ProcessEnvelopeActionAsync(jsonMessage, actionElement.GetString(), messageId, ct);
                return;
            }

            if (!jsonMessage.TryGetProperty("command", out var commandElement))
                return;
                
            var command = commandElement.GetString()?.ToUpper();
            if (string.IsNullOrEmpty(command)) return;
            
            // Check if command requires authentication
            if (RequiresAuthentication(command) && !IsAuthenticated)
            {
                await SendJsonAsync(new { command = "ERROR", message = "Authentication required. Use REGISTER, LOGIN, or AUTO_AUTH first." }, ct);
                return;
            }
            
            switch (command)
            {
                case "REGISTER":
                    // Create a new player account. Returns a persistent token.
                    if (_authService == null)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Auth service unavailable." }, ct);
                        break;
                    }
                    {
                        var username  = jsonMessage.TryGetProperty("username",  out var ru) ? ru.GetString() ?? string.Empty : string.Empty;
                        var password  = jsonMessage.TryGetProperty("password",  out var rp) ? rp.GetString() ?? string.Empty : string.Empty;
                        var email     = jsonMessage.TryGetProperty("email",     out var re) ? re.GetString() ?? string.Empty : string.Empty;
                        var ip        = (_socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address?.ToString();

                        var result = await _authService.RegisterAsync(username, password, email, ip);
                        if (result.Success)
                        {
                            AuthenticatedUserId   = result.UserId;
                            AuthenticatedUsername = result.Username;
                            PlayerName            = result.Username ?? "Anonymous";
                            IsAuthenticated       = true;
                            UdpCrypto             = new UdpEncryption(Id, _udpSharedSecret);
                            _server.Logger.LogInformation("✅ REGISTER → {Username} (Id={UserId}) session={SessionId}", result.Username, result.UserId, Id);
                            await SendJsonAsync(new { command = "REGISTER_OK", userId = result.UserId, username = result.Username, token = result.Token }, ct);
                            await InventoryManager.Instance.OnPlayerJoined(Id, ct);
                        }
                        else
                        {
                            await SendJsonAsync(new { command = "REGISTER_FAILED", message = result.Error }, ct);
                        }
                    }
                    break;

                case "LOGIN":
                    // Log in with username + password. Returns a persistent token.
                    if (_authService == null)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Auth service unavailable." }, ct);
                        break;
                    }
                    {
                        var username = jsonMessage.TryGetProperty("username", out var lu) ? lu.GetString() ?? string.Empty : string.Empty;
                        var password = jsonMessage.TryGetProperty("password", out var lp) ? lp.GetString() ?? string.Empty : string.Empty;
                        var ip       = (_socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address?.ToString();

                        var result = await _authService.LoginAsync(username, password, ip);
                        if (result.Success)
                        {
                            AuthenticatedUserId   = result.UserId;
                            AuthenticatedUsername = result.Username;
                            PlayerName            = result.Username ?? "Anonymous";
                            IsAuthenticated       = true;
                            UdpCrypto             = new UdpEncryption(Id, _udpSharedSecret);
                            _server.Logger.LogInformation("🔐 LOGIN → {Username} (Id={UserId}) session={SessionId}", result.Username, result.UserId, Id);
                            await SendJsonAsync(new { command = "LOGIN_OK", userId = result.UserId, username = result.Username, token = result.Token }, ct);
                            await InventoryManager.Instance.OnPlayerJoined(Id, ct);
                        }
                        else
                        {
                            await SendJsonAsync(new { command = "LOGIN_FAILED", message = result.Error }, ct);
                        }
                    }
                    break;

                case "AUTO_AUTH":
                    // Silent re-login using a token stored by the client after a previous LOGIN/REGISTER.
                    if (_authService == null)
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Auth service unavailable." }, ct);
                        break;
                    }
                    {
                        var token = jsonMessage.TryGetProperty("token", out var at) ? at.GetString() ?? string.Empty : string.Empty;
                        var ip    = (_socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address?.ToString();

                        var result = await _authService.AutoAuthAsync(token, ip);
                        if (result.Success)
                        {
                            AuthenticatedUserId   = result.UserId;
                            AuthenticatedUsername = result.Username;
                            PlayerName            = result.Username ?? "Anonymous";
                            IsAuthenticated       = true;
                            UdpCrypto             = new UdpEncryption(Id, _udpSharedSecret);
                            _server.Logger.LogInformation("🔑 AUTO_AUTH → {Username} (Id={UserId}) session={SessionId}", result.Username, result.UserId, Id);
                            await SendJsonAsync(new { command = "AUTO_AUTH_OK", userId = result.UserId, username = result.Username }, ct);
                            await InventoryManager.Instance.OnPlayerJoined(Id, ct);
                        }
                        else
                        {
                            await SendJsonAsync(new { command = "AUTO_AUTH_FAILED", message = result.Error }, ct);
                        }
                    }
                    break;

                case "NAME":
                    // Override display name only — authentication must already be established.
                    if (jsonMessage.TryGetProperty("name", out var nameElement))
                    {
                        var displayName = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            PlayerName = displayName;
                            _server.Logger.LogInformation("👤 Player {SessionId} set display name to '{Name}'", Id, PlayerName);
                            await SendJsonAsync(new { command = "NAME_OK", name = PlayerName }, ct);
                        }
                        else
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "Display name cannot be empty." }, ct);
                        }
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
                        var newRoomName = roomNameElement.GetString() ?? "Room";
                        int maxPlayers = jsonMessage.TryGetProperty("maxPlayers", out var mpEl) && mpEl.TryGetInt32(out int mp) ? mp : 20;
                        var newRoom = _server.CreateRoom(newRoomName, Id, maxPlayers);
                        CurrentRoomId = newRoom.Id;
                        
                        // Add the player to the room they created
                        var playerInfo = new PlayerInfo(Id, PlayerName, null, new Vector3(), new Quaternion());
                        newRoom.TryAddPlayer(playerInfo);
                        
                        _server.Logger.LogInformation("👤 Player {SessionId} ({Name}) created room '{RoomName}' ({RoomId}) maxPlayers={MaxPlayers}", 
                            Id, PlayerName, newRoomName, newRoom.Id, newRoom.MaxPlayers);
                        await SendJsonAsync(new { command = "ROOM_CREATED", roomId = newRoom.Id, name = newRoomName, maxPlayers = newRoom.MaxPlayers }, ct);
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
                        maxPlayers = r.MaxPlayers,
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
                    
                    // Collect spawn slot indices for all players.
                    // The game client resolves the actual world position from its own scene.
                    var playerSpawnPositions = new Dictionary<string, object>();
                    foreach (var player in gameRoom.Players)
                    {
                        playerSpawnPositions[player.Id] = new { 
                            spawnIndex = gameRoom.GetPlayerSpawnIndex(player.Id)
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

                case "MESSAGE":
                    // Handle chat messages
                    if (jsonMessage.TryGetProperty("message", out var chatMessageElement))
                    {
                        var chatMessage = chatMessageElement.GetString() ?? string.Empty;
                        
                        if (!string.IsNullOrEmpty(chatMessage) && !string.IsNullOrEmpty(CurrentRoomId))
                        {
                            _server.Logger.LogInformation("💬 Player {SessionId} ({Name}) sent message: {Message}", 
                                Id, PlayerName, chatMessage);
                            
                            // Broadcast message to all players in the same room
                            await _server.BroadcastChatMessageAsync(CurrentRoomId, PlayerName, chatMessage, Id);
                            await SendJsonAsync(new { command = "MESSAGE_OK" }, ct);
                        }
                        else
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "Empty message or not in a room." }, ct);
                        }
                    }
                    else
                    {
                        await SendJsonAsync(new { command = "ERROR", message = "Message content is required." }, ct);
                    }
                    break;

                case "INV_MOVE_SLOT":
                    {
                        if (!jsonMessage.TryGetProperty("fromSlot", out var fromSlotEl) || !fromSlotEl.TryGetInt32(out int fromSlot) ||
                            !jsonMessage.TryGetProperty("toSlot",   out var toSlotEl)   || !toSlotEl.TryGetInt32(out int toSlot))
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "fromSlot and toSlot (int) are required." }, ct);
                            break;
                        }
                        await InventoryManager.Instance.HandleMoveSlot(Id, fromSlot, toSlot, ct);
                    }
                    break;

                case "INV_DROP_ITEM":
                    {
                        if (!jsonMessage.TryGetProperty("slotId", out var dropSlotEl) || !dropSlotEl.TryGetInt32(out int dropSlot))
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "slotId (int) is required." }, ct);
                            break;
                        }
                        int dropQty = jsonMessage.TryGetProperty("quantity", out var qtyEl) && qtyEl.TryGetInt32(out int q) ? q : 1;
                        if (string.IsNullOrEmpty(CurrentRoomId))
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "Must be in a room to drop items." }, ct);
                            break;
                        }
                        var dropRoom   = _server.GetAllRooms().FirstOrDefault(r => r.Id == CurrentRoomId);
                        var playerPos  = dropRoom?.Players.FirstOrDefault(p => p.Id == Id)?.Position ?? Vector3.Zero;
                        await InventoryManager.Instance.HandleDropItem(Id, CurrentRoomId, dropSlot, dropQty, playerPos, ct);
                    }
                    break;

                case "INV_USE_ITEM":
                    {
                        if (!jsonMessage.TryGetProperty("slotId", out var useSlotEl) || !useSlotEl.TryGetInt32(out int useSlot))
                        {
                            await SendJsonAsync(new { command = "ERROR", message = "slotId (int) is required." }, ct);
                            break;
                        }
                        await InventoryManager.Instance.HandleUseItem(Id, useSlot, ct);
                    }
                    break;

                case "INV_REQUEST_SYNC":
                    await InventoryManager.Instance.HandleSyncRequest(Id, ct);
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

    //? Envelope-based action handler (Godot-compatible protocol)
    // Called when the incoming message has an "action" field instead of "command".
    private async Task ProcessEnvelopeActionAsync(JsonElement envelope, string? action, string? messageId, CancellationToken ct)
    {
        switch (action?.ToLower())
        {
            case "heartbeat":
                await SendJsonAsync(new
                {
                    command = "HEARTBEAT_ACK",
                    ackFor = messageId,
                    serverTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    sessionId = Id
                }, ct);
                break;

            case "snapshot_sync":
                if (string.IsNullOrEmpty(CurrentRoomId))
                {
                    await SendJsonAsync(new { command = "SNAPSHOT", ackFor = messageId, players = Array.Empty<object>() }, ct);
                    break;
                }
                var room = _server.GetAllRooms().FirstOrDefault(r => r.Id == CurrentRoomId);
                var snapshot = room?.Players.Select(p => new { id = p.Id, name = p.Name }) ?? Enumerable.Empty<object>();
                await SendJsonAsync(new { command = "SNAPSHOT", ackFor = messageId, roomId = CurrentRoomId, players = snapshot }, ct);
                break;

            default:
                _server.Logger.LogWarning("⚠️ Unknown envelope action '{Action}' from {SessionId}", action, Id);
                await SendJsonAsync(new { command = "UNKNOWN_ACTION", action, ackFor = messageId }, ct);
                break;
        }
    }

    private bool RequiresAuthentication(string command)
    {
        return command switch
        {
            "REGISTER" or "LOGIN" or "AUTO_AUTH" or "PING" or "BYE" or "PLAYER_INFO" or "LIST_ROOMS" => false,
            _ => true
        };
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