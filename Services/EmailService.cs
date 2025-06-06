using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MP.Server.Services
{
    /// <summary>
    /// Email service for sending verification, password reset, and notification emails
    /// </summary>
    public class EmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;
        private readonly string _smtpHost;
        private readonly int _smtpPort;
        private readonly string _smtpUsername;
        private readonly string _smtpPassword;
        private readonly string _fromEmail;
        private readonly string _fromName;
        private readonly bool _enableSsl;
        
        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            
            // Load email configuration from appsettings.json or environment variables
            _smtpHost = _configuration["Email:SmtpHost"] ?? "localhost";
            _smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
            _smtpUsername = _configuration["Email:Username"] ?? "";
            _smtpPassword = _configuration["Email:Password"] ?? "";
            _fromEmail = _configuration["Email:FromEmail"] ?? "noreply@mp-server.local";
            _fromName = _configuration["Email:FromName"] ?? "MP-Server";
            _enableSsl = bool.Parse(_configuration["Email:EnableSsl"] ?? "true");
        }
        
        /// <summary>
        /// Send email verification message
        /// </summary>
        public async Task<bool> SendEmailVerificationAsync(string toEmail, string verificationToken)
        {
            try
            {
                var serverUrl = _configuration["ServerUrl"] ?? "http://localhost:8080";
                var verificationUrl = $"{serverUrl}/verify-email?token={verificationToken}";
                
                var subject = "Verify Your MP-Server Account";
                var body = $@"
                    <html>
                    <body>
                        <h2>Welcome to MP-Server!</h2>
                        <p>Thank you for registering an account. To complete your registration, please verify your email address by clicking the link below:</p>
                        <p><a href='{verificationUrl}' style='background-color: #007cba; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>Verify Email Address</a></p>
                        <p>If the button doesn't work, you can copy and paste this URL into your browser:</p>
                        <p>{verificationUrl}</p>
                        <p>This verification link will expire in 48 hours.</p>
                        <hr>
                        <p><small>If you didn't create an account with MP-Server, please ignore this email.</small></p>
                    </body>
                    </html>";
                
                return await SendEmailAsync(toEmail, subject, body, isHtml: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email verification to {Email}", toEmail);
                return false;
            }
        }
        
        /// <summary>
        /// Send password reset email
        /// </summary>
        public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string username, string resetToken)
        {
            try
            {
                var serverUrl = _configuration["ServerUrl"] ?? "http://localhost:8080";
                var resetUrl = $"{serverUrl}/reset-password?token={resetToken}";
                
                var subject = "Reset Your MP-Server Password";
                var body = $@"
                    <html>
                    <body>
                        <h2>Password Reset Request</h2>
                        <p>Hello {username},</p>
                        <p>We received a request to reset your password for your MP-Server account. If you made this request, click the link below to reset your password:</p>
                        <p><a href='{resetUrl}' style='background-color: #dc3545; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>Reset Password</a></p>
                        <p>If the button doesn't work, you can copy and paste this URL into your browser:</p>
                        <p>{resetUrl}</p>
                        <p>This password reset link will expire in 24 hours.</p>
                        <hr>
                        <p><small>If you didn't request a password reset, please ignore this email. Your password will remain unchanged.</small></p>
                    </body>
                    </html>";
                
                return await SendEmailAsync(toEmail, subject, body, isHtml: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send password reset email to {Email}", toEmail);
                return false;
            }
        }
        
        /// <summary>
        /// Send account security notification
        /// </summary>
        public async Task<bool> SendSecurityNotificationAsync(string toEmail, string username, string action, string ipAddress)
        {
            try
            {
                var subject = "MP-Server Security Alert";
                var body = $@"
                    <html>
                    <body>
                        <h2>Security Alert</h2>
                        <p>Hello {username},</p>
                        <p>We're writing to inform you about recent security activity on your MP-Server account:</p>
                        <ul>
                            <li><strong>Action:</strong> {action}</li>
                            <li><strong>Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</li>
                            <li><strong>IP Address:</strong> {ipAddress}</li>
                        </ul>
                        <p>If this was you, no further action is required. If you don't recognize this activity, please change your password immediately.</p>
                        <hr>
                        <p><small>This is an automated security notification from MP-Server.</small></p>
                    </body>
                    </html>";
                
                return await SendEmailAsync(toEmail, subject, body, isHtml: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send security notification to {Email}", toEmail);
                return false;
            }
        }
        
        /// <summary>
        /// Generic email sending method
        /// </summary>
        private async Task<bool> SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false)
        {
            try
            {
                // If SMTP is not configured, log the email instead of sending
                if (string.IsNullOrEmpty(_smtpHost) || _smtpHost == "localhost")
                {
                    _logger.LogInformation("SMTP not configured. Would send email to {Email} with subject: {Subject}", toEmail, subject);
                    if (isHtml)
                        _logger.LogDebug("Email body (HTML): {Body}", body);
                    else
                        _logger.LogDebug("Email body: {Body}", body);
                    return true;
                }
                
                using var client = new SmtpClient(_smtpHost, _smtpPort);
                client.EnableSsl = _enableSsl;
                
                if (!string.IsNullOrEmpty(_smtpUsername))
                {
                    client.Credentials = new NetworkCredential(_smtpUsername, _smtpPassword);
                }
                
                using var message = new MailMessage();
                message.From = new MailAddress(_fromEmail, _fromName);
                message.To.Add(toEmail);
                message.Subject = subject;
                message.Body = body;
                message.IsBodyHtml = isHtml;
                
                await client.SendMailAsync(message);
                
                _logger.LogInformation("Email sent successfully to {Email} with subject: {Subject}", toEmail, subject);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Email} with subject: {Subject}", toEmail, subject);
                return false;
            }
        }
    }
}
