using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MP.Server.Controllers
{
    public class DashboardController : Controller
    {
        private readonly RacingServer _server;

        public DashboardController(RacingServer server)
        {
            _server = server;
        }

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult RoomViewer(string id)
        {
            var room = _server.GetAllRooms().FirstOrDefault(r => r.Id == id);
        
            if (room == null)
            {
                return RedirectToAction("Index");
            }
        
            ViewBag.RoomName = room.Name;
            return View(id);
        }
        
        public IActionResult ModelUploader()
        {
            return View();
        }

        [HttpGet]
        public IActionResult GetStats()
        {
            var sessions = _server.GetAllSessions();
            var rooms = _server.GetAllRooms();
            
            var stats = new
            {
                uptime = DateTime.UtcNow - _server.StartTime,
                activeSessions = sessions.Count,
                totalRooms = rooms.Count,
                activeGames = rooms.Count(r => r.IsActive),
                playersInRooms = rooms.Sum(r => r.PlayerCount)
            };

            return Json(stats);
        }

        [HttpGet]
        public IActionResult GetRooms()
        {
            var rooms = _server.GetAllRooms().Select(r => new 
            {
                id = r.Id,
                name = r.Name,
                playerCount = r.PlayerCount,
                isActive = r.IsActive,
                hostId = r.HostId,
                createdAt = r.CreatedAt
            });

            return Json(rooms);
        }

        [HttpGet]
        public IActionResult GetSessions()
        {
            var sessions = _server.GetAllSessions().Select(s => new
            {
                id = s.Id,
                name = s.PlayerName,
                currentRoomId = s.CurrentRoomId,
                lastActivity = s.LastActivity
            });

            return Json(sessions);
        }

        [HttpPost]
        public IActionResult CloseRoom(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                return Json(new { success = false, message = "Room ID is required" });
            }

            var room = _server.GetAllRooms().FirstOrDefault(r => r.Id == roomId);
            if (room == null)
            {
                return Json(new { success = false, message = "Room not found" });
            }

            // Get all players in the room
            var playersInRoom = _server.GetAllSessions().Where(s => s.CurrentRoomId == roomId).ToList();
            
            // Reset currentRoomId for all players in the room
            foreach (var player in playersInRoom)
            {
                room.TryRemovePlayer(player.Id);
                player.CurrentRoomId = null;
            }

            // Remove the room
            _server.RemoveRoom(roomId);

            return Json(new { success = true, message = $"Room '{room.Name}' closed and {playersInRoom.Count} players removed" });
        }

        [HttpPost]
        public IActionResult CloseAllRooms()
        {
            var rooms = _server.GetAllRooms().ToList();
            int roomCount = rooms.Count;
            int playerCount = 0;

            foreach (var room in rooms)
            {
                // Get all players in the room
                var playersInRoom = _server.GetAllSessions().Where(s => s.CurrentRoomId == room.Id).ToList();
                playerCount += playersInRoom.Count;
                
                // Reset currentRoomId for all players in the room
                foreach (var player in playersInRoom)
                {
                    room.TryRemovePlayer(player.Id);
                    player.CurrentRoomId = null;
                }

                // Remove the room
                _server.RemoveRoom(room.Id);
            }

            return Json(new { success = true, message = $"Closed {roomCount} rooms and removed {playerCount} players from rooms" });
        }

        [HttpPost]
        public async Task<IActionResult> DisconnectPlayer(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return Json(new { success = false, message = "Session ID is required" });
            }

            var player = _server.GetPlayerSession(sessionId);
            if (player == null)
            {
                return Json(new { success = false, message = "Player not found" });
            }

            // If player is in a room, remove them
            if (!string.IsNullOrEmpty(player.CurrentRoomId))
            {
                var room = _server.GetAllRooms().FirstOrDefault(r => r.Id == player.CurrentRoomId);
                if (room != null)
                {
                    room.TryRemovePlayer(sessionId);
                    
                    // If the player was the host and there are still players, transfer host status
                    if (room.HostId == sessionId && room.PlayerCount > 0)
                    {
                        var newHost = room.Players.FirstOrDefault();
                        if (newHost != null)
                        {
                            room.HostId = newHost.Id;
                        }
                    }
                    
                    // If room is now empty and not active, remove it
                    if (room.PlayerCount == 0 && !room.IsActive)
                    {
                        _server.RemoveRoom(room.Id);
                    }
                }
            }

            // Send BYE message to client and disconnect
            await player.SendJsonAsync(new { command = "BYE_ADMIN", message = "Disconnected by administrator" });
            await player.DisconnectAsync();

            return Json(new { success = true, message = $"Player '{player.PlayerName}' has been disconnected" });
        }

        [HttpPost]
        public async Task<IActionResult> DisconnectAllPlayers()
        {
            var sessions = _server.GetAllSessions().ToList();
            int sessionCount = sessions.Count;

            foreach (var player in sessions)
            {
                // If player is in a room, remove them
                if (!string.IsNullOrEmpty(player.CurrentRoomId))
                {
                    var room = _server.GetAllRooms().FirstOrDefault(r => r.Id == player.CurrentRoomId);
                    if (room != null)
                    {
                        room.TryRemovePlayer(player.Id);
                    }
                }

                // Send BYE message to client and disconnect
                await player.SendJsonAsync(new { command = "BYE_ADMIN", message = "Server is closing all connections" });
                await player.DisconnectAsync();
            }

            // Clear all empty rooms
            var emptyRooms = _server.GetAllRooms().Where(r => r.PlayerCount == 0 && !r.IsActive).ToList();
            foreach (var room in emptyRooms)
            {
                _server.RemoveRoom(room.Id);
            }

            return Json(new { success = true, message = $"Disconnected {sessionCount} players" });
        }
    }
}