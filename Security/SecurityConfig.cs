using System;
using System.ComponentModel.DataAnnotations;

namespace MP.Server.Security
{
    /// <summary>
    /// Centralized security configuration for the MP-Server
    /// Contains all configurable security parameters and limits
    /// </summary>
    public class SecurityConfig
    {
        /// <summary>
        /// Packet validation settings
        /// </summary>
        public PacketValidationConfig PacketValidation { get; set; } = new();
        
        /// <summary>
        /// Rate limiting settings
        /// </summary>
        public RateLimitingConfig RateLimiting { get; set; } = new();
        
        /// <summary>
        /// Anti-cheat detection settings
        /// </summary>
        public AntiCheatConfig AntiCheat { get; set; } = new();
        
        /// <summary>
        /// Security logging configuration
        /// </summary>
        public SecurityLoggingConfig Logging { get; set; } = new();
    }
    
    public class PacketValidationConfig
    {
        /// <summary>
        /// Maximum allowed position change per update (units)
        /// </summary>
        [Range(1, 1000)]
        public float MaxPositionJump { get; set; } = 50.0f;
        
        /// <summary>
        /// Maximum allowed speed (units per second)
        /// </summary>
        [Range(1, 500)]
        public float MaxSpeed { get; set; } = 200.0f;
        
        /// <summary>
        /// Maximum allowed angular velocity (radians per second)
        /// </summary>
        [Range(1, 50)]
        public float MaxAngularVelocity { get; set; } = 10.0f;
        
        /// <summary>
        /// Minimum time between updates (milliseconds)
        /// </summary>
        [Range(1, 100)]
        public int MinUpdateInterval { get; set; } = 16; // ~60 FPS
        
        /// <summary>
        /// Maximum time between updates (milliseconds)
        /// </summary>
        [Range(100, 10000)]
        public int MaxUpdateInterval { get; set; } = 5000; // 5 seconds
        
        /// <summary>
        /// World boundary limits (±units from origin)
        /// </summary>
        [Range(100, 10000)]
        public float WorldBounds { get; set; } = 1000.0f;
        
        /// <summary>
        /// Enable strict physics validation
        /// </summary>
        public bool EnablePhysicsValidation { get; set; } = true;
        
        /// <summary>
        /// Enable input range validation
        /// </summary>
        public bool EnableInputValidation { get; set; } = true;
        
        /// <summary>
        /// Enable packet structure validation
        /// </summary>
        public bool EnableStructureValidation { get; set; } = true;
    }
    
    public class RateLimitingConfig
    {
        /// <summary>
        /// Maximum TCP messages per second per client
        /// </summary>
        [Range(1, 100)]
        public int TcpMessagesPerSecond { get; set; } = 10;
        
        /// <summary>
        /// Maximum UDP packets per second per client
        /// </summary>
        [Range(10, 120)]
        public int UdpPacketsPerSecond { get; set; } = 60;
        
        /// <summary>
        /// Burst allowance above normal rate limit
        /// </summary>
        [Range(1, 20)]
        public int BurstAllowance { get; set; } = 5;
        
        /// <summary>
        /// Enable rate limiting
        /// </summary>
        public bool EnableRateLimiting { get; set; } = true;
        
        /// <summary>
        /// Cleanup interval for inactive clients (milliseconds)
        /// </summary>
        [Range(10000, 300000)]
        public int CleanupIntervalMs { get; set; } = 30000;
        
        /// <summary>
        /// Client timeout for rate limit tracking (milliseconds)
        /// </summary>
        [Range(30000, 600000)]
        public int ClientTimeoutMs { get; set; } = 60000;
    }
    
    public class AntiCheatConfig
    {
        /// <summary>
        /// Number of violations before taking action
        /// </summary>
        [Range(1, 20)]
        public int ViolationThreshold { get; set; } = 3;
        
        /// <summary>
        /// Time window for violation counting (minutes)
        /// </summary>
        [Range(1, 60)]
        public int ViolationWindowMinutes { get; set; } = 5;
        
        /// <summary>
        /// Enable automatic kicks for repeat offenders
        /// </summary>
        public bool EnableAutoKick { get; set; } = true;
        
        /// <summary>
        /// Enable temporary bans for severe violations
        /// </summary>
        public bool EnableTempBans { get; set; } = false;
        
        /// <summary>
        /// Temporary ban duration (minutes)
        /// </summary>
        [Range(1, 1440)]
        public int TempBanDurationMinutes { get; set; } = 15;
        
        /// <summary>
        /// Enable detailed cheat detection logging
        /// </summary>
        public bool EnableDetailedLogging { get; set; } = true;
    }
    
    public class SecurityLoggingConfig
    {
        /// <summary>
        /// Log all security events (violations, rate limits, etc.)
        /// </summary>
        public bool LogSecurityEvents { get; set; } = true;
        
        /// <summary>
        /// Log packet validation failures
        /// </summary>
        public bool LogValidationFailures { get; set; } = true;
        
        /// <summary>
        /// Log rate limiting events
        /// </summary>
        public bool LogRateLimiting { get; set; } = true;
        
        /// <summary>
        /// Log player statistics periodically
        /// </summary>
        public bool LogPlayerStats { get; set; } = false;
        
        /// <summary>
        /// Statistics logging interval (minutes)
        /// </summary>
        [Range(1, 60)]
        public int StatsLoggingIntervalMinutes { get; set; } = 5;
        
        /// <summary>
        /// Maximum log entries to keep in memory
        /// </summary>
        [Range(100, 10000)]
        public int MaxLogEntries { get; set; } = 1000;
    }
    
    /// <summary>
    /// Security event types for logging and monitoring
    /// </summary>
    public enum SecurityEventType
    {
        PacketValidationFailure,
        RateLimitExceeded,
        PhysicsViolation,
        InputViolation,
        StructureViolation,
        PlayerKicked,
        PlayerBanned,
        SuspiciousActivity,
        AuthenticationFailure
    }
    
    /// <summary>
    /// Security event data for logging
    /// </summary>
    public class SecurityEvent
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public SecurityEventType EventType { get; set; }
        public string ClientId { get; set; } = "";
        public string Description { get; set; } = "";
        public string? AdditionalData { get; set; }
        public int Severity { get; set; } = 1; // 1-5 scale
    }
}
