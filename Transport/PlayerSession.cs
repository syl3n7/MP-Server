using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Numerics;
using MP.Server;
using MP.Server.Inventory;
using MP.Server.Protocol;
using MP.Server.Security;
using MP.Server.Domain;

namespace MP.Server.Transport;

public sealed class PlayerSession : IDisposable, IPlayerSession
{
    private readonly Socket _socket;
    private readonly GameServer _server;
    private readonly CommandRouter _router;
    private readonly string _udpSharedSecret;
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly bool _useTls;
    
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public string? CurrentRoomId { get; set; }
    public string PlayerName { get; set; } = "Anonymous";
    public bool IsAuthenticated { get; set; } = false;
    public int? AuthenticatedUserId { get; private set; }
    public string? AuthenticatedUsername { get; private set; }
    
    // UDP Encryption
    public UdpEncryption? UdpCrypto { get; private set; }

    // IPlayerSession members added by protocol layer
    public string? RemoteIpAddress => (_socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address?.ToString();

    public void Authenticate(int? userId, string username)
    {
        AuthenticatedUserId   = userId;
        AuthenticatedUsername = username;
        PlayerName            = username;
        IsAuthenticated       = true;
        UdpCrypto             = new UdpEncryption(Id, _udpSharedSecret);
    }

    // Idempotency: maps messageId → receipt timestamp (ms). Prevents duplicate processing.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _recentMessageIds = new();
    private const int MessageIdWindowMs = 30_000;
    
    public PlayerSession(Socket socket, GameServer server, CommandRouter router, bool useTls = false, X509Certificate2? certificate = null, string udpSharedSecret = "change-me-in-appsettings")
    {
        _socket = socket;
        _server = server;
        _router = router;
        _udpSharedSecret = udpSharedSecret;
        _useTls = useTls;
        
        // Create network stream
        var networkStream = new NetworkStream(socket, ownsSocket: false);
        
        if (_useTls && certificate != null)
        {
            // Create SSL stream
            var sslStream = new SslStream(networkStream, false);
            
            try
            {
                // Create SSL server authentication options
                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    //Client certificates would be something to implement later on so that we can securely identify the legitimate game client and prevent abuse from generic TLS clients
                    //we could take a user created account and generate a client certificate for them to use when they are logged in the first time, and then require that certificate for all subsequent connections from that client. 
                    // For now, we will just allow any TLS client to connect, but log a warning if the handshake fails which may indicate an outdated client without TLS support.
                    ClientCertificateRequired = false, 
                    // Only allow modern TLS versions. This will prevent connections from very old clients that don't support TLS 1.2 or 1.3, which is a security risk.
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                };
                
                // Authenticate as server with timeout
                sslStream.AuthenticateAsServer(certificate, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                _stream = sslStream;
                _server.Logger.LogInformation("🔒 TLS handshake completed for session {SessionId}", Id);
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                _server.Logger.LogWarning("⚠️ TLS handshake failed for session {SessionId} - Client may be using outdated protocol: {Error}", 
                                         Id, ex.Message);
                
                // Log the client endpoint for debugging
                var clientEndpoint = socket.RemoteEndPoint?.ToString() ?? "unknown";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_server.DatabaseLoggingService != null)
                        {
                            await _server.DatabaseLoggingService.LogSecurityEventAsync(
                                "TLS_HANDSHAKE_FAILED", 
                                clientEndpoint,
                                2, // Medium severity
                                $"TLS handshake failed - client may be using outdated protocol: {ex.Message}",
                                Id
                            );
                        }
                    }
                    catch
                    {
                        // Ignore logging errors in constructor
                    }
                });
                
