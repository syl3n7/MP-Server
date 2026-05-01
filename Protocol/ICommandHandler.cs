using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MP.Server.Transport;

namespace MP.Server.Protocol;

public interface ICommandHandler
{
    /// <summary>
    /// Keys this handler claims. Commands are UPPER_CASE; envelope actions are lower_case.
    /// </summary>
    IReadOnlyList<string> Handles { get; }

    Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct);
}
