using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MP.Server.Testing;

/// <summary>
/// Optional hosted service that spawns N BotClients against the local server
/// when LoadTest:Enabled = true in appsettings.json.
///
/// Add to Program.cs:
///   builder.Services.AddHostedService&lt;BotRunnerService&gt;();
///
/// Config (appsettings.json):
///   "LoadTest": {
///     "Enabled": false,
///     "BotCount": 10,
///     "UdpIntervalMs": 100
///   }
/// </summary>
public sealed class BotRunnerService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<BotRunnerService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public BotRunnerService(IConfiguration cfg, ILogger<BotRunnerService> logger, ILoggerFactory loggerFactory)
    {
        _cfg          = cfg;
        _logger       = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.GetValue<bool>("LoadTest:Enabled"))
        {
            _logger.LogInformation("🤖 BotRunner disabled (LoadTest:Enabled = false)");
            return;
        }

        int      botCount      = _cfg.GetValue<int>("LoadTest:BotCount", 10);
        int      udpIntervalMs = _cfg.GetValue<int>("LoadTest:UdpIntervalMs", 100);
        string   host          = _cfg["ServerSettings:PublicIP"] ?? "127.0.0.1";
        int      tcpPort       = _cfg.GetValue<int>("ServerSettings:TcpPort", 7777);
        int      udpPort       = _cfg.GetValue<int>("ServerSettings:UdpPort", 7778);
        string   token         = _cfg["ServerSettings:AutoJoinToken"] ?? "test-lab";
        string   udpSecret     = _cfg["SecurityConfig:UdpSharedSecret"] ?? "change-me-before-deploying";

        // Give the server a moment to fully bind before bots connect.
        await Task.Delay(2000, stoppingToken);

        _logger.LogInformation("🤖 BotRunner starting {Count} bots → {Host}:{Port}", botCount, host, tcpPort);

        var bots = new List<Task>();
        for (int i = 0; i < botCount; i++)
        {
            // Stagger connections slightly to avoid a thundering-herd on TLS handshakes.
            await Task.Delay(50 * i, stoppingToken);

            var botLogger = _loggerFactory.CreateLogger<BotClient>();
            var bot = new BotClient(
                host:            host,
                tcpPort:         tcpPort,
                udpPort:         udpPort,
                autoJoinToken:   token,
                logger:          botLogger,
                username:        $"bot{i:D4}",
                password:        "bot-password-123",
                email:           $"bot{i:D4}@bot.test",
                udpSharedSecret: udpSecret,
                udpInterval:     TimeSpan.FromMilliseconds(udpIntervalMs));

            bots.Add(RunBotAsync(bot, stoppingToken));
        }

        await Task.WhenAll(bots);
        _logger.LogInformation("🤖 BotRunner finished — all {Count} bots stopped", botCount);
    }

    private async Task RunBotAsync(BotClient bot, CancellationToken ct)
    {
        await using (bot)
        {
            await bot.RunAsync(ct);
        }
    }
}
