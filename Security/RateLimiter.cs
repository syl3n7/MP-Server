using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MP.Server.Security
{
    /// <summary>
    /// Rate limiter to prevent spam attacks and excessive message sending
    /// Uses sliding window approach for accurate rate limiting
    /// </summary>
    public class RateLimiter
    {
        private readonly ConcurrentDictionary<string, ClientRateInfo> _clientRates = new();
        private readonly ILogger? _logger;
        private readonly Timer _cleanupTimer;
        
        // Rate limiting constants
        public static class Limits
        {
            public const int TCP_MESSAGES_PER_SECOND = 10;
            public const int UDP_PACKETS_PER_SECOND = 120;  // Increased from 60 to 120 - allow up to 120 FPS
            public const int BURST_ALLOWANCE = 10;  // Increased burst allowance from 5 to 10
            public const int CLEANUP_INTERVAL_MS = 30000;  // 30 seconds
            public const int CLIENT_TIMEOUT_MS = 60000;  // 1 minute
        }
        
        public RateLimiter(ILogger? logger = null)
        {
            _logger = logger;
            
            // Setup cleanup timer to remove inactive clients
            _cleanupTimer = new Timer(CleanupInactiveClients, null, 
                TimeSpan.FromMilliseconds(Limits.CLEANUP_INTERVAL_MS),
                TimeSpan.FromMilliseconds(Limits.CLEANUP_INTERVAL_MS));
        }
        
        /// <summary>
        /// Check if a TCP message from a client should be allowed
        /// </summary>
        public bool AllowTcpMessage(string clientId)
        {
            return CheckRateLimit(clientId, MessageType.TCP);
        }
        
        /// <summary>
        /// Check if a UDP packet from a client should be allowed
        /// </summary>
        public bool AllowUdpPacket(string clientId)
        {
            return CheckRateLimit(clientId, MessageType.UDP);
        }
        
        /// <summary>
        /// Get current rate statistics for a client
        /// </summary>
        public RateStats GetClientStats(string clientId)
        {
            if (_clientRates.TryGetValue(clientId, out var info))
            {
                return new RateStats
                {
                    TcpMessagesPerSecond = info.TcpWindow.GetCurrentRate(),
                    UdpPacketsPerSecond = info.UdpWindow.GetCurrentRate(),
                    LastActivity = info.LastActivity
                };
            }
            
            return new RateStats();
        }
        
        /// <summary>
        /// Remove rate limiting data for a client (when they disconnect)
        /// </summary>
        public void RemoveClient(string clientId)
        {
            _clientRates.TryRemove(clientId, out _);
        }
        
        private bool CheckRateLimit(string clientId, MessageType messageType)
        {
            var now = DateTime.UtcNow;
            var info = _clientRates.GetOrAdd(clientId, _ => new ClientRateInfo());
            
            info.LastActivity = now;
            
            SlidingWindow window;
            int limit;
            string typeDesc;
            
            if (messageType == MessageType.TCP)
            {
                window = info.TcpWindow;
                limit = Limits.TCP_MESSAGES_PER_SECOND;
                typeDesc = "TCP";
            }
            else
            {
                window = info.UdpWindow;
                limit = Limits.UDP_PACKETS_PER_SECOND;
                typeDesc = "UDP";
            }
            
            // Add this request to the sliding window
            window.AddRequest(now);
            
            var currentRate = window.GetCurrentRate();
            var allowed = currentRate <= limit + Limits.BURST_ALLOWANCE;
            
            if (!allowed)
            {
                _logger?.LogWarning("Rate limit exceeded for client {ClientId}: {Rate} {Type} messages/sec (limit: {Limit})",
                    clientId, currentRate, typeDesc, limit);
            }
            
            return allowed;
        }
        
        private void CleanupInactiveClients(object? state)
        {
            var cutoff = DateTime.UtcNow.AddMilliseconds(-Limits.CLIENT_TIMEOUT_MS);
            var toRemove = new List<string>();
            
            foreach (var kvp in _clientRates)
            {
                if (kvp.Value.LastActivity < cutoff)
                {
                    toRemove.Add(kvp.Key);
                }
            }
            
            foreach (var clientId in toRemove)
            {
                _clientRates.TryRemove(clientId, out _);
            }
            
            if (toRemove.Count > 0)
            {
                _logger?.LogDebug("Cleaned up {Count} inactive rate limit entries", toRemove.Count);
            }
        }
        
        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
        
        private enum MessageType
        {
            TCP,
            UDP
        }
    }
    
    /// <summary>
    /// Stores rate limiting information for a single client
    /// </summary>
    internal class ClientRateInfo
    {
        public SlidingWindow TcpWindow { get; } = new();
        public SlidingWindow UdpWindow { get; } = new();
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Sliding window implementation for accurate rate limiting
    /// Tracks requests within a time window
    /// </summary>
    internal class SlidingWindow
    {
        private readonly Queue<DateTime> _requests = new();
        private readonly object _lock = new();
        private readonly TimeSpan _windowSize = TimeSpan.FromSeconds(1);
        
        public void AddRequest(DateTime timestamp)
        {
            lock (_lock)
            {
                _requests.Enqueue(timestamp);
                CleanOldRequests(timestamp);
            }
        }
        
        public int GetCurrentRate()
        {
            lock (_lock)
            {
                CleanOldRequests(DateTime.UtcNow);
                return _requests.Count;
            }
        }
        
        private void CleanOldRequests(DateTime now)
        {
            var cutoff = now - _windowSize;
            
            while (_requests.Count > 0 && _requests.Peek() < cutoff)
            {
                _requests.Dequeue();
            }
        }
    }
    
    /// <summary>
    /// Rate statistics for a client
    /// </summary>
    public class RateStats
    {
        public int TcpMessagesPerSecond { get; set; }
        public int UdpPacketsPerSecond { get; set; }
        public DateTime LastActivity { get; set; }
    }
}
