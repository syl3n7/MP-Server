using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MP.Server.Transport;

namespace MP.Server.Protocol;

public sealed class CommandRouter
{
    private readonly Dictionary<string, ICommandHandler> _map;

    public CommandRouter(IEnumerable<ICommandHandler> handlers)
    {
        _map = handlers
            .SelectMany(h => h.Handles.Select(key => (key, h)))
            .ToDictionary(x => x.key, x => x.h, StringComparer.OrdinalIgnoreCase);
    }

    public Task RouteAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        var key = envelope.Action ?? envelope.Command;
        if (string.IsNullOrEmpty(key)) return Task.CompletedTask;

        return _map.TryGetValue(key, out var handler)
            ? handler.HandleAsync(envelope, session, transport, ct)
            : session.SendJsonAsync(new { command = "UNKNOWN_COMMAND", originalCommand = key }, ct);
    }
}
