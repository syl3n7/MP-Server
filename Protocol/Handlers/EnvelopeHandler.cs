using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

public sealed class EnvelopeHandler : ICommandHandler
{
    private readonly ILogger<EnvelopeHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["heartbeat", "snapshot_sync"];

    public EnvelopeHandler(ILogger<EnvelopeHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Action switch
        {
            "heartbeat"     => session.SendJsonAsync(new
            {
                command          = "HEARTBEAT_ACK",
                ackFor           = envelope.MessageId,
                serverTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId        = session.Id
            }, ct),
            "snapshot_sync" => HandleSnapshot(envelope, session, transport, ct),
            _               => Task.CompletedTask
        };

    private async Task HandleSnapshot(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "SNAPSHOT", ackFor = envelope.MessageId, players = Array.Empty<object>() }, ct);
            return;
        }
        var room     = transport.GetAllRooms().FirstOrDefault(r => r.Id == session.CurrentRoomId);
        var snapshot = room?.Players.Select(p => new { id = p.Id, name = p.Name }) ?? Enumerable.Empty<object>();
        await session.SendJsonAsync(new { command = "SNAPSHOT", ackFor = envelope.MessageId, roomId = session.CurrentRoomId, players = snapshot }, ct);
    }
}
