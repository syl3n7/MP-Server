using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    
    // Sequential spawn slot indices assigned as players join (0-based).
    // The game client resolves the actual world position from its own scene.
    private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();
    private readonly ConcurrentDictionary<string, int> _playerSpawnSlots = new();
    
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
            // Assign a sequential spawn slot index (0-based).
            // Use Count - 1 because the player is already in the collection.
            int spawnSlot = _players.Count - 1;
            _playerSpawnSlots.TryAdd(player.Id, spawnSlot);
            
            _logger?.LogInformation("Player {PlayerId} ({PlayerName}) joined room {RoomId} at spawn slot {SpawnSlot}", 
                player.Id, player.PlayerName, Id, spawnSlot);
            
            // Log room activity
            _loggingService?.LogRoomActivityAsync(
                Id, Name, "PlayerJoined", player.Id, player.PlayerName, _players.Count,
                $"Player joined at spawn slot {spawnSlot}");
            
            return true;
        }
        
        return false;
    }
    
    public bool TryRemovePlayer(string playerId)
    {
        var player = _players.TryGetValue(playerId, out var playerInfo) ? playerInfo : null;
        
        _playerSpawnSlots.TryRemove(playerId, out _);
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
    
    /// <summary>
    /// Returns the 0-based spawn slot index for a player.
    /// The game client maps this index to the appropriate spawn point in its own scene.
    /// </summary>
    public int GetPlayerSpawnIndex(string playerId)
    {
        return _playerSpawnSlots.TryGetValue(playerId, out int slot) ? slot : 0;
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