using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MP.Server.Security
{
    /// <summary>
    /// Comprehensive packet validation system to prevent cheating and enforce game rules
    /// </summary>
    public class PacketValidator
    {
        private readonly ILogger<PacketValidator> _logger;
        
        // Physics validation constants
        private const float MAX_POSITION_JUMP = 50.0f; // Maximum position change per frame
        private const float MAX_SPEED = 200.0f; // Maximum speed in units/second
        private const float MAX_ANGULAR_VELOCITY = 10.0f; // Maximum rotation change
        private const float MIN_UPDATE_INTERVAL = 0.008f; // Minimum 8ms between updates (125 FPS) - more lenient
        private const float MAX_UPDATE_INTERVAL = 5.0f; // Maximum 5 seconds between updates
        
        // Input validation ranges
        private const float MAX_STEERING = 1.0f;
        private const float MAX_THROTTLE = 1.0f;
        private const float MAX_BRAKE = 1.0f;
        
        // Player state tracking for validation
        private readonly ConcurrentDictionary<string, PlayerValidationState> _playerStates = new();
        
        public PacketValidator(ILogger<PacketValidator> logger)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// Validates a position update packet for physics consistency
        /// </summary>
        public ValidationResult ValidatePositionUpdate(string sessionId, JsonElement packet)
        {
            try
            {
                // Extract position data
                if (!packet.TryGetProperty("position", out var posElement) ||
                    !packet.TryGetProperty("rotation", out var rotElement))
                {
                    return ValidationResult.Reject("Missing position or rotation data");
                }
                
                var position = ParseVector3(posElement);
                var rotation = ParseQuaternion(rotElement);
                var timestamp = DateTime.UtcNow;
                
                // Get or create player validation state
                var state = _playerStates.GetOrAdd(sessionId, _ => new PlayerValidationState
                {
                    LastPosition = position,
                    LastRotation = rotation,
                    LastUpdateTime = timestamp
                });
                
                // Validate timestamp intervals
                var timeDelta = (timestamp - state.LastUpdateTime).TotalSeconds;
                if (timeDelta < MIN_UPDATE_INTERVAL)
                {
                    _logger.LogDebug("⏱️ Player {SessionId} sending updates frequently: {Interval}ms (within acceptable range)", 
                        sessionId, timeDelta * 1000);
                    // Allow rapid updates but update the state
                    state.Update(position, rotation, timestamp);
                    return ValidationResult.Accept();
                }
                
                if (timeDelta > MAX_UPDATE_INTERVAL)
                {
                    _logger.LogWarning("⚠️ Player {SessionId} long gap between updates: {Interval}s", 
                        sessionId, timeDelta);
                    // Allow but reset state
                    state.Reset(position, rotation, timestamp);
                    return ValidationResult.Accept();
                }
                
                // Validate position changes (teleport detection)
                var positionDelta = Vector3.Distance(position, state.LastPosition);
                var maxAllowedDistance = MAX_SPEED * (float)timeDelta;
                
                // Be more lenient for the first few updates (spawn/initial positioning)
                var isInitialUpdate = timeDelta > 1.0f; // If more than 1 second gap, consider it initial
                var effectiveMaxJump = isInitialUpdate ? MAX_POSITION_JUMP * 3 : MAX_POSITION_JUMP;
                
                if (positionDelta > Math.Max(maxAllowedDistance, effectiveMaxJump))
                {
                    _logger.LogWarning("🚫 Player {SessionId} suspicious position jump: {Distance} units in {Time}s", 
                        sessionId, positionDelta, timeDelta);
                    
                    // For initial spawns, just warn but allow
                    if (isInitialUpdate)
                    {
                        _logger.LogInformation("🏁 Player {SessionId} initial spawn/teleport allowed: {Distance} units", 
                            sessionId, positionDelta);
                        state.Update(position, rotation, timestamp);
                        return ValidationResult.Accept();
                    }
                    
                    return ValidationResult.Reject("Impossible position change detected");
                }
                
                // Validate rotation changes
                var rotationDelta = CalculateRotationDelta(rotation, state.LastRotation);
                var maxAllowedRotation = MAX_ANGULAR_VELOCITY * (float)timeDelta;
                
                if (rotationDelta > maxAllowedRotation)
                {
                    _logger.LogWarning("🚫 Player {SessionId} suspicious rotation change: {Delta} rad in {Time}s", 
                        sessionId, rotationDelta, timeDelta);
                    return ValidationResult.Reject("Impossible rotation change detected");
                }
                
                // Validate position bounds (basic map boundaries)
                if (!IsValidPosition(position))
                {
                    _logger.LogWarning("🚫 Player {SessionId} position outside valid bounds: {Position}", 
                        sessionId, position);
                    return ValidationResult.Reject("Position outside valid game area");
                }
                
                // Update state for next validation
                state.Update(position, rotation, timestamp);
                
                return ValidationResult.Accept();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error validating position update for {SessionId}", sessionId);
                return ValidationResult.Reject("Validation error");
            }
        }
        
        /// <summary>
        /// Validates input commands for legitimate ranges
        /// </summary>
        public ValidationResult ValidateInputCommand(string sessionId, JsonElement packet)
        {
            try
            {
                // Input data is optional - if missing, just accept the packet
                if (!packet.TryGetProperty("input", out var inputElement))
                {
                    _logger.LogDebug("📦 Input packet without input data from {SessionId} - accepting", sessionId);
                    return ValidationResult.Accept();
                }
                
                // Validate steering range
                if (inputElement.TryGetProperty("steering", out var steeringEl))
                {
                    var steering = steeringEl.GetSingle();
                    if (Math.Abs(steering) > MAX_STEERING)
                    {
                        _logger.LogWarning("🚫 Player {SessionId} invalid steering value: {Value}", 
                            sessionId, steering);
                        return ValidationResult.Reject("Invalid steering input");
                    }
                }
                
                // Validate throttle range
                if (inputElement.TryGetProperty("throttle", out var throttleEl))
                {
                    var throttle = throttleEl.GetSingle();
                    if (throttle < 0 || throttle > MAX_THROTTLE)
                    {
                        _logger.LogWarning("🚫 Player {SessionId} invalid throttle value: {Value}", 
                            sessionId, throttle);
                        return ValidationResult.Reject("Invalid throttle input");
                    }
                }
                
                // Validate brake range
                if (inputElement.TryGetProperty("brake", out var brakeEl))
                {
                    var brake = brakeEl.GetSingle();
                    if (brake < 0 || brake > MAX_BRAKE)
                    {
                        _logger.LogWarning("🚫 Player {SessionId} invalid brake value: {Value}", 
                            sessionId, brake);
                        return ValidationResult.Reject("Invalid brake input");
                    }
                }
                
                // Validate timestamp if present
                if (inputElement.TryGetProperty("timestamp", out var timestampEl))
                {
                    var timestamp = timestampEl.GetSingle();
                    var currentTime = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
                    var timeDiff = Math.Abs(currentTime - timestamp);
                    
                    // Allow some clock skew but reject obviously fake timestamps
                    if (timeDiff > 60.0f) // 60 second tolerance
                    {
                        _logger.LogWarning("🚫 Player {SessionId} suspicious timestamp: {Timestamp} vs {Current}", 
                            sessionId, timestamp, currentTime);
                        return ValidationResult.Reject("Invalid timestamp");
                    }
                }
                
                return ValidationResult.Accept();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error validating input command for {SessionId}", sessionId);
                return ValidationResult.Reject("Validation error");
            }
        }
        
        /// <summary>
        /// Validates general packet structure and required fields
        /// </summary>
        public ValidationResult ValidatePacketStructure(JsonElement packet, string expectedCommand)
        {
            try
            {
                // Check command field
                if (!packet.TryGetProperty("command", out var commandEl))
                {
                    return ValidationResult.Reject("Missing command field");
                }
                
                var command = commandEl.GetString();
                if (string.IsNullOrEmpty(command) || command != expectedCommand)
                {
                    return ValidationResult.Reject($"Invalid command: expected {expectedCommand}, got {command}");
                }
                
                // Check session ID
                if (!packet.TryGetProperty("sessionId", out var sessionEl))
                {
                    return ValidationResult.Reject("Missing sessionId field");
                }
                
                var sessionId = sessionEl.GetString();
                if (string.IsNullOrEmpty(sessionId))
                {
                    return ValidationResult.Reject("Empty sessionId");
                }
                
                return ValidationResult.Accept();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error validating packet structure");
                return ValidationResult.Reject("Packet structure validation error");
            }
        }
        
        /// <summary>
        /// Removes validation state for a disconnected player
        /// </summary>
        public void RemovePlayerState(string sessionId)
        {
            _playerStates.TryRemove(sessionId, out _);
            _logger.LogDebug("🧹 Removed validation state for player {SessionId}", sessionId);
        }
        
        #region Helper Methods
        
        private Vector3 ParseVector3(JsonElement element)
        {
            var x = element.TryGetProperty("x", out var xEl) ? xEl.GetSingle() : 0f;
            var y = element.TryGetProperty("y", out var yEl) ? yEl.GetSingle() : 0f;
            var z = element.TryGetProperty("z", out var zEl) ? zEl.GetSingle() : 0f;
            return new Vector3(x, y, z);
        }
        
        private Quaternion ParseQuaternion(JsonElement element)
        {
            var x = element.TryGetProperty("x", out var xEl) ? xEl.GetSingle() : 0f;
            var y = element.TryGetProperty("y", out var yEl) ? yEl.GetSingle() : 0f;
            var z = element.TryGetProperty("z", out var zEl) ? zEl.GetSingle() : 0f;
            var w = element.TryGetProperty("w", out var wEl) ? wEl.GetSingle() : 1f;
            return new Quaternion(x, y, z, w);
        }
        
        private float CalculateRotationDelta(Quaternion current, Quaternion previous)
        {
            // Calculate angular distance between quaternions
            var dot = Math.Abs(Quaternion.Dot(current, previous));
            dot = Math.Min(dot, 1.0f); // Clamp to avoid precision errors
            return 2.0f * (float)Math.Acos(dot);
        }
        
        private bool IsValidPosition(Vector3 position)
        {
            // Define game world boundaries (adjust as needed for your track)
            const float MAX_X = 1000f;
            const float MIN_X = -1000f;
            const float MAX_Y = 100f;
            const float MIN_Y = -100f;
            const float MAX_Z = 1000f;
            const float MIN_Z = -1000f;
            
            return position.X >= MIN_X && position.X <= MAX_X &&
                   position.Y >= MIN_Y && position.Y <= MAX_Y &&
                   position.Z >= MIN_Z && position.Z <= MAX_Z;
        }
        
        #endregion
    }
    
    /// <summary>
    /// Tracks validation state for a single player
    /// </summary>
    public class PlayerValidationState
    {
        public Vector3 LastPosition { get; set; }
        public Quaternion LastRotation { get; set; }
        public DateTime LastUpdateTime { get; set; }
        
        public void Update(Vector3 position, Quaternion rotation, DateTime timestamp)
        {
            LastPosition = position;
            LastRotation = rotation;
            LastUpdateTime = timestamp;
        }
        
        public void Reset(Vector3 position, Quaternion rotation, DateTime timestamp)
        {
            Update(position, rotation, timestamp);
        }
    }
    
    /// <summary>
    /// Validation result for packet processing
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; private set; }
        public string? Reason { get; private set; }
        
        private ValidationResult(bool isValid, string? reason = null)
        {
            IsValid = isValid;
            Reason = reason;
        }
        
        public static ValidationResult Accept() => new(true);
        public static ValidationResult Reject(string reason) => new(false, reason);
    }
}
