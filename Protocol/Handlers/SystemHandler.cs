using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

public sealed class SystemHandler : ICommandHandler
{
    private readonly ILogger<SystemHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["PING", "NAME", "PLAYER_INFO", "BYE"];

    public SystemHandler(ILogger<SystemHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Command switch
        {
            "PING"        => session.SendJsonAsync(new { command = "PONG" }, ct),
            "NAME"        => HandleName(envelope, session, ct),
            "PLAYER_INFO" => session.SendJsonAsync(new { command = "PLAYER_INFO", playerInfo = new { id = session.Id, playerName = session.PlayerName, currentRoomId = session.CurrentRoomId } }, ct),
            "BYE"         => HandleBye(session, ct),
            _             => Task.CompletedTask
        };

    private async Task HandleName(MessageEnvelope e, IPlayerSession session, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("name", out var nameEl)) return;
        var display = nameEl.GetString();
        if (!string.IsNullOrWhiteSpace(display))
        {
            session.PlayerName = display;
            _logger.LogInformation("👤 Player {SessionId} set display name to '{Name}'", session.Id, session.PlayerName);
            await session.SendJsonAsync(new { command = "NAME_OK", name = session.PlayerName }, ct);
        }
        else
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Display name cannot be empty." }, ct);
        }
    }

    private async Task HandleBye(IPlayerSession session, CancellationToken ct)
    {
        _logger.LogInformation("👋 Player {SessionId} ({Name}) is disconnecting", session.Id, session.PlayerName);
        await session.SendJsonAsync(new { command = "BYE_OK" }, ct);
        await session.DisconnectAsync();
    }
}
