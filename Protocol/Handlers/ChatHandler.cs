using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

public sealed class ChatHandler : ICommandHandler
{
    private readonly ILogger<ChatHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["MESSAGE", "RELAY_MESSAGE"];

    public ChatHandler(ILogger<ChatHandler> logger) => _logger = logger;

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Command switch
        {
            "MESSAGE"       => HandleMessage(envelope, session, transport, ct),
            "RELAY_MESSAGE" => HandleRelay(envelope, session, transport, ct),
            _               => Task.CompletedTask
        };

    private async Task HandleMessage(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("message", out var msgEl))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Message content is required." }, ct);
            return;
        }
        var text = msgEl.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(session.CurrentRoomId))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Empty message or not in a room." }, ct);
            return;
        }
        _logger.LogInformation("💬 Player {SessionId} ({Name}) sent message: {Message}", session.Id, session.PlayerName, text);
        await transport.BroadcastChatMessageAsync(session.CurrentRoomId, session.PlayerName, text, session.Id);
        await session.SendJsonAsync(new { command = "MESSAGE_OK" }, ct);
    }

    private async Task HandleRelay(MessageEnvelope e, IPlayerSession session, ITransportServer transport, CancellationToken ct)
    {
        if (!e.Raw.TryGetProperty("message",  out var msgEl) ||
            !e.Raw.TryGetProperty("targetId", out var targetEl))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Invalid message relay request." }, ct);
            return;
        }
        var content  = msgEl.GetString();
        var targetId = targetEl.GetString();
        if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(content))
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Invalid message relay request. Missing target or message." }, ct);
            return;
        }
        var target = transport.GetPlayerSession(targetId);
        if (target == null)
        {
            await session.SendJsonAsync(new { command = "ERROR", message = "Target player not found." }, ct);
            return;
        }
        await target.SendJsonAsync(new { command = "RELAYED_MESSAGE", senderId = session.Id, senderName = session.PlayerName, message = content }, ct);
        await session.SendJsonAsync(new { command = "RELAY_OK", targetId }, ct);
        _logger.LogInformation("✉️ Message relayed from {SenderId} to {TargetId}", session.Id, targetId);
    }
}
