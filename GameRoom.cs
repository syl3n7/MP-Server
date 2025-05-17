using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public sealed class GameRoom
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Race Room";
    public string? HostId { get; set; }
    public int MaxPlayers { get; set; } = 20;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    
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
    
    public IReadOnlyCollection<PlayerInfo> Players => _players.Values.ToList().AsReadOnly();
    
    public bool TryAddPlayer(PlayerInfo player)
    {
        if (_players.Count >= MaxPlayers)
            return false;
            
        // Assign next available garage position
        int positionIndex = _players.Count;
        if (positionIndex < _trackGaragePositions.Length)
        {
            _playerPositions.TryAdd(player.Id, positionIndex);
        }
            
        return _players.TryAdd(player.Id, player);
    }
    
    public bool TryRemovePlayer(string playerId)
    {
        _playerPositions.TryRemove(playerId, out _);
        return _players.TryRemove(playerId, out _);
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
    
    public int PlayerCount => _players.Count;

    public void StartGame()
    {
        IsActive = true;
    }

    public bool TryJoinRoom(string playerId, PlayerInfo player)
    {
        if (IsActive || _players.Count >= MaxPlayers || _players.ContainsKey(playerId))
            return false;

        return _players.TryAdd(playerId, player);
    }
}