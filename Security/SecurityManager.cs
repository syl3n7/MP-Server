using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MP.Server.Services;

namespace MP.Server.Security
{
    /// <summary>
    /// Central security manager that coordinates packet validation, rate limiting, and anti-cheat detection
    /// </summary>
    public class SecurityManager : IDisposable
    {
        private readonly PacketValidator _packetValidator;
        private readonly RateLimiter _rateLimiter;
        private readonly SecurityConfig _config;
        private readonly ILogger? _logger;
        private readonly DatabaseLoggingService? _loggingService;
        private readonly ConcurrentDictionary<string, PlayerSecurityInfo> _playerSecurity = new();
        private readonly ConcurrentQueue<SecurityEvent> _securityEvents = new();
        
        public SecurityManager(SecurityConfig config, ILogger? logger = null, DatabaseLoggingService? loggingService = null)
        {
            _config = config ?? new SecurityConfig();
            _logger = logger;
            _loggingService = loggingService;
            
            _packetValidator = new PacketValidator(logger as ILogger<PacketValidator> ?? NullLogger<PacketValidator>.Instance);
            _rateLimiter = new RateLimiter(logger);
            
            _logger?.LogInformation("Security Manager initialized with configuration");
        }
        
        /// <summary>
        /// Validate a UDP packet from a client
        /// </summary>
        public ValidationResult ValidateUdpPacket(string clientId, byte[] packetData, DateTime timestamp)
        {
            try
            {
                // Check rate limiting first (fastest check)
                if (_config.RateLimiting.EnableRateLimiting && !_rateLimiter.AllowUdpPacket(clientId))
                {
                    RecordSecurityEvent(SecurityEventType.RateLimitExceeded, clientId, 
                        "UDP rate limit exceeded", severity: 2);
                    return ValidationResult.Reject("Rate limit exceeded");
                }
                
                // Validate packet structure and content
                var validationResult = ValidatePacketContent(clientId, packetData);
                
                if (!validationResult.IsValid)
                {
                    // Record security violation
                    RecordSecurityEvent(SecurityEventType.PacketValidationFailure, clientId, 
                        $"Packet validation failed: {validationResult.Reason}", severity: 3);
                    
                    // Track violations for this player
                    TrackViolation(clientId, validationResult.Reason ?? "Unknown violation");
                }
                
                return validationResult;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error validating UDP packet for client {ClientId}", clientId);
                return ValidationResult.Reject("Internal validation error");
            }
        }
        
