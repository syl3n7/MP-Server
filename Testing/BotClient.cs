using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Mime;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Security;

namespace MP.Server.Testing;

/// <summary>
/// Synthetic bot client used for load testing (#17).
///
/// Full flow (mirrors what a real Godot client must do):
///   1. TCP connect + TLS handshake
///   2. Read CONNECTED|sessionId
///   3. REGISTER or LOGIN → receive token
///   4. AUTO_JOIN (with token) → land in the shared test room
///   5. Periodically send UDP position updates while running
///   6. BYE on shutdown
///
/// Usage — spawn N of these concurrently:
///   var bot = new BotClient(host, tcpPort, udpPort, autoJoinToken, logger);
///   await bot.RunAsync(stoppingToken);
/// </summary>
public sealed class BotClient : IAsyncDisposable
{
    // ── Config ─────────────────────────────────────────────────────────────────
    private readonly string _host;
    private readonly int    _tcpPort;
    private readonly int    _udpPort;
    private readonly string _autoJoinToken;
    private readonly string _username;
    private readonly string _password;
    private readonly string _email;
    private readonly TimeSpan _udpInterval;
    private readonly ILogger  _logger;

    // ── State ──────────────────────────────────────────────────────────────────
    private string? _sessionId;
    private string? _roomId;
    private UdpEncryption? _udpCrypto;
    private string? _udpSharedSecret;   // injected or default

    private TcpClient?  _tcp;
    private SslStream?  _ssl;
    private StreamReader? _reader;
    private UdpClient?  _udp;

    public string? SessionId => _sessionId;
    public string? RoomId    => _roomId;
    public bool    InRoom    => _roomId != null;

    // ── Constructor ────────────────────────────────────────────────────────────
    public BotClient(
        string host,
        int    tcpPort,
        int    udpPort,
        string autoJoinToken,
        ILogger logger,
        string? username        = null,
        string? password        = null,
        string? email           = null,
        string? udpSharedSecret = null,
        TimeSpan? udpInterval   = null)
    {
        _host           = host;
        _tcpPort        = tcpPort;
        _udpPort        = udpPort;
        _autoJoinToken  = autoJoinToken;
        _logger         = logger;
        _username       = username       ?? $"bot_{Guid.NewGuid():N8}";
        _password       = password       ?? "bot-password-123";
        _email          = email          ?? $"{_username}@bot.test";
        _udpSharedSecret = udpSharedSecret ?? "change-me-before-deploying";
        _udpInterval    = udpInterval    ?? TimeSpan.FromMilliseconds(100); // 10 Hz
    }

