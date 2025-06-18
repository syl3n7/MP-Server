using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MP.Server.Data;
using MP.Server.Services;
using MP.Server.Logging;
using MP.Server.Security;

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
        
        services.AddDbContext<UserDbContext>(options =>
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
        
        services.AddScoped<DatabaseLoggingService>();
        services.AddHostedService<LogCleanupService>();
    });

    var host = builder.Build();
    
    // Initialize database
    using (var scope = host.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await context.Database.EnsureCreatedAsync();
        Console.WriteLine("✅ Database initialized");
    }
    
    // Create and start the racing server
    using var serverScope = host.Services.CreateScope();
    var logger = serverScope.ServiceProvider.GetRequiredService<ILogger<RacingServer>>();
    var dbLoggingService = serverScope.ServiceProvider.GetRequiredService<DatabaseLoggingService>();
    
    // Server configuration
    const int tcpPort = 443;
    const int udpPort = 443;
    const bool useTls = true;
    
    Console.WriteLine($"🚀 Starting Racing Server...");
    Console.WriteLine($"   TCP Port: {tcpPort}");
    Console.WriteLine($"   UDP Port: {udpPort}");
    Console.WriteLine($"   TLS: {(useTls ? "Enabled" : "Disabled")}");
    
    var server = new RacingServer(tcpPort, udpPort, logger, useTls, null, null, dbLoggingService);
    
    // Start server
    await server.StartAsync(serverCts.Token);
    Console.WriteLine("✅ Racing server started successfully!");
    
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