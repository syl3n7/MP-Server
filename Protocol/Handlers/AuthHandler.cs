using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Inventory;
using MP.Server.Services;
using MP.Server.Transport;

namespace MP.Server.Protocol.Handlers;

public sealed class AuthHandler : ICommandHandler
{
    private readonly AuthService _auth;
    private readonly ILogger<AuthHandler> _logger;

    public IReadOnlyList<string> Handles { get; } = ["REGISTER", "LOGIN", "AUTO_AUTH"];

    public AuthHandler(AuthService auth, ILogger<AuthHandler> logger)
    {
        _auth   = auth;
        _logger = logger;
    }

    public Task HandleAsync(MessageEnvelope envelope, IPlayerSession session, ITransportServer transport, CancellationToken ct)
        => envelope.Command switch
        {
            "REGISTER"  => HandleRegister(envelope, session, ct),
            "LOGIN"     => HandleLogin(envelope, session, ct),
            "AUTO_AUTH" => HandleAutoAuth(envelope, session, ct),
            _           => Task.CompletedTask
        };

    private async Task HandleRegister(MessageEnvelope e, IPlayerSession session, CancellationToken ct)
    {
        var username = e.Raw.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        var password = e.Raw.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
        var email    = e.Raw.TryGetProperty("email",    out var m) ? m.GetString() ?? "" : "";
        var result   = await _auth.RegisterAsync(username, password, email, session.RemoteIpAddress);
        if (result.Success)
        {
            session.Authenticate(result.UserId, result.Username ?? "Anonymous");
            _logger.LogInformation("✅ REGISTER → {Username} (Id={UserId}) session={SessionId}", result.Username, result.UserId, session.Id);
            await session.SendJsonAsync(new { command = "REGISTER_OK", userId = result.UserId, username = result.Username, token = result.Token }, ct);
            await InventoryManager.Instance.OnPlayerJoined(session.Id, ct);
        }
        else
        {
            await session.SendJsonAsync(new { command = "REGISTER_FAILED", message = result.Error }, ct);
        }
    }

    private async Task HandleLogin(MessageEnvelope e, IPlayerSession session, CancellationToken ct)
    {
        var username = e.Raw.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        var password = e.Raw.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
        var result   = await _auth.LoginAsync(username, password, session.RemoteIpAddress);
        if (result.Success)
        {
            session.Authenticate(result.UserId, result.Username ?? "Anonymous");
            _logger.LogInformation("🔐 LOGIN → {Username} (Id={UserId}) session={SessionId}", result.Username, result.UserId, session.Id);
            await session.SendJsonAsync(new { command = "LOGIN_OK", userId = result.UserId, username = result.Username, token = result.Token }, ct);
            await InventoryManager.Instance.OnPlayerJoined(session.Id, ct);
        }
        else
        {
            await session.SendJsonAsync(new { command = "LOGIN_FAILED", message = result.Error }, ct);
        }
    }

    private async Task HandleAutoAuth(MessageEnvelope e, IPlayerSession session, CancellationToken ct)
    {
        var token  = e.Raw.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
        var result = await _auth.AutoAuthAsync(token, session.RemoteIpAddress);
        if (result.Success)
        {
            session.Authenticate(result.UserId, result.Username ?? "Anonymous");
            _logger.LogInformation("🔑 AUTO_AUTH → {Username} (Id={UserId}) session={SessionId}", result.Username, result.UserId, session.Id);
            await session.SendJsonAsync(new { command = "AUTO_AUTH_OK", userId = result.UserId, username = result.Username }, ct);
            await InventoryManager.Instance.OnPlayerJoined(session.Id, ct);
        }
        else
        {
            await session.SendJsonAsync(new { command = "AUTO_AUTH_FAILED", message = result.Error }, ct);
        }
    }
}