                sslStream.Dispose();
                _stream = networkStream; // Fallback to plain TCP
                _useTls = false; // Update flag to reflect actual state
            }
            catch (Exception ex)
            {
                _server.Logger.LogError(ex, "❌ Unexpected TLS error for session {SessionId}", Id);
                sslStream.Dispose();
                _stream = networkStream; // Fallback to plain TCP
                _useTls = false;
            }
        }
        else
        {
            _stream = networkStream;
        }
        
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);
    }

    public async Task ProcessMessagesAsync(CancellationToken ct)
    {
        try
        {
            // Send welcome message as JSON so clients can parse it the same way as every other message
            await SendJsonAsync(new { command = "CONNECTED", sessionId = Id }, ct);
            
            _server.Logger.LogInformation("👋 Welcome message sent to session {SessionId}", Id);
            
            while (!ct.IsCancellationRequested)
            {
                var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                
                while (TryParseMessage(ref buffer, out var message))
                {
                    LastActivity = DateTime.UtcNow;
                    _server.Logger.LogDebug("📨 Received message from {SessionId}: {MessageSize} bytes", 
                        Id, message.Length);
                    await ProcessMessageAsync(message, ct);
                }
                
                _reader.AdvanceTo(buffer.Start, buffer.End);
                
                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _server.Logger.LogError(ex, "❌ Error processing messages for session {SessionId}", Id);
        }
    }

    private bool TryParseMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> message)
    {
        // Simple newline-delimited protocol
        var position = buffer.PositionOf((byte)'\n');
        if (position == null)
        {
            message = default;
            return false;
        }
        
        message = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private async Task ProcessMessageAsync(ReadOnlySequence<byte> message, CancellationToken ct)
    {
        try
        {
            var messageText = Encoding.UTF8.GetString(message.ToArray());
            _server.Logger.LogDebug("🔍 Processing message from {SessionId}: '{Message}'", Id, messageText);

            var json     = JsonSerializer.Deserialize<JsonElement>(messageText);
            var envelope = MessageEnvelope.Parse(json);

            // Idempotency: drop duplicates within the 30-second window
            if (envelope.MessageId != null)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!_recentMessageIds.TryAdd(envelope.MessageId, nowMs))
                {
                    _server.Logger.LogDebug("⏭️ Duplicate messageId {MessageId} from {SessionId}, skipping", envelope.MessageId, Id);
                    return;
                }
                foreach (var kvp in _recentMessageIds)
                    if (nowMs - kvp.Value > MessageIdWindowMs)
                        _recentMessageIds.TryRemove(kvp.Key, out _);
            }

            // Auth gating for envelope actions
            if (envelope.Action != null && !IsAuthenticated)
            {
                await SendJsonAsync(new { command = "ERROR", message = "Authentication required.", ackFor = envelope.MessageId }, ct);
                return;
            }

            // Auth gating for legacy commands
            if (envelope.Command != null && RequiresAuthentication(envelope.Command) && !IsAuthenticated)
            {
                await SendJsonAsync(new { command = "ERROR", message = "Authentication required. Use REGISTER, LOGIN, or AUTO_AUTH first." }, ct);
                return;
            }

            await _router.RouteAsync(envelope, this, _server, ct);
        }
        catch (JsonException ex)
        {
            _server.Logger.LogError(ex, "❌ JSON parsing error for session {SessionId}", Id);
            await SendJsonAsync(new { command = "ERROR", message = "Invalid JSON format" }, ct);
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "❌ Error processing message for session {SessionId}", Id);
        }
    }

    private bool RequiresAuthentication(string command)
    {
        return command switch
        {
            "REGISTER" or "LOGIN" or "AUTO_AUTH" or "PING" or "BYE" or "PLAYER_INFO" or "LIST_ROOMS" => false,
            _ => true
        };
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try
        {
            await _writer.WriteAsync(data, ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "❌ Error sending data to session {SessionId}", Id);
            throw;
        }
    }
    
    public async Task SendTextAsync(string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await SendAsync(bytes, ct);
    }

    public async Task SendJsonAsync<T>(T message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        json += "\n"; // Keep the newline delimiter for message framing
        await SendTextAsync(json, ct);
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
                await _socket.DisconnectAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _server.Logger.LogError(ex, "❌ Error disconnecting session {SessionId}", Id);
        }
    }

    public void Dispose()
    {
        _reader.Complete();
        _writer.Complete();
        _stream.Dispose();
        _socket.Dispose();
    }
}