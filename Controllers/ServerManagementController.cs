using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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

        public ServerManagementController(ServerManagementService serverManagement, ILogger<ServerManagementController> logger)
        {
            _serverManagement = serverManagement;
            _logger = logger;
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
        /// Test database connection
        /// </summary>
        [HttpPost("test-database")]
        public async Task<IActionResult> TestDatabase([FromBody] DatabaseTestRequest request)
        {
            try
            {
                if (request?.ConnectionString == null)
                {
                    return BadRequest(new { message = "Connection string is required" });
                }

                var result = await _serverManagement.TestDatabaseConnectionAsync(request.ConnectionString);
                
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
                _logger.LogError(ex, "Error testing database connection");
                return StatusCode(500, new { message = "Error testing database connection" });
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
                return Json(status);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting server status");
                return Json(new { isRunning = false, message = "Error getting server status" });
            }
        }

        /// <summary>
        /// Start server - used by dashboard
        /// </summary>
        [HttpPost("dashboard-start")]
        public async Task<IActionResult> DashboardStartServer([FromForm] string connectionString, [FromForm] int tcpPort = 443, [FromForm] int udpPort = 443, [FromForm] bool useTls = true)
        {
            try
            {
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

        /// <summary>
        /// Test database connection - used by dashboard
        /// </summary>
        [HttpPost("dashboard-test-db")]
        public async Task<IActionResult> DashboardTestDatabase([FromForm] string connectionString)
        {
            try
            {
                var result = await _serverManagement.TestDatabaseConnectionAsync(connectionString);
                return Json(new { success = result.Success, message = result.Message });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error testing database connection");
                return Json(new { success = false, message = $"Error testing database: {ex.Message}" });
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

    /// <summary>
    /// Request model for database testing
    /// </summary>
    public class DatabaseTestRequest
    {
        public string ConnectionString { get; set; } = string.Empty;
    }
}
