using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MP.Server.Data;
using MP.Server.Observability;
using MP.Server.Security;
using MP.Server.Services;
using MP.Server.Transport;

Console.WriteLine("🏁 MP-Server");
Console.WriteLine("============");

var builder = WebApplication.CreateBuilder(args);

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

// ── Game server ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<GameServer>(sp =>
{
    var cfg    = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<GameServer>>();
    var dbLog  = sp.GetRequiredService<DatabaseLoggingService>();
    var auth   = sp.GetRequiredService<AuthService>();

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

    return new GameServer(tcpPort, udpPort, logger, useTls, null, secCfg, dbLog, auth, publicIp, hostname);
});

// Register GameServer as IHostedService so the host starts/stops it automatically.
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameServer>());

// Console UI runs as a background service alongside the web host.
builder.Services.AddHostedService<ConsoleUiService>();

// ── Dashboard HTTP port ───────────────────────────────────────────────────────
int dashPort = builder.Configuration.GetValue<int>("DashboardSettings:Port", 8080);
builder.WebHost.UseUrls($"http://0.0.0.0:{dashPort}");

var app = builder.Build();

// ── Database initialisation ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<UserDbContext>>();
    await using var ctx = await dbFactory.CreateDbContextAsync();
    await ctx.Database.EnsureCreatedAsync();
    Console.WriteLine("✅ Database initialised");
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
