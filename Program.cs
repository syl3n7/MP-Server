using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MP.Server.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MP.Server.Data;
using MP.Server.Services;
using MP.Server.Logging;
using MP.Server.Security;
using MP.Server.Transport;

Console.WriteLine("🏁 MP-Server Console Edition");
Console.WriteLine("============================");

// Setup cancellation tokens for clean shutdown
var serverCts = new CancellationTokenSource();
var appCts = new CancellationTokenSource();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n🛑 Shutdown requested...");
    appCts.Cancel();
};

try
{
    // Create host builder for dependency injection
    var builder = Host.CreateDefaultBuilder(args);
    builder.ConfigureServices((context, services) =>
    {
        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        // Add database services
        var connectionString = context.Configuration.GetSection("ConnectionStrings")["DefaultConnection"] 
            ?? "Server=localhost;Database=mpserver;User=root;Password=yourpassword;Port=3306;";
        
        // AddDbContextFactory registers both IDbContextFactory<T> (singleton) and T (scoped),
        // allowing concurrent per-operation contexts in AuthService while keeping the
        // scoped pattern available for DatabaseLoggingService.
        services.AddDbContextFactory<UserDbContext>(options =>
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
        
        services.AddScoped<DatabaseLoggingService>();
        services.AddSingleton<AuthService>();
        services.AddHostedService<LogCleanupService>();
    });

    var host = builder.Build();
    
    // Initialize database
    using (var scope = host.Services.CreateScope())
    {
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<UserDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        Console.WriteLine("✅ Database initialized");
    }
    
    // Create and start the game server
    using var serverScope = host.Services.CreateScope();
    var logger = serverScope.ServiceProvider.GetRequiredService<ILogger<GameServer>>();
    var dbLoggingService = serverScope.ServiceProvider.GetRequiredService<DatabaseLoggingService>();
    var authService = host.Services.GetRequiredService<AuthService>();
    
    // Server configuration — read from appsettings.json ServerSettings
    var config = host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
    int tcpPort = int.TryParse(config["ServerSettings:TcpPort"], out int tp) ? tp : 7777;
    int udpPort = int.TryParse(config["ServerSettings:UdpPort"], out int up) ? up : 7778;
    bool useTls = !bool.TryParse(config["ServerSettings:UseTls"], out bool tls) || tls;

    // Read public IP from config (ServerSettings:PublicIP) with env-var fallback
    var publicIp = config["ServerSettings:PublicIP"]
        ?? Environment.GetEnvironmentVariable("SERVER_PUBLIC_IP")
        ?? "0.0.0.0";
    var hostname = config["ServerSettings:Hostname"]
        ?? Environment.GetEnvironmentVariable("SERVER_HOSTNAME")
        ?? "mp-server";
    
    Console.WriteLine($"\ud83d\ude80 Starting Game Server...");
    Console.WriteLine($"   TCP Port: {tcpPort}");
    Console.WriteLine($"   UDP Port: {udpPort}");
    Console.WriteLine($"   TLS: {(useTls ? "Enabled" : "Disabled")}");
    Console.WriteLine($"   Public IP: {publicIp}");
    Console.WriteLine($"   Hostname: {hostname}");
    
    // Build SecurityConfig, pulling the UDP secret from appsettings.json
    var securityConfig = new MP.Server.Security.SecurityConfig
    {
        UdpSharedSecret = config["SecurityConfig:UdpSharedSecret"] ?? "change-me-before-deploying"
    };
    
    var server = new GameServer(tcpPort, udpPort, logger, useTls, null, securityConfig, dbLoggingService, authService, publicIp, hostname);
    
    // Start server
    await server.StartAsync(serverCts.Token);
    Console.WriteLine("✅ Game server started successfully!");
    
    // Start console UI
    var consoleUI = new ConsoleUI(server, serverCts);
    
    // Run both the server and console UI
    var serverTask = Task.Run(async () =>
    {
        try
        {
            while (!appCts.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, appCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    });
    
    var consoleTask = consoleUI.RunAsync(appCts.Token);
    
    // Wait for either task to complete
    await Task.WhenAny(serverTask, consoleTask);
    
    Console.WriteLine("🛑 Shutting down server...");
    serverCts.Cancel();
    await server.StopAsync(CancellationToken.None);
    
    Console.WriteLine("👋 Server stopped. Goodbye!");
}
catch (Exception ex)
{
    Console.WriteLine($"💥 Fatal error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
}
finally
{
    serverCts?.Dispose();
    appCts?.Dispose();
}