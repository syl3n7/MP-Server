using System;
using System.Collections.Generic;
using MP.Server.Models;

namespace MP.Server.Services
{
    #region Authentication Results
    
    public class UserRegistrationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int? UserId { get; set; }
        
        public static UserRegistrationResult CreateSuccess(int userId) => new() { Success = true, UserId = userId };
        public static UserRegistrationResult CreateFailure(string error) => new() { Success = false, ErrorMessage = error };
    }
    
    public class UserAuthenticationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public User? User { get; set; }
        
        public static UserAuthenticationResult CreateSuccess(User user) => new() { Success = true, User = user };
        public static UserAuthenticationResult CreateFailure(string error) => new() { Success = false, ErrorMessage = error };
    }
    
    #endregion
    
    #region Password Management Results
    
    public class PasswordResetResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        
        public static PasswordResetResult CreateSuccess() => new() { Success = true };
        public static PasswordResetResult CreateFailure(string error) => new() { Success = false, ErrorMessage = error };
    }
    
    public class PasswordChangeResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        
        public static PasswordChangeResult CreateSuccess() => new() { Success = true };
        public static PasswordChangeResult CreateFailure(string error) => new() { Success = false, ErrorMessage = error };
    }
    
    #endregion
    
    #region Profile Management Results
    
    public class ProfileUpdateResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        
        public static ProfileUpdateResult CreateSuccess() => new() { Success = true };
        public static ProfileUpdateResult CreateFailure(string error) => new() { Success = false, ErrorMessage = error };
    }
    
    public class EmailVerificationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        
        public static EmailVerificationResult CreateSuccess() => new() { Success = true };
        public static EmailVerificationResult CreateFailure(string error) => new() { Success = false, ErrorMessage = error };
    }
    
    #endregion
    
    #region DTOs and Request Objects
    
    public class ProfileUpdateRequest
    {
        public string? DisplayName { get; set; }
        public string? Bio { get; set; }
        public string? Country { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? AvatarUrl { get; set; }
    }
    
    public class UserRegistrationRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
    
    public class UserLoginRequest
    {
        public string UsernameOrEmail { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
    
    public class PasswordResetRequest
    {
        public string Email { get; set; } = string.Empty;
    }
    
    public class PasswordResetConfirmRequest
    {
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
    
    public class PasswordChangeRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
    
    public class UserListResult
    {
        public List<User> Users { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
    
    public class UserProfileDto
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Bio { get; set; }
        public string? Country { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? AvatarUrl { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastLoginAt { get; set; }
        public bool IsEmailVerified { get; set; }
        public bool IsActive { get; set; }
        public bool IsBanned { get; set; }
        
        // Gaming Statistics
        public int TotalRaces { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public float BestLapTime { get; set; }
        public int TotalPlayTime { get; set; }
        public float WinRate { get; set; }
        
        public static UserProfileDto FromUser(User user)
        {
            return new UserProfileDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                DisplayName = user.DisplayName,
                Bio = user.Bio,
                Country = user.Country,
                DateOfBirth = user.DateOfBirth,
                AvatarUrl = user.AvatarUrl,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsEmailVerified = user.IsEmailVerified,
                IsActive = user.IsActive,
                IsBanned = user.IsBanned,
                TotalRaces = user.TotalRaces,
                Wins = user.Wins,
                Losses = user.Losses,
                BestLapTime = user.BestLapTime,
                TotalPlayTime = user.TotalPlayTime,
                WinRate = user.WinRate
            };
        }
    }
    
    public class UserSummaryDto
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastLoginAt { get; set; }
        public bool IsActive { get; set; }
        public bool IsBanned { get; set; }
        public bool IsEmailVerified { get; set; }
        public int TotalRaces { get; set; }
        public float WinRate { get; set; }
        
        public static UserSummaryDto FromUser(User user)
        {
            return new UserSummaryDto
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsActive = user.IsActive,
                IsBanned = user.IsBanned,
                IsEmailVerified = user.IsEmailVerified,
                TotalRaces = user.TotalRaces,
                WinRate = user.WinRate
            };
        }
    }

    public class ServiceResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        
        public static ServiceResult CreateSuccess(string message) => new() { Success = true, Message = message };
        public static ServiceResult CreateFailure(string message) => new() { Success = false, Message = message };
    }
    
    #endregion

    #region User Management Requests

    public class BanUserRequest
    {
        public int UserId { get; set; }
        public bool IsBanned { get; set; }
        public string? Reason { get; set; }
        public int? BanDurationDays { get; set; }
    }
    
    public class UnbanUserRequest
    {
        public int UserId { get; set; }
    }
    
    public class ForcePasswordResetRequest
    {
        public int UserId { get; set; }
    }
    
    public class DeleteUserRequest
    {
        public int UserId { get; set; }
    }

    #endregion
}
