using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MP.Server.Data;
using MP.Server.Models;

namespace MP.Server.Services
{
    /// <summary>
    /// Service for logging server events to the database
    /// </summary>
    public class DatabaseLoggingService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DatabaseLoggingService> _logger;

        public DatabaseLoggingService(IServiceScopeFactory scopeFactory, ILogger<DatabaseLoggingService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task LogServerEventAsync(
            string level,
            string category,
            string message,
            string? sessionId = null,
            string? ipAddress = null,
            string? playerName = null,
            string? roomId = null,
            object? additionalData = null,
            Exception? exception = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var log = new ServerLog
                {
                    Level = level,
                    Category = category,
                    Message = message,
                    SessionId = sessionId,
                    IpAddress = ipAddress,
                    PlayerName = playerName,
                    RoomId = roomId,
                    AdditionalData = additionalData != null ? JsonSerializer.Serialize(additionalData) : null,
                    StackTrace = exception?.StackTrace
                };

                context.ServerLogs.Add(log);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Don't let logging failures crash the application
                _logger.LogError(ex, "Failed to save server log to database");
            }
        }

        public async Task LogConnectionEventAsync(
            string eventType,
            string sessionId,
            string ipAddress,
            string? playerName = null,
            string connectionType = "TCP",
            bool usedTls = false,
            int? duration = null,
            string? reason = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var log = new ConnectionLog
                {
                    EventType = eventType,
                    SessionId = sessionId,
                    IpAddress = ipAddress,
                    PlayerName = playerName,
                    ConnectionType = connectionType,
                    UsedTls = usedTls,
                    Duration = duration,
                    Reason = reason
                };

                context.ConnectionLogs.Add(log);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save connection log to database");
            }
        }

        public async Task LogSecurityEventAsync(
            string eventType,
            string ipAddress,
            int severity,
            string description,
            string? sessionId = null,
            string? playerName = null,
            object? additionalData = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var log = new SecurityLog
                {
                    EventType = eventType,
                    IpAddress = ipAddress,
                    SessionId = sessionId,
                    PlayerName = playerName,
                    Severity = severity,
                    Description = description,
                    AdditionalData = additionalData != null ? JsonSerializer.Serialize(additionalData) : null
                };

                context.SecurityLogs.Add(log);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save security log to database");
            }
        }

        public async Task LogRoomActivityAsync(
            string roomId,
            string roomName,
            string eventType,
            string? playerId = null,
            string? playerName = null,
            int playerCount = 0,
            string? details = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var log = new RoomActivityLog
                {
                    RoomId = roomId,
                    RoomName = roomName,
                    EventType = eventType,
                    PlayerId = playerId,
                    PlayerName = playerName,
                    PlayerCount = playerCount,
                    Details = details
                };

                context.RoomActivityLogs.Add(log);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save room activity log to database");
            }
        }

        public async Task<List<ServerLog>> GetRecentServerLogsAsync(int limit = 100, string? level = null, string? category = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var query = context.ServerLogs.AsQueryable();

                if (!string.IsNullOrEmpty(level))
                    query = query.Where(l => l.Level == level);

                if (!string.IsNullOrEmpty(category))
                    query = query.Where(l => l.Category == category);

                return await query
                    .OrderByDescending(l => l.Timestamp)
                    .Take(limit)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve server logs from database");
                return new List<ServerLog>();
            }
        }

        public async Task<List<ConnectionLog>> GetRecentConnectionLogsAsync(int limit = 100)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                return await context.ConnectionLogs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(limit)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve connection logs from database");
                return new List<ConnectionLog>();
            }
        }

        public async Task<List<SecurityLog>> GetRecentSecurityLogsAsync(int limit = 100, bool unresolvedOnly = false)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var query = context.SecurityLogs.AsQueryable();

                if (unresolvedOnly)
                    query = query.Where(l => !l.IsResolved);

                return await query
                    .OrderByDescending(l => l.Timestamp)
                    .Take(limit)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve security logs from database");
                return new List<SecurityLog>();
            }
        }

        public async Task<List<RoomActivityLog>> GetRecentRoomActivityLogsAsync(int limit = 100, string? roomId = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var query = context.RoomActivityLogs.AsQueryable();

                if (!string.IsNullOrEmpty(roomId))
                    query = query.Where(l => l.RoomId == roomId);

                return await query
                    .OrderByDescending(l => l.Timestamp)
                    .Take(limit)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve room activity logs from database");
                return new List<RoomActivityLog>();
            }
        }

        public async Task<Dictionary<string, int>> GetLogStatisticsAsync(DateTime since)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                var stats = new Dictionary<string, int>();

                // Server logs by level
                var serverLogStats = await context.ServerLogs
                    .Where(l => l.Timestamp >= since)
                    .GroupBy(l => l.Level)
                    .Select(g => new { Level = g.Key, Count = g.Count() })
                    .ToListAsync();

                foreach (var stat in serverLogStats)
                {
                    stats[$"ServerLogs_{stat.Level}"] = stat.Count;
                }

                // Connection events
                stats["TotalConnections"] = await context.ConnectionLogs
                    .Where(l => l.Timestamp >= since && l.EventType == "Connect")
                    .CountAsync();

                stats["TotalDisconnections"] = await context.ConnectionLogs
                    .Where(l => l.Timestamp >= since && l.EventType == "Disconnect")
                    .CountAsync();

                // Security events by severity
                var securityStats = await context.SecurityLogs
                    .Where(l => l.Timestamp >= since)
                    .GroupBy(l => l.Severity)
                    .Select(g => new { Severity = g.Key, Count = g.Count() })
                    .ToListAsync();

                foreach (var stat in securityStats)
                {
                    stats[$"SecurityEvents_Severity{stat.Severity}"] = stat.Count;
                }

                // Room activities
                stats["RoomActivities"] = await context.RoomActivityLogs
                    .Where(l => l.Timestamp >= since)
                    .CountAsync();

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve log statistics from database");
                return new Dictionary<string, int>();
            }
        }

        public async Task<object> ExportLogsAsync(string logType, DateTime? fromDate = null, DateTime? toDate = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                switch (logType.ToLower())
                {
                    case "server":
                        var serverQuery = context.ServerLogs.AsQueryable();
                        if (fromDate.HasValue)
                            serverQuery = serverQuery.Where(l => l.Timestamp >= fromDate.Value);
                        if (toDate.HasValue)
                            serverQuery = serverQuery.Where(l => l.Timestamp <= toDate.Value);
                        
                        return await serverQuery
                            .OrderByDescending(l => l.Timestamp)
                            .Select(l => new
                            {
                                l.Id,
                                l.Timestamp,
                                l.Level,
                                l.Message,
                                l.Category,
                                l.StackTrace
                            })
                            .ToListAsync();

                    case "connection":
                        var connectionQuery = context.ConnectionLogs.AsQueryable();
                        if (fromDate.HasValue)
                            connectionQuery = connectionQuery.Where(l => l.Timestamp >= fromDate.Value);
                        if (toDate.HasValue)
                            connectionQuery = connectionQuery.Where(l => l.Timestamp <= toDate.Value);
                        
                        return await connectionQuery
                            .OrderByDescending(l => l.Timestamp)
                            .Select(l => new
                            {
                                l.Id,
                                l.Timestamp,
                                l.EventType,
                                l.IpAddress,
                                l.SessionId,
                                l.PlayerName,
                                l.Reason
                            })
                            .ToListAsync();

                    case "security":
                        var securityQuery = context.SecurityLogs.AsQueryable();
                        if (fromDate.HasValue)
                            securityQuery = securityQuery.Where(l => l.Timestamp >= fromDate.Value);
                        if (toDate.HasValue)
                            securityQuery = securityQuery.Where(l => l.Timestamp <= toDate.Value);
                        
                        return await securityQuery
                            .OrderByDescending(l => l.Timestamp)
                            .Select(l => new
                            {
                                l.Id,
                                l.Timestamp,
                                l.EventType,
                                l.IpAddress,
                                l.SessionId,
                                l.PlayerName,
                                l.Severity,
                                l.Description,
                                l.AdditionalData,
                                l.IsResolved,
                                l.Resolution
                            })
                            .ToListAsync();

                    case "room":
                        var roomQuery = context.RoomActivityLogs.AsQueryable();
                        if (fromDate.HasValue)
                            roomQuery = roomQuery.Where(l => l.Timestamp >= fromDate.Value);
                        if (toDate.HasValue)
                            roomQuery = roomQuery.Where(l => l.Timestamp <= toDate.Value);
                        
                        return await roomQuery
                            .OrderByDescending(l => l.Timestamp)
                            .Select(l => new
                            {
                                l.Id,
                                l.Timestamp,
                                l.RoomId,
                                l.RoomName,
                                l.EventType,
                                l.PlayerId,
                                l.PlayerName,
                                l.PlayerCount,
                                l.Details
                            })
                            .ToListAsync();

                    default:
                        throw new ArgumentException($"Unknown log type: {logType}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export logs from database");
                throw;
            }
        }

        public async Task<int> ClearLogsAsync(string logType, DateTime? cutoffDate = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();

                int deletedCount = 0;
                var defaultCutoff = cutoffDate ?? DateTime.UtcNow.AddDays(-30); // Default to 30 days old

                switch (logType.ToLower())
                {
                    case "server":
                        var serverLogsToDelete = await context.ServerLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        context.ServerLogs.RemoveRange(serverLogsToDelete);
                        deletedCount = serverLogsToDelete.Count;
                        break;

                    case "connection":
                        var connectionLogsToDelete = await context.ConnectionLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        context.ConnectionLogs.RemoveRange(connectionLogsToDelete);
                        deletedCount = connectionLogsToDelete.Count;
                        break;

                    case "security":
                        var securityLogsToDelete = await context.SecurityLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        context.SecurityLogs.RemoveRange(securityLogsToDelete);
                        deletedCount = securityLogsToDelete.Count;
                        break;

                    case "room":
                        var roomLogsToDelete = await context.RoomActivityLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        context.RoomActivityLogs.RemoveRange(roomLogsToDelete);
                        deletedCount = roomLogsToDelete.Count;
                        break;

                    case "all":
                        var allServerLogs = await context.ServerLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        var allConnectionLogs = await context.ConnectionLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        var allSecurityLogs = await context.SecurityLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();
                        var allRoomLogs = await context.RoomActivityLogs
                            .Where(l => l.Timestamp < defaultCutoff)
                            .ToListAsync();

                        context.ServerLogs.RemoveRange(allServerLogs);
                        context.ConnectionLogs.RemoveRange(allConnectionLogs);
                        context.SecurityLogs.RemoveRange(allSecurityLogs);
                        context.RoomActivityLogs.RemoveRange(allRoomLogs);
                        
                        deletedCount = allServerLogs.Count + allConnectionLogs.Count + 
                                     allSecurityLogs.Count + allRoomLogs.Count;
                        break;

                    default:
                        throw new ArgumentException($"Unknown log type: {logType}");
                }

                await context.SaveChangesAsync();
                _logger.LogInformation("Cleared {DeletedCount} {LogType} logs older than {CutoffDate}", 
                    deletedCount, logType, defaultCutoff);

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear logs from database");
                throw;
            }
        }

        public async Task CleanupOldLogsAsync(int retentionDays = 30)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
                
                await ClearLogsAsync("server", cutoffDate);
                await ClearLogsAsync("connection", cutoffDate);
                await ClearLogsAsync("security", cutoffDate);
                await ClearLogsAsync("room", cutoffDate);

                _logger.LogInformation("Completed automatic log cleanup for logs older than {CutoffDate}", cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform automatic log cleanup");
            }
        }
    }
}
