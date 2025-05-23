using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

// Setup cancellation tokens for clean shutdown
var serverCts = new CancellationTokenSource();
var appCts = new CancellationTokenSource();

// Create builder for the web application
var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllersWithViews();
builder.Logging.AddConsole();

// Register the racing server as a singleton (with TLS enabled by default)
builder.Services.AddSingleton<RacingServer>(serviceProvider => 
{
    var logger = serviceProvider.GetRequiredService<ILogger<RacingServer>>();
    return new RacingServer(443, 443, logger, useTls: true);
});

// Build the web application
var app = builder.Build();

try
{
    // Configure the HTTP request pipeline
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
    }
    
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthorization();
    
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Dashboard}/{action=Index}/{id?}");
    
    // Get the racing server from DI container
    var server = app.Services.GetRequiredService<RacingServer>();
    
    // Start the racing server
    await server.StartAsync(serverCts.Token);
    Console.WriteLine("Server started! Type 'help' for available commands.");
    
    // Launch console UI in separate task
    var consoleUI = new ConsoleUI(server, serverCts);
    _ = Task.Run(() => consoleUI.RunAsync(appCts.Token));
    
    // Run the web application on port 8080
    app.Urls.Add("http://0.0.0.0:8080");
    await app.RunAsync(appCts.Token);
}
catch (OperationCanceledException)
{
    // Expected during shutdown
}
catch (Exception ex)
{
    Console.WriteLine($"Fatal error: {ex.Message}");
}
finally
{
    // Clean shutdown
    var server = app.Services.GetRequiredService<RacingServer>();
    if (!serverCts.IsCancellationRequested)
        await server.StopAsync();
        
    serverCts.Dispose();
    appCts.Dispose();
}