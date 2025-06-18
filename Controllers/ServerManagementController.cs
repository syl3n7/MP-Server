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
        /// Get current server status for dashboard
        /// </summary>
        [HttpGet("dashboard-status")]
        public IActionResult GetDashboardServerStatus()
        {
            try
            {
                _logger.LogInformation("Dashboard requesting server status");
                
                var status = _serverManagement.GetServerStatus();
                
                _logger.LogInformation("Server status: IsRunning={IsRunning}, Message={Message}", 
                                      status.IsRunning, status.Message);
                
                return Ok(new { 
                    isRunning = status.IsRunning,
                    message = status.Message,
                    startTime = status.StartTime,
                    activeSessions = status.ActiveSessions,
                    activeRooms = status.ActiveRooms
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error getting dashboard server status");
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
        /// Start the racing server from dashboard with dashboard config format
        /// </summary>
        [HttpPost("dashboard-start")]
        public async Task<IActionResult> StartServerFromDashboard([FromBody] DashboardServerConfig config)
        {
            try
            {
                _logger.LogInformation("Dashboard requesting server start with config: TCP={TcpPort}, UDP={UdpPort}, TLS={UseTls}", 
                                      config.TcpPort, config.UdpPort, config.UseTls);

                if (config == null)
                {
                    return BadRequest(new { success = false, message = "Server configuration is required" });
                }

                // Convert dashboard config to server config
                var serverConfig = new ServerConfiguration
                {
                    TcpPort = config.TcpPort,
                    UdpPort = config.UdpPort,
                    UseTls = config.UseTls,
                    ConnectionString = _configuration.GetConnectionString("DefaultConnection") ?? 
                                     "Server=localhost;Database=mpserver;User=root;Password=yourpassword;Port=3306;"
                };

                var result = await _serverManagement.StartServerAsync(serverConfig);
                
                _logger.LogInformation("Server start result: Success={Success}, Message={Message}", 
                                      result.Success, result.Message);
                
                return Ok(new { 
                    success = result.Success, 
                    message = result.Message 
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error starting server from dashboard");
                return StatusCode(500, new { success = false, message = "Error starting server: " + ex.Message });
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
        /// Stop the racing server from dashboard
        /// </summary>
        [HttpPost("dashboard-stop")]
        public async Task<IActionResult> StopServerFromDashboard()
        {
            try
            {
                _logger.LogInformation("Dashboard requesting server stop");
                
                var result = await _serverManagement.StopServerAsync();
                
                _logger.LogInformation("Server stop result: Success={Success}, Message={Message}", 
                                      result.Success, result.Message);
                
                return Ok(new { 
                    success = result.Success, 
                    message = result.Message 
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error stopping server from dashboard");
                return StatusCode(500, new { success = false, message = "Error stopping server: " + ex.Message });
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
    /// Dashboard server configuration model
    /// </summary>
    public class DashboardServerConfig
    {
        public int TcpPort { get; set; } = 443;
        public int UdpPort { get; set; } = 443;
        public bool UseTls { get; set; } = true;
    }

}
