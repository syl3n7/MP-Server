using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MP.Server.Data;
using MP.Server.Models;

namespace MP.Server.Services
{
    /// <summary>
    /// Result returned by every AuthService operation.
    /// </summary>
    public record AuthResult(
        bool Success,
        string? Error,
        int? UserId,
        string? Username,
        string? Token
    );

    /// <summary>
    /// Database-backed authentication service.
    /// Owns registration, login, token-based auto-auth, and logout.
    /// Uses IDbContextFactory so it is safe to call concurrently from multiple sessions.
    /// </summary>
    public class AuthService
    {
        private readonly IDbContextFactory<UserDbContext> _dbFactory;
        private readonly ILogger<AuthService> _logger;

        private const int MaxFailedAttempts = 3;
        private const int LockoutMinutes = 30;
        private const int TokenExpiryDays = 30;
        private const int MinUsernameLength = 3;
        private const int MaxUsernameLength = 50;
        private const int MinPasswordLength = 6;

        public AuthService(IDbContextFactory<UserDbContext> dbFactory, ILogger<AuthService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Creates a new player account and returns a persistent login token.
        /// </summary>
        public async Task<AuthResult> RegisterAsync(
            string username,
            string password,
            string email = "",
            string? ipAddress = null)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                username.Length < MinUsernameLength ||
                username.Length > MaxUsernameLength)
                return Fail($"Username must be {MinUsernameLength}–{MaxUsernameLength} characters.");

            if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
                return Fail($"Password must be at least {MinPasswordLength} characters.");

            await using var db = await _dbFactory.CreateDbContextAsync();

            if (await db.Users.AnyAsync(u => u.Username == username))
                return Fail("Username already taken.");

            var resolvedEmail = string.IsNullOrWhiteSpace(email)
                ? $"{username}@placeholder.local"
                : email;

            if (!resolvedEmail.EndsWith("@placeholder.local") &&
                await db.Users.AnyAsync(u => u.Email == resolvedEmail))
                return Fail("Email already registered.");

            var user = new User
            {
                Username = username,
                Email = resolvedEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                DisplayName = username,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            var token = await IssueTokenAsync(db, user.Id, ipAddress);
            await WriteAuditAsync(db, user.Id, username, ipAddress, success: true, failureReason: null);

            _logger.LogInformation("✅ Registered new user {Username} (Id={UserId})", username, user.Id);

            return new AuthResult(true, null, user.Id, user.DisplayName ?? username, token);
        }

        /// <summary>
        /// Validates credentials, applies lockout policy, and returns a persistent login token.
        /// </summary>
        public async Task<AuthResult> LoginAsync(
            string username,
            string password,
            string? ipAddress = null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);

            if (user == null)
            {
                await WriteAuditAsync(db, null, username, ipAddress, false, "User not found");
                return Fail("Invalid username or password.");
            }

            if (!user.IsActive || user.IsBanned)
            {
                await WriteAuditAsync(db, user.Id, username, ipAddress, false, "Account disabled or banned");
                return Fail("Account is disabled.");
            }

            if (user.IsLocked)
            {
                await WriteAuditAsync(db, user.Id, username, ipAddress, false, $"Locked until {user.LockedUntil}");
                return Fail($"Account locked. Try again after {user.LockedUntil:HH:mm} UTC.");
            }

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    user.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                    _logger.LogWarning(
                        "Account {Username} locked after {Attempts} failed attempts",
                        username, user.FailedLoginAttempts);
                }
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await WriteAuditAsync(db, user.Id, username, ipAddress, false, "Wrong password");
                return Fail("Invalid username or password.");
            }

            // Successful login — reset lockout state
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            user.LastLoginAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var token = await IssueTokenAsync(db, user.Id, ipAddress);
            await WriteAuditAsync(db, user.Id, username, ipAddress, true, null);

            _logger.LogInformation("🔐 User {Username} (Id={UserId}) logged in", username, user.Id);

            return new AuthResult(true, null, user.Id, user.DisplayName ?? user.Username, token);
        }

        /// <summary>
        /// Validates a stored persistent token and authenticates the session silently.
        /// </summary>
        public async Task<AuthResult> AutoAuthAsync(string rawToken, string? ipAddress = null)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
                return Fail("Token is required.");

            var hash = HashToken(rawToken);

            await using var db = await _dbFactory.CreateDbContextAsync();

            var authToken = await db.UserAuthTokens
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash);

            if (authToken == null || !authToken.IsValid)
                return Fail("Token invalid or expired.");

            if (!authToken.User.IsActive || authToken.User.IsBanned)
                return Fail("Account is disabled.");

            authToken.LastUsedAt = DateTime.UtcNow;
            authToken.User.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "🔑 Auto-auth for {Username} (Id={UserId})",
                authToken.User.Username, authToken.UserId);

            return new AuthResult(
                true, null,
                authToken.UserId,
                authToken.User.DisplayName ?? authToken.User.Username,
                rawToken     // return the same token so the client keeps using it
            );
        }

        /// <summary>
        /// Revokes a token so it cannot be used for auto-auth again.
        /// </summary>
        public async Task RevokeTokenAsync(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) return;

            var hash = HashToken(rawToken);

            await using var db = await _dbFactory.CreateDbContextAsync();
            var entry = await db.UserAuthTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (entry != null)
            {
                entry.IsRevoked = true;
                await db.SaveChangesAsync();
            }
        }

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        private async Task<string> IssueTokenAsync(
            UserDbContext db,
            int userId,
            string? ipAddress)
        {
            var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

            db.UserAuthTokens.Add(new UserAuthToken
            {
                UserId = userId,
                TokenHash = HashToken(rawToken),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(TokenExpiryDays),
                CreatedFromIp = ipAddress
            });

            await db.SaveChangesAsync();
            return rawToken;
        }

        private static async Task WriteAuditAsync(
            UserDbContext db,
            int? userId,
            string? username,
            string? ipAddress,
            bool success,
            string? failureReason)
        {
            db.LoginAuditLogs.Add(new LoginAuditLog
            {
                UserId = userId,
                Username = username,
                IpAddress = ipAddress,
                Success = success,
                FailureReason = failureReason,
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        private static AuthResult Fail(string error) =>
            new(false, error, null, null, null);

        private static string HashToken(string rawToken)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(
                sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));
        }
    }
}
