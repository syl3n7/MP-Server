using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MP.Server.Models
{
    /// <summary>
    /// User entity representing a player account in the system
    /// </summary>
    [Index(nameof(Username), IsUnique = true)]
    [Index(nameof(Email), IsUnique = true)]
    public class User
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [StringLength(50)]
        public string Username { get; set; } = string.Empty;
        
        [Required]
        [StringLength(100)]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
        
        [Required]
        [StringLength(255)]
        public string PasswordHash { get; set; } = string.Empty;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        public bool IsEmailVerified { get; set; } = false;
        public bool IsActive { get; set; } = true;
        public bool IsBanned { get; set; } = false;
        
        // Profile Information
        [StringLength(100)]
        public string? DisplayName { get; set; }
        
        [StringLength(500)]
        public string? Bio { get; set; }
        
        [StringLength(50)]
        public string? Country { get; set; }
        
        public DateTime? DateOfBirth { get; set; }
        
        [StringLength(255)]
        public string? AvatarUrl { get; set; }
        
        // Gaming Statistics
        public int TotalRaces { get; set; } = 0;
        public int Wins { get; set; } = 0;
        public int Losses { get; set; } = 0;
        public float BestLapTime { get; set; } = 0.0f;
        public int TotalPlayTime { get; set; } = 0; // in minutes
        
        // Security
        public int FailedLoginAttempts { get; set; } = 0;
        public DateTime? LockedUntil { get; set; }
        public string? PasswordResetToken { get; set; }
        public DateTime? PasswordResetExpires { get; set; }
        public string? EmailVerificationToken { get; set; }
        
        // Computed Properties
        public float WinRate => TotalRaces > 0 ? (float)Wins / TotalRaces * 100 : 0.0f;
        public bool IsLocked => LockedUntil.HasValue && LockedUntil > DateTime.UtcNow;
    }
    
    /// <summary>
    /// User session tracking for active connections
    /// </summary>
    public class UserSession
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public int UserId { get; set; }
        
        [ForeignKey("UserId")]
        public User User { get; set; } = null!;
        
        [Required]
        [StringLength(50)]
        public string SessionId { get; set; } = string.Empty;
        
        [StringLength(45)] // IPv6 max length
        public string? IpAddress { get; set; }
        
        [StringLength(500)]
        public string? UserAgent { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        
        [StringLength(50)]
        public string? CurrentRoomId { get; set; }
    }
    
    /// <summary>
    /// Password reset requests tracking
    /// </summary>
    public class PasswordResetRequest
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        public int UserId { get; set; }
        
        [ForeignKey("UserId")]
        public User User { get; set; } = null!;
        
        [Required]
        [StringLength(255)]
        public string Token { get; set; } = string.Empty;
        
        [StringLength(45)]
        public string? IpAddress { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }
        public bool IsUsed { get; set; } = false;
        public DateTime? UsedAt { get; set; }
    }
    
    /// <summary>
    /// User login audit log
    /// </summary>
    public class LoginAuditLog
    {
        [Key]
        public int Id { get; set; }
        
        public int? UserId { get; set; }
        
        [ForeignKey("UserId")]
        public User? User { get; set; }
        
        [StringLength(50)]
        public string? Username { get; set; }
        
        [StringLength(45)]
        public string? IpAddress { get; set; }
        
        [StringLength(500)]
        public string? UserAgent { get; set; }
        
        public bool Success { get; set; }
        
        [StringLength(500)]
        public string? FailureReason { get; set; }
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
