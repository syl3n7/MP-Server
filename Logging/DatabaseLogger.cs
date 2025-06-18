using System;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MP.Server.Services;

namespace MP.Server.Logging
{
    /// <summary>
    /// Custom logger that saves important logs to the database
    /// </summary>
    public class DatabaseLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly DatabaseLoggingService _dbLoggingService;
        private readonly IServiceProvider _serviceProvider;

        public DatabaseLogger(string categoryName, DatabaseLoggingService dbLoggingService, IServiceProvider serviceProvider)
        {
            _categoryName = categoryName;
            _dbLoggingService = dbLoggingService;
            _serviceProvider = serviceProvider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var level = GetLogLevelString(logLevel);

            // Extract context information if available
            string? sessionId = null;
            string? ipAddress = null;
            string? playerName = null;
            string? roomId = null;

            // Try to extract context from the message or state
            if (state is IEnumerable<KeyValuePair<string, object?>> props)
            {
                foreach (var prop in props)
                {
                    switch (prop.Key.ToLower())
                    {
                        case "sessionid":
                            sessionId = prop.Value?.ToString();
                            break;
                        case "ipaddress":
                        case "clientip":
                            ipAddress = prop.Value?.ToString();
                            break;
                        case "playername":
                            playerName = prop.Value?.ToString();
                            break;
                        case "roomid":
                            roomId = prop.Value?.ToString();
                            break;
                    }
                }
            }

            // Save to database asynchronously (fire and forget for performance)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dbLoggingService.LogServerEventAsync(
                        level: level,
                        category: _categoryName,
                        message: message,
                        sessionId: sessionId,
                        ipAddress: ipAddress,
                        playerName: playerName,
                        roomId: roomId,
                        exception: exception
                    );
                }
                catch
                {
                    // Ignore database logging errors to prevent cascading failures
                }
            });
        }

        private static string GetLogLevelString(LogLevel logLevel) => logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Info",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => "Unknown"
        };

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Logger provider for the database logger
    /// </summary>
    public class DatabaseLoggerProvider : ILoggerProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly DatabaseLoggingService _dbLoggingService;
        private readonly ConcurrentDictionary<string, DatabaseLogger> _loggers = new();

        public DatabaseLoggerProvider(IServiceProvider serviceProvider, DatabaseLoggingService dbLoggingService)
        {
            _serviceProvider = serviceProvider;
            _dbLoggingService = dbLoggingService;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new DatabaseLogger(name, _dbLoggingService, _serviceProvider));
        }

        public void Dispose()
        {
            _loggers.Clear();
        }
    }

    /// <summary>
    /// Extension methods for configuring database logging
    /// </summary>
    public static class DatabaseLoggerExtensions
    {
        public static ILoggingBuilder AddDatabaseLogging(this ILoggingBuilder builder, IServiceProvider serviceProvider)
        {
            var dbLoggingService = serviceProvider.GetRequiredService<DatabaseLoggingService>();
            builder.AddProvider(new DatabaseLoggerProvider(serviceProvider, dbLoggingService));
            return builder;
        }
    }
}
