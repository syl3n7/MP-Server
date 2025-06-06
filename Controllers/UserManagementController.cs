using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MP.Server.Services;
using System.Threading.Tasks;

namespace MP.Server.Controllers
{
    /// <summary>
    /// User management API controller for web dashboard
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class UserManagementController : ControllerBase
    {
        private readonly UserManagementService _userService;
        private readonly ILogger<UserManagementController> _logger;
        
        public UserManagementController(UserManagementService userService, ILogger<UserManagementController> logger)
        {
            _userService = userService;
            _logger = logger;
        }
        
        #region User Registration and Authentication
        
        /// <summary>
        /// Register a new user
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] UserRegistrationRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            if (request.Password != request.ConfirmPassword)
                return BadRequest("Passwords do not match");
            
            var result = await _userService.RegisterUserAsync(request.Username, request.Email, request.Password);
            
            if (result.Success)
                return Ok(new { message = "User registered successfully. Please check your email for verification." });
            else
                return BadRequest(new { message = result.ErrorMessage });
        }
        
        /// <summary>
        /// Authenticate user
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] UserLoginRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            
            var result = await _userService.AuthenticateUserAsync(request.UsernameOrEmail, request.Password, ipAddress, userAgent);
            
            if (result.Success)
            {
                var userDto = UserProfileDto.FromUser(result.User!);
                return Ok(new { message = "Login successful", user = userDto });
            }
            else
                return Unauthorized(new { message = result.ErrorMessage });
        }
        
        #endregion
        
        #region Password Management
        
        /// <summary>
        /// Initiate password reset
        /// </summary>
        [HttpPost("password-reset")]
        public async Task<IActionResult> RequestPasswordReset([FromBody] PasswordResetRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _userService.InitiatePasswordResetAsync(request.Email, ipAddress);
            
            // Always return success to prevent email enumeration
            return Ok(new { message = "If an account with that email exists, a password reset link has been sent." });
        }
        
        /// <summary>
        /// Confirm password reset
        /// </summary>
        [HttpPost("password-reset/confirm")]
        public async Task<IActionResult> ConfirmPasswordReset([FromBody] PasswordResetConfirmRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            if (request.NewPassword != request.ConfirmPassword)
                return BadRequest("Passwords do not match");
            
            var result = await _userService.ResetPasswordAsync(request.Token, request.NewPassword);
            
            if (result.Success)
                return Ok(new { message = "Password reset successfully" });
            else
                return BadRequest(new { message = result.ErrorMessage });
        }
        
        /// <summary>
        /// Change password (authenticated user)
        /// </summary>
        [HttpPost("password-change")]
        public async Task<IActionResult> ChangePassword([FromBody] PasswordChangeRequest request, [FromQuery] int userId)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            if (request.NewPassword != request.ConfirmPassword)
                return BadRequest("Passwords do not match");
            
            var result = await _userService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
            
            if (result.Success)
                return Ok(new { message = "Password changed successfully" });
            else
                return BadRequest(new { message = result.ErrorMessage });
        }
        
        #endregion
        
        #region Email Verification
        
        /// <summary>
        /// Verify email address
        /// </summary>
        [HttpGet("verify-email")]
        public async Task<IActionResult> VerifyEmail([FromQuery] string token)
        {
            if (string.IsNullOrEmpty(token))
                return BadRequest("Invalid verification token");
            
            var result = await _userService.VerifyEmailAsync(token);
            
            if (result.Success)
                return Ok(new { message = "Email verified successfully" });
            else
                return BadRequest(new { message = result.ErrorMessage });
        }
        
        #endregion
        
        #region Profile Management
        
        /// <summary>
        /// Get user profile
        /// </summary>
        [HttpGet("profile/{userId}")]
        public async Task<IActionResult> GetProfile(int userId)
        {
            var user = await _userService.GetUserProfileAsync(userId);
            
            if (user == null)
                return NotFound("User not found");
            
            var userDto = UserProfileDto.FromUser(user);
            return Ok(userDto);
        }
        
        /// <summary>
        /// Update user profile
        /// </summary>
        [HttpPut("profile/{userId}")]
        public async Task<IActionResult> UpdateProfile(int userId, [FromBody] ProfileUpdateRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            var result = await _userService.UpdateUserProfileAsync(userId, request);
            
            if (result.Success)
                return Ok(new { message = "Profile updated successfully" });
            else
                return BadRequest(new { message = result.ErrorMessage });
        }
        
        #endregion
        
        #region Admin Functions
        
        /// <summary>
        /// Get all users (admin only)
        /// </summary>
        [HttpGet("admin/users")]
        public async Task<IActionResult> GetAllUsers(
            [FromQuery] int page = 1, 
            [FromQuery] int pageSize = 50, 
            [FromQuery] string? search = null)
        {
            try
            {
                var result = await _userService.GetUsersAsync(page, pageSize, search);
                var userDtos = result.Users.Select(UserSummaryDto.FromUser).ToList();
                
                return Ok(new
                {
                    users = userDtos,
                    totalCount = result.TotalCount,
                    page = result.Page,
                    pageSize = result.PageSize,
                    totalPages = result.TotalPages
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting users list");
                return StatusCode(500, "Internal server error");
            }
        }
        
        /// <summary>
        /// Ban/unban user (admin only)
        /// </summary>
        [HttpPost("admin/users/{userId}/ban")]
        public async Task<IActionResult> BanUser(int userId, [FromBody] BanUserRequest request)
        {
            var success = await _userService.SetUserBanStatusAsync(userId, request.IsBanned, request.Reason);
            
            if (success)
                return Ok(new { message = request.IsBanned ? "User banned successfully" : "User unbanned successfully" });
            else
                return BadRequest("Failed to update user ban status");
        }
        
        /// <summary>
        /// Get user statistics (admin only)
        /// </summary>
        [HttpGet("admin/statistics")]
        public async Task<IActionResult> GetUserStatistics()
        {
            try
            {
                // This would typically be implemented with more complex queries
                var result = await _userService.GetUsersAsync(1, int.MaxValue);
                
                var stats = new
                {
                    totalUsers = result.TotalCount,
                    activeUsers = result.Users.Count(u => u.IsActive && !u.IsBanned),
                    bannedUsers = result.Users.Count(u => u.IsBanned),
                    unverifiedUsers = result.Users.Count(u => !u.IsEmailVerified),
                    newUsersToday = result.Users.Count(u => u.CreatedAt.Date == DateTime.UtcNow.Date),
                    newUsersThisWeek = result.Users.Count(u => u.CreatedAt >= DateTime.UtcNow.AddDays(-7)),
                    newUsersThisMonth = result.Users.Count(u => u.CreatedAt >= DateTime.UtcNow.AddDays(-30))
                };
                
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user statistics");
                return StatusCode(500, "Internal server error");
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Request model for banning users
    /// </summary>
    public class BanUserRequest
    {
        public bool IsBanned { get; set; }
        public string? Reason { get; set; }
    }
}
