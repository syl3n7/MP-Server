using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using MP.Server.Security;
using MP.Server.Services;

namespace MP.Server.Controllers
{
    public class DashboardController : Controller
    {
        private readonly ServerManagementService _serverManagement;
        private readonly UserManagementService? _userService;

        public DashboardController(ServerManagementService serverManagement, UserManagementService? userService = null)
        {
            _serverManagement = serverManagement;
            _userService = userService;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public IActionResult GetStats()
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var sessions = server.GetAllSessions();
            var rooms = server.GetAllRooms();
            
            var stats = new
            {
                uptime = FormatUptime(DateTime.UtcNow - server.StartTime),
                activeSessions = sessions.Count,
                totalRooms = rooms.Count,
                activeGames = rooms.Count(r => r.IsActive),
                playersInRooms = rooms.Sum(r => r.PlayerCount)
            };

            return Json(stats);
        }

        // Helper method to format uptime in a human readable format
        private string FormatUptime(TimeSpan timeSpan)
        {
            if (timeSpan.TotalDays >= 1)
            {
                return $"{timeSpan.Days}d {timeSpan.Hours}h {timeSpan.Minutes}m";
            }
            else if (timeSpan.TotalHours >= 1)
            {
                return $"{timeSpan.Hours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";
            }
            else if (timeSpan.TotalMinutes >= 1)
            {
                return $"{timeSpan.Minutes}m {timeSpan.Seconds}s";
            }
            else
            {
                return $"{timeSpan.Seconds}s";
            }
        }

        [HttpGet]
        public IActionResult GetRooms()
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var rooms = server.GetAllRooms().Select(r => new 
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
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var sessions = server.GetAllSessions().Select(s => new
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
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { success = false, message = "Server is not running" });
            }

            if (string.IsNullOrEmpty(roomId))
            {
                return Json(new { success = false, message = "Room ID is required" });
            }

            var room = server.GetAllRooms().FirstOrDefault(r => r.Id == roomId);
            if (room == null)
            {
                return Json(new { success = false, message = "Room not found" });
            }

            // Get all players in the room
            var playersInRoom = server.GetAllSessions().Where(s => s.CurrentRoomId == roomId).ToList();
            
            // Reset currentRoomId for all players in the room
            foreach (var player in playersInRoom)
            {
                room.TryRemovePlayer(player.Id);
                player.CurrentRoomId = null;
            }

            // Remove the room
            server.RemoveRoom(roomId);

            return Json(new { success = true, message = $"Room '{room.Name}' closed and {playersInRoom.Count} players removed" });
        }

