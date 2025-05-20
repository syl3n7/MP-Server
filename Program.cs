using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;

// Setup cancellation tokens for clean shutdown
var serverCts = new CancellationTokenSource();
var appCts = new CancellationTokenSource();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole();
var host = builder.Build();

// Create the server
var server = new RacingServer(8443, 8443, host.Services.GetRequiredService<ILogger<RacingServer>>());

try
{
    // Start server
    await server.StartAsync(serverCts.Token);
    Console.WriteLine("Server started! Type 'help' for available commands.");
    
    // Launch UI in separate task
    var consoleUI = new ConsoleUI(server, serverCts);
    _ = Task.Run(() => consoleUI.RunAsync(appCts.Token));
    
    // Run until host is stopped
    await host.RunAsync(appCts.Token);
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
    if (!serverCts.IsCancellationRequested)
        await server.StopAsync();
        
    serverCts.Dispose();
    appCts.Dispose();
}