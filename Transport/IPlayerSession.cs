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

    // ── UDP encryption ────────────────────────────────────────────────────────
    UdpEncryption? UdpCrypto { get; }

    // ── Messaging ─────────────────────────────────────────────────────────────
    Task SendJsonAsync<T>(T message, CancellationToken ct = default);
    Task DisconnectAsync();
}
