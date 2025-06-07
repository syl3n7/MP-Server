using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using MP.Server.Services;
using System.Threading.Tasks;

namespace MP.Server.Controllers
{
    /// <summary>
    /// API controller for server management operations
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ServerManagementController : Controller
    {
        private readonly ServerManagementService _serverManagement;
        private readonly ILogger<ServerManagementController> _logger;
        private readonly IConfiguration _configuration;

        public ServerManagementController(
            ServerManagementService serverManagement, 
            ILogger<ServerManagementController> logger,
            IConfiguration configuration)
        {
            _serverManagement = serverManagement;
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// Get current server status
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetServerStatus()
        {
            try
            {
                var status = _serverManagement.GetServerStatus();
                return Ok(status);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting server status");
                return StatusCode(500, new { message = "Error getting server status" });
            }
        }

        /// <summary>
        /// Start the racing server with specified configuration
        /// </summary>
        [HttpPost("start")]
        public async Task<IActionResult> StartServer([FromBody] ServerConfiguration config)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new { message = "Server configuration is required" });
                }

                var result = await _serverManagement.StartServerAsync(config);
                
                if (result.Success)
                {
                    return Ok(result);
                }
                else
                {
                    return BadRequest(result);
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error starting server");
                return StatusCode(500, new { message = "Error starting server" });
            }
        }

        /// <summary>
        /// Stop the racing server
        /// </summary>
        [HttpPost("stop")]
        public async Task<IActionResult> StopServer()
        {
            try
            {
                var result = await _serverManagement.StopServerAsync();
                
                if (result.Success)
                {
                    return Ok(result);
                }
                else
                {
                    return BadRequest(result);
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error stopping server");
                return StatusCode(500, new { message = "Error stopping server" });
            }
        }



        /// <summary>
        /// Get server statistics (if server is running)
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetServerStats()
        {
            try
            {
                var server = _serverManagement.GetServer();
                if (server == null)
                {
                    return BadRequest(new { message = "Server is not running" });
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

                return Ok(stats);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting server stats");
                return StatusCode(500, new { message = "Error getting server stats" });
            }
        }

        /// <summary>
        /// Get current server status - used by dashboard
        /// </summary>
        [HttpGet("dashboard-status")]
        public IActionResult DashboardStatus()
        {
            try
            {
                var status = _serverManagement.GetServerStatus();
                
                // Add basic status information
                var result = new
                {
                    isRunning = status.IsRunning,
                    message = status.Message,
                    startTime = status.StartTime,
                    activeSessions = status.ActiveSessions,
                    activeRooms = status.ActiveRooms
                };
                
                return Json(result);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting server status");
                return Json(new { 
                    isRunning = false, 
                    message = "Error getting server status"
                });
            }
        }

        /// <summary>
        /// Start server - used by dashboard
        /// </summary>
        [HttpPost("dashboard-start")]
        public async Task<IActionResult> DashboardStartServer([FromForm] int tcpPort = 443, [FromForm] int udpPort = 443, [FromForm] bool useTls = true)
        {
            try
            {
                // Get connection string from configuration
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    return Json(new { success = false, message = "Database connection string not configured in appsettings.json" });
                }

                var config = new ServerConfiguration
                {
                    ConnectionString = connectionString,
                    TcpPort = tcpPort,
                    UdpPort = udpPort,
                    UseTls = useTls
                };

                var result = await _serverManagement.StartServerAsync(config);
                return Json(new { success = result.Success, message = result.Message });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error starting server");
                return Json(new { success = false, message = $"Error starting server: {ex.Message}" });
            }
        }

        /// <summary>
        /// Stop server - used by dashboard
        /// </summary>
        [HttpPost("dashboard-stop")]
        public async Task<IActionResult> DashboardStopServer()
        {
            try
            {
                var result = await _serverManagement.StopServerAsync();
                return Json(new { success = result.Success, message = result.Message });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error stopping server");
                return Json(new { success = false, message = $"Error stopping server: {ex.Message}" });
            }
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
    }


}
