using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

public sealed class GameRoom
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Race Room";
    public string? HostId { get; set; }
    public int MaxPlayers { get; set; } = 20;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    
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
    
    public bool ContainsPlayer(string playerId)
    {
        return _players.ContainsKey(playerId);
    }
    
    public int PlayerCount => _players.Count;

    public void StartGame()
    {
        IsActive = true;
    }
}