        /// <summary>
        /// Validate packet content by parsing and routing to appropriate validator
        /// </summary>
        private ValidationResult ValidatePacketContent(string clientId, byte[] packetData)
        {
            try
            {
                // Try to parse as JSON to determine packet type
                JsonElement packet;
                string message = System.Text.Encoding.UTF8.GetString(packetData);
                
                try
                {
                    using var document = JsonDocument.Parse(message);
                    packet = document.RootElement.Clone(); // Clone to avoid disposal issues
                }
                catch (JsonException)
                {
                    return ValidationResult.Reject("Invalid JSON format");
                }
                
                // Check for command type
                if (!packet.TryGetProperty("command", out var commandElement))
                {
                    return ValidationResult.Reject("Missing command field");
                }
                
                string? command = commandElement.GetString();
                
                // Route to appropriate validator based on command type
                return command switch
                {
                    "UPDATE" => _packetValidator.ValidatePositionUpdate(clientId, packet),
                    "INPUT" => _packetValidator.ValidateInputCommand(clientId, packet),
                    _ => ValidationResult.Reject($"Unknown command type: {command}")
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error validating packet content for client {ClientId}", clientId);
                return ValidationResult.Reject("Validation error");
            }
        }

        /// <summary>
        /// Check if a TCP message from a client should be allowed
        /// </summary>
        public bool AllowTcpMessage(string clientId)
        {
            if (!_config.RateLimiting.EnableRateLimiting)
                return true;
                
            var allowed = _rateLimiter.AllowTcpMessage(clientId);
            
            if (!allowed)
            {
                RecordSecurityEvent(SecurityEventType.RateLimitExceeded, clientId, 
                    "TCP rate limit exceeded", severity: 2);
            }
            
            return allowed;
        }
        
        /// <summary>
        /// Get security statistics for a player
        /// </summary>
        public PlayerSecurityStats GetPlayerStats(string clientId)
        {
            var rateStats = _rateLimiter.GetClientStats(clientId);
            var securityInfo = _playerSecurity.GetOrAdd(clientId, _ => new PlayerSecurityInfo());
            
            return new PlayerSecurityStats
            {
                ClientId = clientId,
                TcpMessagesPerSecond = rateStats.TcpMessagesPerSecond,
                UdpPacketsPerSecond = rateStats.UdpPacketsPerSecond,
                TotalViolations = securityInfo.TotalViolations,
                RecentViolations = securityInfo.GetRecentViolations(_config.AntiCheat.ViolationWindowMinutes),
                LastActivity = rateStats.LastActivity,
                ThreatLevel = CalculateThreatLevel(securityInfo)
            };
        }
        
        /// <summary>
        /// Get recent security events
        /// </summary>
        public List<SecurityEvent> GetRecentEvents(int maxCount = 100)
        {
            return _securityEvents.TakeLast(Math.Min(maxCount, _config.Logging.MaxLogEntries)).ToList();
        }
        
        /// <summary>
        /// Remove all security data for a disconnected client
        /// </summary>
        public void RemoveClient(string clientId)
        {
            _rateLimiter.RemoveClient(clientId);
            _packetValidator.RemovePlayerState(clientId);
            _playerSecurity.TryRemove(clientId, out _);
            
            _logger?.LogDebug("Removed security data for client {ClientId}", clientId);
        }
        
        private void TrackViolation(string clientId, string violationType)
        {
            var info = _playerSecurity.GetOrAdd(clientId, _ => new PlayerSecurityInfo());
            info.AddViolation(violationType);
            
            var recentViolations = info.GetRecentViolations(_config.AntiCheat.ViolationWindowMinutes);
            
            // Check if player should be kicked
            if (_config.AntiCheat.EnableAutoKick && recentViolations >= _config.AntiCheat.ViolationThreshold)
            {
                RecordSecurityEvent(SecurityEventType.PlayerKicked, clientId, 
                    $"Auto-kicked for {recentViolations} violations in {_config.AntiCheat.ViolationWindowMinutes} minutes", 
                    severity: 4);
                
                // TODO: Implement actual kick mechanism (would need server reference)
                _logger?.LogWarning("Player {ClientId} should be kicked for excessive violations", clientId);
            }
        }
        
        private void RecordSecurityEvent(SecurityEventType eventType, string clientId, string description, int severity = 1)
        {
            if (!_config.Logging.LogSecurityEvents)
                return;
                
            var securityEvent = new SecurityEvent
            {
                EventType = eventType,
                ClientId = clientId,
                Description = description,
                Severity = severity
            };
            
            _securityEvents.Enqueue(securityEvent);
            
            // Trim old events if we exceed the limit
            while (_securityEvents.Count > _config.Logging.MaxLogEntries)
            {
                _securityEvents.TryDequeue(out _);
            }
            
            // Log to regular logging system based on severity
            var logLevel = severity switch
            {
                1 => LogLevel.Debug,
                2 => LogLevel.Information,
                3 => LogLevel.Warning,
                4 => LogLevel.Error,
                _ => LogLevel.Critical
            };
            
            _logger?.Log(logLevel, "Security Event [{EventType}] Client: {ClientId} - {Description}", 
                eventType, clientId, description);
            
            // Log to database if logging service is available
            if (_loggingService != null)
            {
                var playerInfo = _playerSecurity.TryGetValue(clientId, out var info) ? info : null;
                var additionalData = new
                {
                    EventType = eventType.ToString(),
                    ThreatLevel = playerInfo != null ? CalculateThreatLevel(playerInfo) : 0,
                    TotalViolations = playerInfo?.TotalViolations ?? 0,
                    Timestamp = DateTime.UtcNow
                };
                
                _ = _loggingService.LogSecurityEventAsync(
                    eventType: eventType.ToString(),
                    sessionId: clientId,
                    ipAddress: "Unknown", // Would need to be passed from calling context
                    playerName: null, // Would need to be passed from calling context
                    severity: severity,
                    description: description,
                    additionalData: additionalData
                );
            }
        }
        
        private int CalculateThreatLevel(PlayerSecurityInfo info)
        {
            var recentViolations = info.GetRecentViolations(_config.AntiCheat.ViolationWindowMinutes);
            
            return recentViolations switch
            {
                0 => 0,  // No threat
                1 => 1,  // Low threat
                2 => 2,  // Medium threat
                >= 3 => 3,  // High threat
                _ => 0   // Default case for any other values
            };
        }
        
        public void Dispose()
        {
            _rateLimiter?.Dispose();
        }
    }
    
    /// <summary>
    /// Tracks security information for a single player
    /// </summary>
    internal class PlayerSecurityInfo
    {
        private readonly List<ViolationRecord> _violations = new();
        private readonly object _lock = new();
        
        public int TotalViolations { get; private set; }
        
        public void AddViolation(string type)
        {
            lock (_lock)
            {
                _violations.Add(new ViolationRecord { Type = type, Timestamp = DateTime.UtcNow });
                TotalViolations++;
            }
        }
        
        public int GetRecentViolations(int windowMinutes)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-windowMinutes);
            
            lock (_lock)
            {
                return _violations.Count(v => v.Timestamp >= cutoff);
            }
        }
        
        private class ViolationRecord
        {
            public string Type { get; set; } = "";
            public DateTime Timestamp { get; set; }
        }
    }
    
    /// <summary>
    /// Security statistics for a player
    /// </summary>
    public class PlayerSecurityStats
    {
        public string ClientId { get; set; } = "";
        public int TcpMessagesPerSecond { get; set; }
        public int UdpPacketsPerSecond { get; set; }
        public int TotalViolations { get; set; }
        public int RecentViolations { get; set; }
        public DateTime LastActivity { get; set; }
        public int ThreatLevel { get; set; } // 0-3 scale
    }
}
