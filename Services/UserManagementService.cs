using System;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MP.Server.Data;
using MP.Server.Models;
using BCrypt.Net;

namespace MP.Server.Services
{
    /// <summary>
    /// Comprehensive user management service with authentication, profile management, and password recovery
    /// </summary>
    public class UserManagementService
    {
        private readonly UserDbContext _dbContext;
        private readonly ILogger<UserManagementService> _logger;
        private readonly EmailService _emailService;
        
        // Security constants
        private const int MaxFailedAttempts = 5;
        private const int LockoutDurationMinutes = 30;
        private const int PasswordResetTokenExpiryHours = 24;
        private const int EmailVerificationTokenExpiryHours = 48;
        
        public UserManagementService(
            UserDbContext dbContext, 
            ILogger<UserManagementService> logger,
            EmailService emailService)
        {
            _dbContext = dbContext;
            _logger = logger;
            _emailService = emailService;
        }
        
        #region User Registration and Authentication
        
        /// <summary>
        /// Register a new user with email verification
        /// </summary>
        public async Task<UserRegistrationResult> RegisterUserAsync(string username, string email, string password)
        {
            try
            {
                // Validate input
                if (string.IsNullOrWhiteSpace(username) || username.Length < 3 || username.Length > 50)
                    return UserRegistrationResult.CreateFailure("Username must be between 3 and 50 characters");
                    
                if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
                    return UserRegistrationResult.CreateFailure("Invalid email address");
                    
                if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                    return UserRegistrationResult.CreateFailure("Password must be at least 8 characters long");
                
                // Check if username or email already exists
                var existingUser = await _dbContext.Users
                    .Where(u => u.Username == username || u.Email == email)
                    .FirstOrDefaultAsync();
                    
                if (existingUser != null)
                {
                    if (existingUser.Username == username)
                        return UserRegistrationResult.CreateFailure("Username is already taken");
                    else
                        return UserRegistrationResult.CreateFailure("Email is already registered");
                }
                
                // Create new user
                var user = new User
                {
                    Username = username,
                    Email = email,
                    PasswordHash = HashPassword(password),
                    DisplayName = username,
                    EmailVerificationToken = GenerateSecureToken(),
                    IsEmailVerified = false,
                    IsActive = true
                };
                
                _dbContext.Users.Add(user);
                await _dbContext.SaveChangesAsync();
                
                // Send verification email
                await _emailService.SendEmailVerificationAsync(user.Email, user.EmailVerificationToken!);
                
                _logger.LogInformation("User registered successfully: {Username} ({Email})", username, email);
                
                return UserRegistrationResult.CreateSuccess(user.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering user: {Username}", username);
                return UserRegistrationResult.CreateFailure("Registration failed due to internal error");
            }
        }
        
        /// <summary>
        /// Authenticate user with enhanced security features
        /// </summary>
        public async Task<UserAuthenticationResult> AuthenticateUserAsync(string usernameOrEmail, string password, string? ipAddress = null, string? userAgent = null)
        {
            try
            {
                var user = await _dbContext.Users
                    .Where(u => u.Username == usernameOrEmail || u.Email == usernameOrEmail)
                    .FirstOrDefaultAsync();
                
                // Log attempt regardless of user existence
                var auditLog = new LoginAuditLog
                {
                    UserId = user?.Id,
                    Username = usernameOrEmail,
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    Success = false,
                    Timestamp = DateTime.UtcNow
                };
                
                if (user == null)
                {
                    auditLog.FailureReason = "User not found";
                    _dbContext.LoginAuditLogs.Add(auditLog);
                    await _dbContext.SaveChangesAsync();
                    
                    return UserAuthenticationResult.CreateFailure("Invalid username or password");
                }
                
                // Check if account is locked
                if (user.IsLocked)
                {
                    auditLog.FailureReason = "Account locked";
                    _dbContext.LoginAuditLogs.Add(auditLog);
                    await _dbContext.SaveChangesAsync();
                    
                    return UserAuthenticationResult.CreateFailure($"Account is locked until {user.LockedUntil:yyyy-MM-dd HH:mm:ss} UTC");
                }
                
                // Check if account is banned or inactive
                if (user.IsBanned)
                {
                    auditLog.FailureReason = "Account banned";
                    _dbContext.LoginAuditLogs.Add(auditLog);
                    await _dbContext.SaveChangesAsync();
                    
                    return UserAuthenticationResult.CreateFailure("Account has been banned");
                }
                
                if (!user.IsActive)
                {
                    auditLog.FailureReason = "Account inactive";
                    _dbContext.LoginAuditLogs.Add(auditLog);
                    await _dbContext.SaveChangesAsync();
                    
                    return UserAuthenticationResult.CreateFailure("Account is not active");
                }
                
                // Verify password
                if (!VerifyPassword(password, user.PasswordHash))
                {
                    // Increment failed attempts
                    user.FailedLoginAttempts++;
                    
                    if (user.FailedLoginAttempts >= MaxFailedAttempts)
                    {
                        user.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutDurationMinutes);
                        auditLog.FailureReason = $"Too many failed attempts, account locked for {LockoutDurationMinutes} minutes";
                    }
                    else
                    {
                        auditLog.FailureReason = $"Invalid password (attempt {user.FailedLoginAttempts}/{MaxFailedAttempts})";
                    }
                    
                    _dbContext.LoginAuditLogs.Add(auditLog);
                    await _dbContext.SaveChangesAsync();
                    
                    return UserAuthenticationResult.CreateFailure("Invalid username or password");
                }
                
                // Successful authentication
                user.FailedLoginAttempts = 0;
                user.LockedUntil = null;
                user.LastLoginAt = DateTime.UtcNow;
                
                auditLog.Success = true;
                auditLog.FailureReason = null;
                auditLog.UserId = user.Id;
                
                _dbContext.LoginAuditLogs.Add(auditLog);
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("User authenticated successfully: {Username}", user.Username);
                
                return UserAuthenticationResult.CreateSuccess(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error authenticating user: {UsernameOrEmail}", usernameOrEmail);
                return UserAuthenticationResult.CreateFailure("Authentication failed due to internal error");
            }
        }
        
        #endregion
        
        #region Password Recovery
        
        /// <summary>
        /// Initiate password recovery process
        /// </summary>
        public async Task<bool> InitiatePasswordResetAsync(string email, string? ipAddress = null)
        {
            try
            {
                var user = await _dbContext.Users
                    .Where(u => u.Email == email && u.IsActive)
                    .FirstOrDefaultAsync();
                
                if (user == null)
                {
                    // Don't reveal if email exists or not for security
                    _logger.LogWarning("Password reset attempted for non-existent email: {Email}", email);
                    return true;
                }
                
                // Generate reset token
                var token = GenerateSecureToken();
                var resetRequest = new Models.PasswordResetRequest
                {
                    UserId = user.Id,
                    Token = token,
                    IpAddress = ipAddress,
                    ExpiresAt = DateTime.UtcNow.AddHours(PasswordResetTokenExpiryHours)
                };
                
                // Invalidate existing reset requests
                var existingRequests = await _dbContext.PasswordResetRequests
                    .Where(r => r.UserId == user.Id && !r.IsUsed)
                    .ToListAsync();
                
                foreach (var request in existingRequests)
                {
                    request.IsUsed = true;
                    request.UsedAt = DateTime.UtcNow;
                }
                
                _dbContext.PasswordResetRequests.Add(resetRequest);
                await _dbContext.SaveChangesAsync();
                
                // Send reset email
                await _emailService.SendPasswordResetEmailAsync(user.Email, user.Username, token);
                
                _logger.LogInformation("Password reset initiated for user: {Username}", user.Username);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating password reset for email: {Email}", email);
                return false;
            }
        }
        
        /// <summary>
        /// Reset password using token
        /// </summary>
        public async Task<PasswordResetResult> ResetPasswordAsync(string token, string newPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
                    return PasswordResetResult.CreateFailure("Password must be at least 8 characters long");
                
                var resetRequest = await _dbContext.PasswordResetRequests
                    .Include(r => r.User)
                    .Where(r => r.Token == token && !r.IsUsed && r.ExpiresAt > DateTime.UtcNow)
                    .FirstOrDefaultAsync();
                
                if (resetRequest == null)
                    return PasswordResetResult.CreateFailure("Invalid or expired reset token");
                
                // Update password
                resetRequest.User.PasswordHash = HashPassword(newPassword);
                resetRequest.User.UpdatedAt = DateTime.UtcNow;
                resetRequest.User.FailedLoginAttempts = 0;
                resetRequest.User.LockedUntil = null;
                
                // Mark token as used
                resetRequest.IsUsed = true;
                resetRequest.UsedAt = DateTime.UtcNow;
                
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("Password reset successful for user: {Username}", resetRequest.User.Username);
                
                return PasswordResetResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting password with token: {Token}", token);
                return PasswordResetResult.CreateFailure("Password reset failed due to internal error");
            }
        }
        
