using System.Linq;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;

[Route("api/[controller]")]
[ApiController]
public class RoomDataController : ControllerBase
{
    private readonly RacingServer _server;

    public RoomDataController(RacingServer server)
    {
        _server = server;
    }

    [HttpGet("rooms")]
    public IActionResult GetAllRooms()
    {
        var rooms = _server.GetAllRooms();
        var roomData = rooms.Select(r => new {
            id = r.Id,
            name = r.Name,
            playerCount = r.PlayerCount,
            isActive = r.IsActive,
            hostId = r.HostId,
            createdAt = r.CreatedAt
        }).ToList();
        
        return Ok(roomData);
    }

    [HttpGet("room/{roomId}/players")]
    public IActionResult GetRoomPlayers(string roomId)
    {
        var rooms = _server.GetAllRooms();
        var room = rooms.FirstOrDefault(r => r.Id == roomId);
        
        if (room == null)
            return NotFound(new { error = "Room not found" });
            
        var players = room.Players.Select(p => new {
            id = p.Id,
            name = p.Name,
            position = new {
                x = p.Position.X,
                y = p.Position.Y,
                z = p.Position.Z
            },
            rotation = new {
                x = p.Rotation.X,
                y = p.Rotation.Y,
                z = p.Rotation.Z,
                w = p.Rotation.W
            }
        }).ToList();
        
        return Ok(players);
    }
}