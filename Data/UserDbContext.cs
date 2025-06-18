using Microsoft.EntityFrameworkCore;
using MP.Server.Models;

namespace MP.Server.Data
{
    /// <summary>
    /// Entity Framework database context for user management
    /// </summary>
    public class UserDbContext : DbContext
    {
        public UserDbContext(DbContextOptions<UserDbContext> options) : base(options)
        {
        }
        
        // User management tables
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;
        public DbSet<PasswordResetRequest> PasswordResetRequests { get; set; } = null!;
        public DbSet<LoginAuditLog> LoginAuditLogs { get; set; } = null!;
        
        // Server logging tables
        public DbSet<ServerLog> ServerLogs { get; set; } = null!;
        public DbSet<ConnectionLog> ConnectionLogs { get; set; } = null!;
        public DbSet<SecurityLog> SecurityLogs { get; set; } = null!;
        public DbSet<RoomActivityLog> RoomActivityLogs { get; set; } = null!;
        
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // User configuration
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(e => e.Username).IsUnique();
                entity.HasIndex(e => e.Email).IsUnique();
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
                    
                entity.Property(e => e.LastLoginAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
                    
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // UserSession configuration
            modelBuilder.Entity<UserSession>(entity =>
            {
                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                    
                entity.HasIndex(e => e.SessionId).IsUnique();
                entity.HasIndex(e => e.UserId);
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
                    
                entity.Property(e => e.LastActivity)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // PasswordResetRequest configuration
            modelBuilder.Entity<PasswordResetRequest>(entity =>
            {
                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                    
                entity.HasIndex(e => e.Token).IsUnique();
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // LoginAuditLog configuration
            modelBuilder.Entity<LoginAuditLog>(entity =>
            {
                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.SetNull);
                    
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.UserId);
                
                entity.Property(e => e.Timestamp)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // ServerLog configuration
            modelBuilder.Entity<ServerLog>(entity =>
            {
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.Level);
                entity.HasIndex(e => e.Category);
                entity.HasIndex(e => e.SessionId);
                
                entity.Property(e => e.Timestamp)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // ConnectionLog configuration
            modelBuilder.Entity<ConnectionLog>(entity =>
            {
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.IpAddress);
                entity.HasIndex(e => e.EventType);
                
                entity.Property(e => e.Timestamp)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // SecurityLog configuration
            modelBuilder.Entity<SecurityLog>(entity =>
            {
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.IpAddress);
                entity.HasIndex(e => e.EventType);
                entity.HasIndex(e => e.Severity);
                entity.HasIndex(e => e.IsResolved);
                
                entity.Property(e => e.Timestamp)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
            
            // RoomActivityLog configuration
            modelBuilder.Entity<RoomActivityLog>(entity =>
            {
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.RoomId);
                entity.HasIndex(e => e.EventType);
                entity.HasIndex(e => e.PlayerId);
                
                entity.Property(e => e.Timestamp)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            });
        }
    }
}