        #endregion
        
        #region User Profile Management
        
        /// <summary>
        /// Get user profile by ID
        /// </summary>
        public async Task<User?> GetUserProfileAsync(int userId)
        {
            return await _dbContext.Users.FindAsync(userId);
        }
        
        /// <summary>
        /// Get user profile by username
        /// </summary>
        public async Task<User?> GetUserProfileByUsernameAsync(string username)
        {
            return await _dbContext.Users
                .Where(u => u.Username == username)
                .FirstOrDefaultAsync();
        }
        
        /// <summary>
        /// Update user profile
        /// </summary>
        public async Task<ProfileUpdateResult> UpdateUserProfileAsync(int userId, ProfileUpdateRequest request)
        {
            try
            {
                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return ProfileUpdateResult.CreateFailure("User not found");
                
                // Update profile fields
                if (!string.IsNullOrWhiteSpace(request.DisplayName))
                    user.DisplayName = request.DisplayName.Trim();
                
                if (!string.IsNullOrWhiteSpace(request.Bio))
                    user.Bio = request.Bio.Trim();
                
                if (!string.IsNullOrWhiteSpace(request.Country))
                    user.Country = request.Country.Trim();
                
                if (request.DateOfBirth.HasValue)
                    user.DateOfBirth = request.DateOfBirth.Value;
                
                if (!string.IsNullOrWhiteSpace(request.AvatarUrl))
                    user.AvatarUrl = request.AvatarUrl.Trim();
                
                user.UpdatedAt = DateTime.UtcNow;
                
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("Profile updated for user: {Username}", user.Username);
                
                return ProfileUpdateResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating profile for user: {UserId}", userId);
                return ProfileUpdateResult.CreateFailure("Profile update failed due to internal error");
            }
        }
        
