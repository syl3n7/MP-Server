using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MP.Server.Data;
using MP.Server.Observability;
using MP.Server.Protocol;
using MP.Server.Protocol.Handlers;
using MP.Server.Security;
using MP.Server.Services;
using MP.Server.Testing;
using MP.Server.Transport;
using Serilog;

// Bootstrap logger captures startup errors before full config is loaded.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

Console.WriteLine("🏁 MP-Server");
Console.WriteLine("============");

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ── Web / MVC ─────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Database=mpserver;User=root;Password=yourpassword;Port=3306;";

builder.Services.AddDbContextFactory<UserDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// ── Server services ───────────────────────────────────────────────────────────
// DatabaseLoggingService uses IServiceScopeFactory internally → safe as singleton.
builder.Services.AddSingleton<DatabaseLoggingService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddHostedService<LogCleanupService>();

// ── Protocol handlers ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<ICommandHandler, AuthHandler>();
builder.Services.AddSingleton<ICommandHandler, RoomHandler>();
builder.Services.AddSingleton<ICommandHandler, ChatHandler>();
builder.Services.AddSingleton<ICommandHandler, InventoryHandler>();
builder.Services.AddSingleton<ICommandHandler, CombatHandler>();
builder.Services.AddSingleton<ICommandHandler, SystemHandler>();
builder.Services.AddSingleton<ICommandHandler, EnvelopeHandler>();
builder.Services.AddSingleton<ICommandHandler, UdpMovementHandler>();
builder.Services.AddSingleton<CommandRouter>();

// ── Game server ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<GameServer>(sp =>
{
    var cfg    = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<GameServer>>();
    var dbLog  = sp.GetRequiredService<DatabaseLoggingService>();
    var router = sp.GetRequiredService<CommandRouter>();

    int    tcpPort  = cfg.GetValue<int>("ServerSettings:TcpPort", 7777);
    int    udpPort  = cfg.GetValue<int>("ServerSettings:UdpPort", 7778);
    bool   useTls   = cfg.GetValue<bool>("ServerSettings:UseTls", true);
    string publicIp = cfg["ServerSettings:PublicIP"]
                      ?? Environment.GetEnvironmentVariable("SERVER_PUBLIC_IP")
                      ?? "0.0.0.0";
    string hostname = cfg["ServerSettings:Hostname"]
                      ?? Environment.GetEnvironmentVariable("SERVER_HOSTNAME")
                      ?? "mp-server";

    var secCfg = new SecurityConfig
    {
        UdpSharedSecret = cfg["SecurityConfig:UdpSharedSecret"] ?? "change-me-before-deploying"
    };

    return new GameServer(tcpPort, udpPort, logger, router, useTls, null, secCfg, dbLog, publicIp, hostname);
});

// Register GameServer as IHostedService so the host starts/stops it automatically.
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameServer>());

// Console UI runs as a background service alongside the web host.
builder.Services.AddHostedService<ConsoleUiService>();

// ── Load test bots (disabled by default — enable via LoadTest:Enabled in appsettings) ───
builder.Services.AddHostedService<BotRunnerService>();

// ── Dashboard HTTP port ───────────────────────────────────────────────────────
int dashPort = builder.Configuration.GetValue<int>("DashboardSettings:Port", 8080);
builder.WebHost.UseUrls($"http://0.0.0.0:{dashPort}");

var app = builder.Build();

// ── Database initialisation ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<UserDbContext>>();
    await using var ctx = await dbFactory.CreateDbContextAsync();
    await ctx.Database.MigrateAsync();
    Console.WriteLine("✅ Database migrated");
}

Console.WriteLine($"🌐 Dashboard → http://0.0.0.0:{dashPort}");

// ── Routing ───────────────────────────────────────────────────────────────────
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");

await app.RunAsync();

// ─────────────────────────────────────────────────────────────────────────────
// ConsoleUI hosted-service wrapper
// ─────────────────────────────────────────────────────────────────────────────
public sealed class ConsoleUiService : BackgroundService
{
    private readonly GameServer _server;
    private readonly IHostApplicationLifetime _lifetime;

    public ConsoleUiService(GameServer server, IHostApplicationLifetime lifetime)
    {
        _server  = server;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the server a moment to fully start before printing the prompt.
        await Task.Delay(500, stoppingToken).ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ui  = new ConsoleUI(_server, cts);

        try
        {
            await ui.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }

        // If the console UI requested quit, stop the whole application.
        if (!stoppingToken.IsCancellationRequested)
            _lifetime.StopApplication();
    }
}