        [HttpPost]
        public IActionResult CloseAllRooms()
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { success = false, message = "Server is not running" });
            }

            var rooms = server.GetAllRooms().ToList();
            int roomCount = rooms.Count;
            int playerCount = 0;

            foreach (var room in rooms)
            {
                // Get all players in the room
                var playersInRoom = server.GetAllSessions().Where(s => s.CurrentRoomId == room.Id).ToList();
                playerCount += playersInRoom.Count;
                
                // Reset currentRoomId for all players in the room
                foreach (var player in playersInRoom)
                {
                    room.TryRemovePlayer(player.Id);
                    player.CurrentRoomId = null;
                }

                // Remove the room
                server.RemoveRoom(room.Id);
            }

            return Json(new { success = true, message = $"Closed {roomCount} rooms and removed {playerCount} players from rooms" });
        }

        [HttpPost]
        public async Task<IActionResult> DisconnectPlayer(string sessionId)
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { success = false, message = "Server is not running" });
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                return Json(new { success = false, message = "Session ID is required" });
            }

            var player = server.GetPlayerSession(sessionId);
            if (player == null)
            {
                return Json(new { success = false, message = "Player not found" });
            }

            // If player is in a room, remove them
            if (!string.IsNullOrEmpty(player.CurrentRoomId))
            {
                var room = server.GetAllRooms().FirstOrDefault(r => r.Id == player.CurrentRoomId);
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
                        server.RemoveRoom(room.Id);
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
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { success = false, message = "Server is not running" });
            }

            var sessions = server.GetAllSessions().ToList();
            int sessionCount = sessions.Count;

            foreach (var player in sessions)
            {
                // If player is in a room, remove them
                if (!string.IsNullOrEmpty(player.CurrentRoomId))
                {
                    var room = server.GetAllRooms().FirstOrDefault(r => r.Id == player.CurrentRoomId);
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
            var emptyRooms = server.GetAllRooms().Where(r => r.PlayerCount == 0 && !r.IsActive).ToList();
            foreach (var room in emptyRooms)
            {
                server.RemoveRoom(room.Id);
            }

            return Json(new { success = true, message = $"Disconnected {sessionCount} players" });
        }

        // Security Monitoring Endpoints

        [HttpGet]
        public IActionResult GetSecurityStats()
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var sessions = server.GetAllSessions();
            var securityEvents = server.SecurityManager.GetRecentEvents(100);
            
            var stats = new
            {
                totalEvents = securityEvents.Count,
                recentEvents = securityEvents.Count(e => e.Timestamp >= DateTime.UtcNow.AddMinutes(-5)),
                eventsByType = securityEvents.GroupBy(e => e.EventType.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                threatLevels = sessions.Select(s => server.SecurityManager.GetPlayerStats(s.Id))
                    .GroupBy(stats => stats.ThreatLevel)
                    .ToDictionary(g => g.Key, g => g.Count()),
                highThreatPlayers = sessions.Select(s => server.SecurityManager.GetPlayerStats(s.Id))
                    .Where(stats => stats.ThreatLevel >= 2)
                    .Count()
            };

            return Json(stats);
        }

        [HttpGet]
        public IActionResult GetSecurityEvents(int limit = 50)
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var events = server.SecurityManager.GetRecentEvents(limit)
                .OrderByDescending(e => e.Timestamp)
                .Select(e => new
                {
                    timestamp = e.Timestamp,
                    eventType = e.EventType.ToString(),
                    clientId = e.ClientId,
                    description = e.Description,
                    severity = e.Severity,
                    additionalData = e.AdditionalData
                });

            return Json(events);
        }

        [HttpGet]
        public IActionResult GetPlayerSecurityDetails()
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var sessions = server.GetAllSessions();
            var playerSecurityData = sessions.Select(s =>
            {
                var stats = server.SecurityManager.GetPlayerStats(s.Id);
                return new
                {
                    id = s.Id,
                    name = s.PlayerName,
                    currentRoomId = s.CurrentRoomId,
                    threatLevel = stats.ThreatLevel,
                    totalViolations = stats.TotalViolations,
                    recentViolations = stats.RecentViolations,
                    tcpRate = stats.TcpMessagesPerSecond,
                    udpRate = stats.UdpPacketsPerSecond,
                    lastActivity = stats.LastActivity,
                    isAuthenticated = s.IsAuthenticated
                };
            }).OrderByDescending(p => p.threatLevel).ThenByDescending(p => p.recentViolations);

            return Json(playerSecurityData);
        }

        [HttpPost]
        public async Task<IActionResult> BanPlayer(string sessionId, string reason = "Security violation")
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { success = false, message = "Server is not running" });
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                return Json(new { success = false, message = "Session ID is required" });
            }

            var player = server.GetPlayerSession(sessionId);
            if (player == null)
            {
                return Json(new { success = false, message = "Player not found" });
            }

            // Remove from room if in one
            if (!string.IsNullOrEmpty(player.CurrentRoomId))
            {
                var room = server.GetAllRooms().FirstOrDefault(r => r.Id == player.CurrentRoomId);
                if (room != null)
                {
                    room.TryRemovePlayer(sessionId);
                    
                    // Transfer host if needed
                    if (room.HostId == sessionId && room.PlayerCount > 0)
                    {
                        var newHost = room.Players.FirstOrDefault();
                        if (newHost != null)
                        {
                            room.HostId = newHost.Id;
                        }
                    }
                    
                    if (room.PlayerCount == 0 && !room.IsActive)
                    {
                        server.RemoveRoom(room.Id);
                    }
                }
            }

            // Send ban message and disconnect
            await player.SendJsonAsync(new { command = "BANNED", reason = reason });
            await player.DisconnectAsync();

            return Json(new { success = true, message = $"Player '{player.PlayerName}' has been banned for: {reason}" });
        }

        [HttpGet]
        public IActionResult GetRateLimitStatus()
        {
            var server = _serverManagement.GetServer();
            if (server == null)
            {
                return Json(new { error = "Server is not running" });
            }

            var sessions = server.GetAllSessions();
            var rateLimitData = sessions.Select(s =>
            {
                var stats = server.SecurityManager.GetPlayerStats(s.Id);
                return new
                {
                    sessionId = s.Id,
                    playerName = s.PlayerName,
                    tcpRate = stats.TcpMessagesPerSecond,
                    udpRate = stats.UdpPacketsPerSecond,
                    tcpLimit = 10, // From RateLimiter.Limits.TCP_MESSAGES_PER_SECOND
                    udpLimit = 60, // From RateLimiter.Limits.UDP_PACKETS_PER_SECOND
                    tcpUtilization = Math.Round((stats.TcpMessagesPerSecond / 10.0) * 100, 1),
                    udpUtilization = Math.Round((stats.UdpPacketsPerSecond / 60.0) * 100, 1),
                    lastActivity = stats.LastActivity
                };
            }).OrderByDescending(p => Math.Max(p.tcpUtilization, p.udpUtilization));

            return Json(rateLimitData);
        }

        // User Management Endpoints

        [HttpGet]
        public async Task<IActionResult> GetUserStats()
        {
            if (_userService == null)
            {
                return Json(new { error = "User management service not available" });
            }

            var stats = await _userService.GetUserStatisticsAsync();
            return Json(stats);
        }

        [HttpGet]
        public async Task<IActionResult> GetAllUsers(int page = 1, int pageSize = 10)
        {
            if (_userService == null)
            {
                return Json(new { error = "User management service not available" });
            }

            var users = await _userService.GetAllUsersAsync(page, pageSize);
            return Json(users);
        }

        [HttpGet]
        public async Task<IActionResult> GetOnlineUsers()
        {
            if (_userService == null)
            {
                return Json(new { error = "User management service not available" });
            }

            var sessions = await _userService.GetActiveSessionsAsync();
            return Json(sessions);
        }

        [HttpPost]
        public async Task<IActionResult> BanUserAccount([FromBody] BanUserRequest request)
        {
            if (_userService == null)
            {
                return Json(new { success = false, message = "User management service not available" });
            }

            if (string.IsNullOrEmpty(request.UserId) || string.IsNullOrEmpty(request.Reason))
            {
                return Json(new { success = false, message = "User ID and reason are required" });
            }

            var result = await _userService.BanUserAsync(request.UserId, request.Reason, request.BanDurationDays);
            return Json(new { success = result.Success, message = result.Message });
        }

        [HttpPost]
        public async Task<IActionResult> UnbanUserAccount([FromBody] UnbanUserRequest request)
        {
            if (_userService == null)
            {
                return Json(new { success = false, message = "User management service not available" });
            }

            if (string.IsNullOrEmpty(request.UserId))
            {
                return Json(new { success = false, message = "User ID is required" });
            }

            var result = await _userService.UnbanUserAsync(request.UserId);
            return Json(new { success = result.Success, message = result.Message });
        }

        [HttpPost]
        public async Task<IActionResult> ForcePasswordReset([FromBody] ForcePasswordResetRequest request)
        {
            if (_userService == null)
            {
                return Json(new { success = false, message = "User management service not available" });
            }

            if (string.IsNullOrEmpty(request.UserId))
            {
                return Json(new { success = false, message = "User ID is required" });
            }

            var result = await _userService.AdminInitiatePasswordResetAsync(request.UserId);
            return Json(new { success = result.Success, message = result.Message });
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteUserAccount(string userId)
        {
            if (_userService == null)
            {
                return Json(new { success = false, message = "User management service not available" });
            }

            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { success = false, message = "User ID is required" });
            }

            var result = await _userService.DeleteUserAsync(userId);
            return Json(new { success = result.Success, message = result.Message });
        }

        [HttpGet]
        public async Task<IActionResult> GetUserAuditLog(string userId, int limit = 50)
        {
            if (_userService == null)
            {
                return Json(new { error = "User management service not available" });
            }

            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { error = "User ID is required" });
            }

            var auditLog = await _userService.GetUserAuditLogAsync(userId, limit);
            return Json(auditLog);
        }

        // Helper classes for request bodies
        public class BanUserRequest
        {
            public string UserId { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public int? BanDurationDays { get; set; }
        }

        public class UnbanUserRequest
        {
            public string UserId { get; set; } = string.Empty;
        }

        public class ForcePasswordResetRequest
        {
            public string UserId { get; set; } = string.Empty;
        }
    }
}