        /// <summary>
        /// Change user password
        /// </summary>
        public async Task<PasswordChangeResult> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
        {
            try
            {
                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return PasswordChangeResult.CreateFailure("User not found");
                
                // Verify current password
                if (!VerifyPassword(currentPassword, user.PasswordHash))
                    return PasswordChangeResult.CreateFailure("Current password is incorrect");
                
                if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
                    return PasswordChangeResult.CreateFailure("New password must be at least 8 characters long");
                
                user.PasswordHash = HashPassword(newPassword);
                user.UpdatedAt = DateTime.UtcNow;
                
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("Password changed for user: {Username}", user.Username);
                
                return PasswordChangeResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing password for user: {UserId}", userId);
                return PasswordChangeResult.CreateFailure("Password change failed due to internal error");
            }
        }
        
        #endregion
        
        #region Session Management
        
        /// <summary>
        /// Create user session
        /// </summary>
        public async Task<UserSession> CreateUserSessionAsync(int userId, string sessionId, string? ipAddress = null, string? userAgent = null)
        {
            try
            {
                // Cleanup old sessions for this user
                var oldSessions = await _dbContext.UserSessions
                    .Where(s => s.UserId == userId && s.IsActive)
                    .ToListAsync();
                
                foreach (var session in oldSessions)
                {
                    session.IsActive = false;
                }
                
                var userSession = new UserSession
                {
                    UserId = userId,
                    SessionId = sessionId,
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    IsActive = true
                };
                
                _dbContext.UserSessions.Add(userSession);
                await _dbContext.SaveChangesAsync();
                
                return userSession;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating session for user: {UserId}", userId);
                throw;
            }
        }
        
