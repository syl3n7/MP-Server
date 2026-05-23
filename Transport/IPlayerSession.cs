using System;
using System.Threading;
using System.Threading.Tasks;
using MP.Server.Security;

namespace MP.Server.Transport;

/// <summary>
/// Abstracts a connected player session for use by Protocol handlers and the transport layer.
/// Keeps handler code free from concrete I/O types.
/// </summary>
public interface IPlayerSession
{
    // ── Identity ──────────────────────────────────────────────────────────────
    string Id { get; }
    string PlayerName { get; set; }

    // ── Auth state ────────────────────────────────────────────────────────────
    bool IsAuthenticated { get; set; }
    int? AuthenticatedUserId { get; }
    string? AuthenticatedUsername { get; }

    // ── Room state ────────────────────────────────────────────────────────────
    string? CurrentRoomId { get; set; }

    // ── Activity ──────────────────────────────────────────────────────────────
    DateTime LastActivity { get; }

    // ── RTT / latency ─────────────────────────────────────────────────────────
    /// <summary>Last measured round-trip time in milliseconds (0 if not yet measured).</summary>
    double LastRttMs { get; }
    /// <summary>Smoothed RTT jitter (EWMA) in milliseconds.</summary>
    double JitterMs  { get; }
    /// <summary>Records a new RTT sample and updates jitter (called by SystemHandler on PONG).</summary>
    void RecordRtt(double rttMs);
    /// <summary>Unix timestamp (ms) when the server last sent PING. 0 = no probe pending.</summary>
    long PingSentAt { get; set; }

    // ── UDP encryption ────────────────────────────────────────────────────────
    UdpEncryption? UdpCrypto { get; }

    // ── UDP remote endpoint (set by transport on first/each UDP packet received) ──
    System.Net.IPEndPoint? UdpEndpoint { get; set; }

    // ── Network info ──────────────────────────────────────────────────────────
    /// <summary>Remote IP string, or null if unavailable. Used by auth handlers.</summary>
    string? RemoteIpAddress { get; }

    // ── Auth helper ───────────────────────────────────────────────────────────
    /// <summary>Set auth state and initialise UDP crypto. Called by AuthHandler on success.</summary>
    void Authenticate(int? userId, string username);

    // ── Messaging ─────────────────────────────────────────────────────────────
    Task SendJsonAsync<T>(T message, CancellationToken ct = default);
    Task DisconnectAsync();
}
