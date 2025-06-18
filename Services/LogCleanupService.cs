using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MP.Server.Services
{
    /// <summary>
    /// Background service that periodically cleans up old logs
    /// </summary>
    public class LogCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<LogCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24); // Run daily
        private readonly int _retentionDays = 30; // Keep logs for 30 days

        public LogCleanupService(IServiceProvider serviceProvider, ILogger<LogCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Log cleanup service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PerformCleanupAsync();
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during log cleanup");
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken); // Retry in 1 hour
                }
            }

            _logger.LogInformation("Log cleanup service stopped");
        }

        private async Task PerformCleanupAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var loggingService = scope.ServiceProvider.GetService<DatabaseLoggingService>();
                
                if (loggingService != null)
                {
                    _logger.LogInformation("Starting automatic log cleanup for logs older than {RetentionDays} days", _retentionDays);
                    await loggingService.CleanupOldLogsAsync(_retentionDays);
                    _logger.LogInformation("Automatic log cleanup completed successfully");
                }
                else
                {
                    _logger.LogWarning("DatabaseLoggingService not available for log cleanup");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform automatic log cleanup");
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Log cleanup service is stopping");
            await base.StopAsync(stoppingToken);
        }
    }
}
