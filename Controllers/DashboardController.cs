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
    }
}