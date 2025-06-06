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
        
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;
        public DbSet<PasswordResetRequest> PasswordResetRequests { get; set; } = null!;
        public DbSet<LoginAuditLog> LoginAuditLogs { get; set; } = null!;
        
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // User configuration
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(e => e.Username).IsUnique();
                entity.HasIndex(e => e.Email).IsUnique();
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("datetime('now')");
                    
                entity.Property(e => e.LastLoginAt)
                    .HasDefaultValueSql("datetime('now')");
                    
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("datetime('now')");
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
                    .HasDefaultValueSql("datetime('now')");
                    
                entity.Property(e => e.LastActivity)
                    .HasDefaultValueSql("datetime('now')");
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
                    .HasDefaultValueSql("datetime('now')");
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
                    .HasDefaultValueSql("datetime('now')");
            });
        }
    }
}
