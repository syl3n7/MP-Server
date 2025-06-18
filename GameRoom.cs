using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MP.Server;
using MP.Server.Services;
using Microsoft.Extensions.Logging;

public sealed class GameRoom
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Race Room";
    public string? HostId { get; set; }
    public int MaxPlayers { get; set; } = 20;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    
    private readonly DatabaseLoggingService? _loggingService;
    private readonly ILogger? _logger;
    
    // Predefined spawn positions on the track
    private readonly Vector3[] _trackGaragePositions = new Vector3[]
    {
        new Vector3(66, -2, 0.8f),   // Position 0
        new Vector3(60, -2, 0.8f),   // Position 1
        new Vector3(54, -2, 0.8f),   // Position 2
        new Vector3(47, -2, 0.8f),   // Position 3
        new Vector3(41, -2, 0.8f),   // Position 4
        new Vector3(35, -2, 0.8f),   // Position 5
        new Vector3(28, -2, 0.8f),   // Position 6
        new Vector3(22, -2, 0.8f),   // Position 7
        new Vector3(16, -2, 0.8f),   // Position 8
        new Vector3(9, -2, 0.8f),    // Position 9
        new Vector3(3, -2, 0.8f),    // Position 10
        new Vector3(-3, -2, 0.8f),   // Position 11
        new Vector3(-9, -2, 0.8f),   // Position 12
        new Vector3(-15, -2, 0.8f),  // Position 13
        new Vector3(-22, -2, 0.8f),  // Position 14
        new Vector3(-28, -2, 0.8f),  // Position 15
        new Vector3(-34, -2, 0.8f),  // Position 16
        new Vector3(-41, -2, 0.8f),  // Position 17
        new Vector3(-47, -2, 0.8f),  // Position 18
        new Vector3(-54, -2, 0.8f)   // Position 19
    };
    
    private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();
    private readonly ConcurrentDictionary<string, int> _playerPositions = new();
    
    public GameRoom(DatabaseLoggingService? loggingService = null, ILogger? logger = null)
    {
        _loggingService = loggingService;
        _logger = logger;
        
        // Log room creation
        _loggingService?.LogRoomActivityAsync(
            Id, Name, "RoomCreated", null, null, 0, 
            $"Room created with max players: {MaxPlayers}");
    }
    
    public IReadOnlyCollection<PlayerInfo> Players => _players.Values.ToList().AsReadOnly();
    
    public bool TryAddPlayer(PlayerInfo player)
    {
        if (_players.Count >= MaxPlayers)
        {
            _logger?.LogWarning("Failed to add player {PlayerId} to room {RoomId}: Room is full ({Count}/{MaxPlayers})", 
                player.Id, Id, _players.Count, MaxPlayers);
            return false;
        }
            
        // First try to add the player to prevent race conditions
        if (_players.TryAdd(player.Id, player))
        {
            // Player was successfully added, now assign spawn position
            // Use _players.Count - 1 because the player is now in the collection
            int positionIndex = _players.Count - 1;
            if (positionIndex >= 0 && positionIndex < _trackGaragePositions.Length)
            {
                _playerPositions.TryAdd(player.Id, positionIndex);
            }
            
            _logger?.LogInformation("Player {PlayerId} ({PlayerName}) joined room {RoomId} at position {Position}", 
                player.Id, player.PlayerName, Id, positionIndex);
            
            // Log room activity
            _loggingService?.LogRoomActivityAsync(
                Id, Name, "PlayerJoined", player.Id, player.PlayerName, _players.Count,
                $"Player joined at spawn position {positionIndex}");
            
            return true;
        }
        
        return false;
    }
    
    public bool TryRemovePlayer(string playerId)
    {
        var player = _players.TryGetValue(playerId, out var playerInfo) ? playerInfo : null;
        
        _playerPositions.TryRemove(playerId, out _);
        var removed = _players.TryRemove(playerId, out _);
        
        if (removed && player != null)
        {
            _logger?.LogInformation("Player {PlayerId} ({PlayerName}) left room {RoomId}", 
                playerId, player.PlayerName, Id);
            
            // Log room activity
            _loggingService?.LogRoomActivityAsync(
                Id, Name, "PlayerLeft", playerId, player.PlayerName, _players.Count,
                "Player left the room");
        }
        
        return removed;
    }
    
    public Vector3 GetPlayerSpawnPosition(string playerId)
    {
        if (_playerPositions.TryGetValue(playerId, out int positionIndex) && 
            positionIndex >= 0 && positionIndex < _trackGaragePositions.Length)
        {
            return _trackGaragePositions[positionIndex];
        }
        
        // Default position if no specific position is assigned
        return _trackGaragePositions[0];
    }
    
    public bool ContainsPlayer(string playerId)
    {
        return _players.ContainsKey(playerId);
    }
    
    public bool UpdatePlayerPosition(PlayerInfo updatedPlayerInfo)
    {
        if (_players.TryGetValue(updatedPlayerInfo.Id, out var existingPlayer))
        {
            // Create new PlayerInfo with updated position/rotation but keeping existing spawn position
            var newPlayerInfo = new PlayerInfo(
                existingPlayer.Id,
                existingPlayer.Name,
                updatedPlayerInfo.UdpEndpoint, // Update UDP endpoint
                updatedPlayerInfo.Position,    // Update position
                updatedPlayerInfo.Rotation     // Update rotation
            );
            
            // Replace the player info in the dictionary
            _players.TryUpdate(updatedPlayerInfo.Id, newPlayerInfo, existingPlayer);
            
            _logger?.LogDebug("Updated position for player {PlayerId} in room {RoomId}", 
                updatedPlayerInfo.Id, Id);
            
            return true;
        }
        
        return false;
    }
    
    public int PlayerCount => _players.Count;

    public void StartGame()
    {
        IsActive = true;
        
        _logger?.LogInformation("Game started in room {RoomId} with {PlayerCount} players", 
            Id, _players.Count);
        
        // Log room activity
        _loggingService?.LogRoomActivityAsync(
            Id, Name, "GameStarted", HostId, null, _players.Count,
            $"Game started with {_players.Count} players");
    }
    
    public void EndGame(string? reason = null)
    {
        IsActive = false;
        
        _logger?.LogInformation("Game ended in room {RoomId}. Reason: {Reason}", 
            Id, reason ?? "Normal completion");
        
        // Log room activity
        _loggingService?.LogRoomActivityAsync(
            Id, Name, "GameEnded", HostId, null, _players.Count,
            $"Game ended. Reason: {reason ?? "Normal completion"}");
    }
    
    public void SetHost(string newHostId)
    {
        var oldHostId = HostId;
        HostId = newHostId;
        
        var newHost = _players.TryGetValue(newHostId, out var hostInfo) ? hostInfo : null;
        
        _logger?.LogInformation("Host changed in room {RoomId} from {OldHostId} to {NewHostId} ({HostName})", 
            Id, oldHostId, newHostId, newHost?.PlayerName);
        
        // Log room activity
        _loggingService?.LogRoomActivityAsync(
            Id, Name, "HostChanged", newHostId, newHost?.PlayerName, _players.Count,
            $"Host changed from {oldHostId} to {newHostId}");
    }
}