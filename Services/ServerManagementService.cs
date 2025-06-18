using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MP.Server.Data;
using MP.Server.Security;

namespace MP.Server.Services
{
    /// <summary>
    /// Service for managing the racing server lifecycle from the dashboard
    /// </summary>
    public class ServerManagementService
    {
        private readonly ILogger<ServerManagementService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly DatabaseLoggingService _dbLoggingService;
        private RacingServer? _server;
        private CancellationTokenSource? _serverCts;
        private bool _isRunning = false;

        public ServerManagementService(ILogger<ServerManagementService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            
            // Get database logging service
            using var scope = serviceProvider.CreateScope();
            _dbLoggingService = scope.ServiceProvider.GetRequiredService<DatabaseLoggingService>();
        }

        public bool IsServerRunning => _isRunning && _server != null;

        public RacingServer? GetServer() => _server;

        public ServerStatus GetServerStatus()
        {
            if (!_isRunning || _server == null)
            {
                return new ServerStatus
                {
                    IsRunning = false,
                    Message = "Server is stopped"
                };
            }

            return new ServerStatus
            {
                IsRunning = true,
                Message = "Server is running",
                StartTime = _server.StartTime,
                ActiveSessions = _server.GetAllSessions().Count,
                ActiveRooms = _server.GetAllRooms().Count
            };
        }

        public async Task<ServerOperationResult> StartServerAsync(ServerConfiguration config)
        {
            try
            {
                if (_isRunning)
                {
                    return new ServerOperationResult
                    {
                        Success = false,
                        Message = "Server is already running"
                    };
                }

                // Test database connection first
                var dbTestResult = await TestDatabaseConnectionAsync(config.ConnectionString);
                if (!dbTestResult.Success)
                {
                    return dbTestResult;
                }

                // Create server instance
                using var scope = _serviceProvider.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<RacingServer>>();
                var dbLoggingService = scope.ServiceProvider.GetRequiredService<DatabaseLoggingService>();
                
                _server = new RacingServer(config.TcpPort, config.UdpPort, logger, config.UseTls, null, null, dbLoggingService);
                _serverCts = new CancellationTokenSource();

                // Start the server
                await _server.StartAsync(_serverCts.Token);
                _isRunning = true;

                _logger.LogInformation("🚀 Racing server started successfully on TCP:{TcpPort} UDP:{UdpPort}", 
                    config.TcpPort, config.UdpPort);

                return new ServerOperationResult
                {
                    Success = true,
                    Message = $"Server started successfully on TCP:{config.TcpPort} UDP:{config.UdpPort}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to start racing server");
                return new ServerOperationResult
                {
                    Success = false,
                    Message = $"Failed to start server: {ex.Message}"
                };
            }
        }

        public async Task<ServerOperationResult> StopServerAsync()
        {
            try
            {
                if (!_isRunning || _server == null)
                {
                    return new ServerOperationResult
                    {
                        Success = false,
                        Message = "Server is not running"
                    };
                }

                _serverCts?.Cancel();
                await _server.StopAsync();
                
                _server?.Dispose();
                _serverCts?.Dispose();
                
                _server = null;
                _serverCts = null;
                _isRunning = false;

                _logger.LogInformation("🛑 Racing server stopped successfully");

                return new ServerOperationResult
                {
                    Success = true,
                    Message = "Server stopped successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to stop racing server");
                return new ServerOperationResult
                {
                    Success = false,
                    Message = $"Failed to stop server: {ex.Message}"
                };
            }
        }

        public async Task<ServerOperationResult> TestDatabaseConnectionAsync(string connectionString)
        {
            try
            {
                var optionsBuilder = new DbContextOptionsBuilder<UserDbContext>();
                optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

                using var context = new UserDbContext(optionsBuilder.Options);
                await context.Database.CanConnectAsync();
                await context.Database.EnsureCreatedAsync();

                return new ServerOperationResult
                {
                    Success = true,
                    Message = "Database connection successful"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Database connection test failed");
                return new ServerOperationResult
                {
                    Success = false,
                    Message = $"Database connection failed: {ex.Message}"
                };
            }
        }
    }

    /// <summary>
    /// Server configuration model
    /// </summary>
    public class ServerConfiguration
    {
        public int TcpPort { get; set; } = 443;
        public int UdpPort { get; set; } = 443;
        public bool UseTls { get; set; } = true;
        public string ConnectionString { get; set; } = "Server=localhost;Database=mpserver;User=root;Password=yourpassword;Port=3306;";
    }

    /// <summary>
    /// Server status information
    /// </summary>
    public class ServerStatus
    {
        public bool IsRunning { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? StartTime { get; set; }
        public int ActiveSessions { get; set; }
        public int ActiveRooms { get; set; }
    }

    /// <summary>
    /// Result of server operations
    /// </summary>
    public class ServerOperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
