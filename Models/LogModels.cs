using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MP.Server.Models
{
    /// <summary>
    /// Represents a server log entry stored in the database
    /// </summary>
    public class ServerLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        [Required]
        [StringLength(50)]
        public string Level { get; set; } = string.Empty; // Debug, Info, Warning, Error, Critical
        
        [Required]
        [StringLength(100)]
        public string Category { get; set; } = string.Empty; // RacingServer, PlayerSession, Security, etc.
        
        [Required]
        [StringLength(1000)]
        public string Message { get; set; } = string.Empty;
        
        [StringLength(50)]
        public string? SessionId { get; set; }
        
        [StringLength(45)]
        public string? IpAddress { get; set; }
        
        [StringLength(100)]
        public string? PlayerName { get; set; }
        
        [StringLength(50)]
        public string? RoomId { get; set; }
        
        [Column(TypeName = "json")]
        public string? AdditionalData { get; set; } // JSON string for extra context
        
        public string? StackTrace { get; set; } // For errors/exceptions
    }
    
    /// <summary>
    /// Represents a connection event (connect/disconnect)
    /// </summary>
    public class ConnectionLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        [Required]
        [StringLength(20)]
        public string EventType { get; set; } = string.Empty; // Connect, Disconnect, Timeout
        
        [Required]
        [StringLength(50)]
        public string SessionId { get; set; } = string.Empty;
        
        [Required]
        [StringLength(45)]
        public string IpAddress { get; set; } = string.Empty;
        
        [StringLength(100)]
        public string? PlayerName { get; set; }
        
        [StringLength(20)]
        public string ConnectionType { get; set; } = string.Empty; // TCP, UDP
        
        public bool UsedTls { get; set; }
        
        public int? Duration { get; set; } // Connection duration in seconds (for disconnect events)
        
        [StringLength(500)]
        public string? Reason { get; set; } // Disconnect reason or error message
    }
    
    /// <summary>
    /// Represents a security-related event
    /// </summary>
    public class SecurityLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        [Required]
        [StringLength(50)]
        public string EventType { get; set; } = string.Empty; // AuthFailure, Suspicious, RateLimit, etc.
        
        [Required]
        [StringLength(45)]
        public string IpAddress { get; set; } = string.Empty;
        
        [StringLength(50)]
        public string? SessionId { get; set; }
        
        [StringLength(100)]
        public string? PlayerName { get; set; }
        
        [Required]
        public int Severity { get; set; } // 1=Low, 2=Medium, 3=High, 4=Critical
        
        [Required]
        [StringLength(1000)]
        public string Description { get; set; } = string.Empty;
        
        [Column(TypeName = "json")]
        public string? AdditionalData { get; set; }
        
        public bool IsResolved { get; set; } = false;
        
        [StringLength(500)]
        public string? Resolution { get; set; }
    }
    
    /// <summary>
    /// Represents a game room activity log
    /// </summary>
    public class RoomActivityLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        [Required]
        [StringLength(50)]
        public string RoomId { get; set; } = string.Empty;
        
        [Required]
        [StringLength(200)]
        public string RoomName { get; set; } = string.Empty;
        
        [Required]
        [StringLength(50)]
        public string EventType { get; set; } = string.Empty; // Created, PlayerJoined, PlayerLeft, GameStarted, GameEnded
        
        [StringLength(50)]
        public string? PlayerId { get; set; }
        
        [StringLength(100)]
        public string? PlayerName { get; set; }
        
        public int PlayerCount { get; set; }
        
        [StringLength(500)]
        public string? Details { get; set; }
    }
}