    // ── Main run loop ──────────────────────────────────────────────────────────
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await ConnectAsync(ct);
            await AuthenticateAsync(ct);
            await AutoJoinAsync(ct);
            await SendUdpLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Bot {Name}] Unexpected error", _username);
        }
        finally
        {
            await ByeAsync();
        }
    }

    // ── Step 1: TCP + TLS ──────────────────────────────────────────────────────
    private async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _tcpPort, ct);

        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);

        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _host,
            RemoteCertificateValidationCallback = (_, _, _, errors) =>
                errors == SslPolicyErrors.None ||
                errors == SslPolicyErrors.RemoteCertificateChainErrors ||
                errors == SslPolicyErrors.RemoteCertificateNameMismatch
        }, ct);

        _reader = new StreamReader(_ssl, Encoding.UTF8);
        var welcome = await _reader.ReadLineAsync(ct) ?? throw new IOException("No welcome from server");
        // Server sends JSON: {"command":"CONNECTED","sessionId":"<id>"}
        using (var welcomeDoc = JsonDocument.Parse(welcome))
        {
            _sessionId = welcomeDoc.RootElement.GetProperty("sessionId").GetString()
                ?? throw new IOException("Welcome message missing sessionId");
        }

        _logger.LogInformation("[Bot {Name}] Connected, session={SessionId}", _username, _sessionId);
    }

    // ── Step 2: Register (falls back to Login on duplicate username) ───────────
    private async Task AuthenticateAsync(CancellationToken ct)
    {
        await SendTcpAsync(new { command = "REGISTER", username = _username, password = _password, email = _email }, ct);
        var response = await ReadTcpAsync(ct);

        if (response.TryGetProperty("command", out var cmd))
        {
            switch (cmd.GetString())
            {
                case "REGISTER_OK":
                    _logger.LogInformation("[Bot {Name}] Registered successfully", _username);
                    InitUdpCrypto();
                    return;

                case "REGISTER_FAILED":
                    // Username already exists — fall back to LOGIN
                    _logger.LogDebug("[Bot {Name}] REGISTER_FAILED, trying LOGIN", _username);
                    await SendTcpAsync(new { command = "LOGIN", username = _username, password = _password }, ct);
                    var loginResp = await ReadTcpAsync(ct);
                    if (loginResp.TryGetProperty("command", out var lCmd) && lCmd.GetString() == "LOGIN_OK")
                    {
                        _logger.LogInformation("[Bot {Name}] Logged in successfully", _username);
                        InitUdpCrypto();
                        return;
                    }
                    throw new InvalidOperationException($"[Bot {_username}] LOGIN failed: {loginResp}");

                default:
                    throw new InvalidOperationException($"[Bot {_username}] Unexpected auth response: {response}");
            }
        }
    }

    private void InitUdpCrypto()
    {
        if (_sessionId != null)
            _udpCrypto = new UdpEncryption(_sessionId, _udpSharedSecret!);
    }

    // ── Step 3: AUTO_JOIN ──────────────────────────────────────────────────────
    private async Task AutoJoinAsync(CancellationToken ct)
    {
        await SendTcpAsync(new { command = "AUTO_JOIN", token = _autoJoinToken }, ct);
        var response = await ReadTcpAsync(ct);

        if (!response.TryGetProperty("command", out var cmd))
            throw new InvalidOperationException($"[Bot {_username}] Malformed AUTO_JOIN response");

        switch (cmd.GetString())
        {
            case "JOIN_OK":
                _roomId = response.TryGetProperty("roomId", out var rid) ? rid.GetString() : null;
                _logger.LogInformation("[Bot {Name}] AUTO_JOIN → room {RoomId}", _username, _roomId);

                // Set up UDP socket now that we have a room
                _udp = new UdpClient();
                break;

            case "ERROR":
                var msg = response.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                throw new InvalidOperationException($"[Bot {_username}] AUTO_JOIN error: {msg}");

            default:
                throw new InvalidOperationException($"[Bot {_username}] Unexpected AUTO_JOIN response: {response}");
        }
    }

    // ── Step 4: UDP position update loop ──────────────────────────────────────
    private async Task SendUdpLoopAsync(CancellationToken ct)
    {
        if (_udp == null || _roomId == null || _sessionId == null) return;

        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            var update = new
            {
                command   = "UPDATE",
                sessionId = _sessionId,
                position  = new { x = (float)(rng.NextDouble() * 100 - 50), y = -2f, z = 0.8f },
                rotation  = new { x = 0f, y = 0f, z = 0f, w = 1f }
            };

            try
            {
                byte[] packet = _udpCrypto != null
                    ? _udpCrypto.CreatePacket(update)
                    : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(update));

                await _udp.SendAsync(packet, _host, _udpPort, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("[Bot {Name}] UDP send failed: {Error}", _username, ex.Message);
            }

            await Task.Delay(_udpInterval, ct);
        }
    }

    // ── Shutdown ───────────────────────────────────────────────────────────────
    private async Task ByeAsync()
    {
        try
        {
            if (_ssl != null)
                await SendTcpAsync(new { command = "BYE" }, CancellationToken.None);
        }
        catch { /* best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        if (_ssl != null) await _ssl.DisposeAsync();
        _tcp?.Dispose();
        _udp?.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private async Task SendTcpAsync(object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        await _ssl!.WriteAsync(bytes, ct);
    }

    private async Task<JsonElement> ReadTcpAsync(CancellationToken ct)
    {
        var line = await _reader!.ReadLineAsync(ct) ?? throw new IOException("Connection closed");
        return JsonSerializer.Deserialize<JsonElement>(line);
    }
}