        /// <summary>
        /// Update session activity
        /// </summary>
        public async Task UpdateSessionActivityAsync(string sessionId, string? roomId = null)
        {
            try
            {
                var session = await _dbContext.UserSessions
                    .Where(s => s.SessionId == sessionId && s.IsActive)
                    .FirstOrDefaultAsync();
                
                if (session != null)
                {
                    session.LastActivity = DateTime.UtcNow;
                    if (roomId != null)
                        session.CurrentRoomId = roomId;
                    
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating session activity: {SessionId}", sessionId);
            }
        }
        
        /// <summary>
        /// End user session
        /// </summary>
        public async Task EndUserSessionAsync(string sessionId)
        {
            try
            {
                var session = await _dbContext.UserSessions
                    .Where(s => s.SessionId == sessionId)
                    .FirstOrDefaultAsync();
                
                if (session != null)
                {
                    session.IsActive = false;
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending session: {SessionId}", sessionId);
            }
        }
        
        #endregion
        
        #region Email Verification
        
        /// <summary>
        /// Verify email using token
        /// </summary>
        public async Task<EmailVerificationResult> VerifyEmailAsync(string token)
        {
            try
            {
                var user = await _dbContext.Users
                    .Where(u => u.EmailVerificationToken == token && !u.IsEmailVerified)
                    .FirstOrDefaultAsync();
                
                if (user == null)
                    return EmailVerificationResult.CreateFailure("Invalid or expired verification token");
                
                user.IsEmailVerified = true;
                user.EmailVerificationToken = null;
                user.UpdatedAt = DateTime.UtcNow;
                
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("Email verified for user: {Username}", user.Username);
                
                return EmailVerificationResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying email with token: {Token}", token);
                return EmailVerificationResult.CreateFailure("Email verification failed due to internal error");
            }
        }
        
        #endregion
        
        #region Admin Functions
        
        /// <summary>
        /// Get all users with pagination and filtering
        /// </summary>
        public async Task<UserListResult> GetUsersAsync(int page = 1, int pageSize = 50, string? searchTerm = null)
        {
            try
            {
                var query = _dbContext.Users.AsQueryable();
                
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    query = query.Where(u => u.Username.Contains(searchTerm) || 
                                           u.Email.Contains(searchTerm) || 
                                           (u.DisplayName != null && u.DisplayName.Contains(searchTerm)));
                }
                
                var totalCount = await query.CountAsync();
                var users = await query
                    .OrderByDescending(u => u.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
                
                return new UserListResult
                {
                    Users = users,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting users list");
                throw;
            }
        }
        
        /// <summary>
        /// Ban/unban user
        /// </summary>
        public async Task<bool> SetUserBanStatusAsync(int userId, bool isBanned, string? reason = null)
        {
            try
            {
                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return false;
                
                user.IsBanned = isBanned;
                user.UpdatedAt = DateTime.UtcNow;
                
                // End all active sessions if banning
                if (isBanned)
                {
                    var activeSessions = await _dbContext.UserSessions
                        .Where(s => s.UserId == userId && s.IsActive)
                        .ToListAsync();
                    
                    foreach (var session in activeSessions)
                    {
                        session.IsActive = false;
                    }
                }
                
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("User {Username} ban status changed to: {IsBanned}. Reason: {Reason}", 
                    user.Username, isBanned, reason ?? "Not specified");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting ban status for user: {UserId}", userId);
                return false;
            }
        }

        /// <summary>
        /// Get user statistics for dashboard
        /// </summary>
        public async Task<object> GetUserStatisticsAsync()
        {
            try
            {
                var totalUsers = await _dbContext.Users.CountAsync();
                var activeUsers = await _dbContext.Users.CountAsync(u => u.IsActive && !u.IsBanned);
                var bannedUsers = await _dbContext.Users.CountAsync(u => u.IsBanned);
                var unverifiedUsers = await _dbContext.Users.CountAsync(u => !u.IsEmailVerified);
                var newUsersToday = await _dbContext.Users.CountAsync(u => u.CreatedAt.Date == DateTime.UtcNow.Date);
                var newUsersThisWeek = await _dbContext.Users.CountAsync(u => u.CreatedAt >= DateTime.UtcNow.AddDays(-7));
                var newUsersThisMonth = await _dbContext.Users.CountAsync(u => u.CreatedAt >= DateTime.UtcNow.AddDays(-30));
                var activeSessions = await _dbContext.UserSessions.CountAsync(s => s.IsActive);

                return new
                {
                    totalUsers,
                    activeUsers,
                    bannedUsers,
                    unverifiedUsers,
                    newUsersToday,
                    newUsersThisWeek,
                    newUsersThisMonth,
                    activeSessions
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user statistics");
                throw;
            }
        }

        /// <summary>
        /// Get all users with pagination for dashboard
        /// </summary>
        public async Task<object> GetAllUsersAsync(int page = 1, int pageSize = 10)
        {
            try
            {
                var result = await GetUsersAsync(page, pageSize);
                var userDtos = result.Users.Select(UserSummaryDto.FromUser).ToList();
                
                return new
                {
                    users = userDtos,
                    totalCount = result.TotalCount,
                    page = result.Page,
                    pageSize = result.PageSize,
                    totalPages = result.TotalPages
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all users");
                throw;
            }
        }

        /// <summary>
        /// Get active user sessions
        /// </summary>
        public async Task<object> GetActiveSessionsAsync()
        {
            try
            {
                var sessions = await _dbContext.UserSessions
                    .Include(s => s.User)
                    .Where(s => s.IsActive)
                    .OrderByDescending(s => s.LastActivity)
                    .Select(s => new
                    {
                        sessionId = s.SessionId,
                        userId = s.UserId,
                        username = s.User.Username,
                        displayName = s.User.DisplayName,
                        ipAddress = s.IpAddress,
                        userAgent = s.UserAgent,
                        createdAt = s.CreatedAt,
                        lastActivity = s.LastActivity,
                        currentRoomId = s.CurrentRoomId
                    })
                    .ToListAsync();

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active sessions");
                throw;
            }
        }

        /// <summary>
        /// Ban user by ID (admin function)
        /// </summary>
        public async Task<ServiceResult> BanUserAsync(string userId, string reason, int? banDurationDays = null)
        {
            try
            {
                if (!int.TryParse(userId, out int userIdInt))
                    return ServiceResult.CreateFailure("Invalid user ID");

                var user = await _dbContext.Users.FindAsync(userIdInt);
                if (user == null)
                    return ServiceResult.CreateFailure("User not found");

                user.IsBanned = true;
                user.UpdatedAt = DateTime.UtcNow;

                // End all active sessions
                var activeSessions = await _dbContext.UserSessions
                    .Where(s => s.UserId == userIdInt && s.IsActive)
                    .ToListAsync();
                
                foreach (var session in activeSessions)
                {
                    session.IsActive = false;
                }

                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("User {Username} banned by admin. Reason: {Reason}", user.Username, reason);
                
                return ServiceResult.CreateSuccess($"User '{user.Username}' has been banned");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error banning user: {UserId}", userId);
                return ServiceResult.CreateFailure("Failed to ban user");
            }
        }

        /// <summary>
        /// Unban user by ID (admin function)
        /// </summary>
        public async Task<ServiceResult> UnbanUserAsync(string userId)
        {
            try
            {
                if (!int.TryParse(userId, out int userIdInt))
                    return ServiceResult.CreateFailure("Invalid user ID");

                var user = await _dbContext.Users.FindAsync(userIdInt);
                if (user == null)
                    return ServiceResult.CreateFailure("User not found");

                user.IsBanned = false;
                user.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("User {Username} unbanned by admin", user.Username);
                
                return ServiceResult.CreateSuccess($"User '{user.Username}' has been unbanned");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unbanning user: {UserId}", userId);
                return ServiceResult.CreateFailure("Failed to unban user");
            }
        }

        /// <summary>
        /// Admin initiated password reset
        /// </summary>
        public async Task<ServiceResult> AdminInitiatePasswordResetAsync(string userId)
        {
            try
            {
                if (!int.TryParse(userId, out int userIdInt))
                    return ServiceResult.CreateFailure("Invalid user ID");

                var user = await _dbContext.Users.FindAsync(userIdInt);
                if (user == null)
                    return ServiceResult.CreateFailure("User not found");

                var resetResult = await InitiatePasswordResetAsync(user.Email);
                
                if (resetResult)
                {
                    _logger.LogInformation("Password reset initiated by admin for user: {Username}", user.Username);
                    return ServiceResult.CreateSuccess($"Password reset email sent to {user.Email}");
                }
                else
                {
                    return ServiceResult.CreateFailure("Failed to initiate password reset");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating admin password reset for user: {UserId}", userId);
                return ServiceResult.CreateFailure("Failed to initiate password reset");
            }
        }

        /// <summary>
        /// Delete user account (admin function)
        /// </summary>
        public async Task<ServiceResult> DeleteUserAsync(string userId)
        {
            try
            {
                if (!int.TryParse(userId, out int userIdInt))
                    return ServiceResult.CreateFailure("Invalid user ID");

                var user = await _dbContext.Users.FindAsync(userIdInt);
                if (user == null)
                    return ServiceResult.CreateFailure("User not found");

                // End all active sessions
                var activeSessions = await _dbContext.UserSessions
                    .Where(s => s.UserId == userIdInt)
                    .ToListAsync();
                
                _dbContext.UserSessions.RemoveRange(activeSessions);

                // Remove password reset requests
                var resetRequests = await _dbContext.PasswordResetRequests
                    .Where(r => r.UserId == userIdInt)
                    .ToListAsync();
                
                _dbContext.PasswordResetRequests.RemoveRange(resetRequests);

                // Remove audit logs
                var auditLogs = await _dbContext.LoginAuditLogs
                    .Where(l => l.UserId == userIdInt)
                    .ToListAsync();
                
                _dbContext.LoginAuditLogs.RemoveRange(auditLogs);

                // Remove user
                _dbContext.Users.Remove(user);

                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("User {Username} deleted by admin", user.Username);
                
                return ServiceResult.CreateSuccess($"User '{user.Username}' has been deleted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user: {UserId}", userId);
                return ServiceResult.CreateFailure("Failed to delete user");
            }
        }

        /// <summary>
        /// Get user audit log
        /// </summary>
        public async Task<object> GetUserAuditLogAsync(string userId, int limit = 50)
        {
            try
            {
                if (!int.TryParse(userId, out int userIdInt))
                    return new { error = "Invalid user ID" };

                var auditLogs = await _dbContext.LoginAuditLogs
                    .Where(l => l.UserId == userIdInt)
                    .OrderByDescending(l => l.Timestamp)
                    .Take(limit)
                    .Select(l => new
                    {
                        id = l.Id,
                        timestamp = l.Timestamp,
                        ipAddress = l.IpAddress,
                        userAgent = l.UserAgent,
                        success = l.Success,
                        failureReason = l.FailureReason
                    })
                    .ToListAsync();

                return auditLogs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user audit log: {UserId}", userId);
                return new { error = "Failed to get audit log" };
            }
        }
        
        #endregion
        
        #region Private Helper Methods
        
        private static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(12));
        }
        
        private static bool VerifyPassword(string password, string hash)
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch
            {
                return false;
            }
        }
        
        private static string GenerateSecureToken()
        {
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }
        
        private static bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
        
        #endregion
    }
}
