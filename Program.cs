using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MP.Server.Data;
using MP.Server.Services;
using MP.Server.Logging;

// Setup cancellation tokens for clean shutdown
var serverCts = new CancellationTokenSource();
var appCts = new CancellationTokenSource();

// Create builder for the web application
var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllersWithViews();
builder.Logging.AddConsole();

// Add database logging service
builder.Services.AddScoped<DatabaseLoggingService>();

// Add Entity Framework with MariaDB
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Server=localhost;Database=mpserver;User=root;Password=yourpassword;";
builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Register user management services
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<EmailService>();

// Register server management service
builder.Services.AddSingleton<ServerManagementService>();

// Register background services
builder.Services.AddHostedService<LogCleanupService>();

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
    
    // Initialize database with default connection string if no server is configured
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await context.Database.EnsureCreatedAsync();
        Console.WriteLine("Database initialized with default connection.");
    }

    // Auto-start the racing server with default configuration
    try
    {
        var serverManagement = app.Services.GetRequiredService<ServerManagementService>();
        var defaultConfig = new MP.Server.Services.ServerConfiguration
        {
            TcpPort = 443,
            UdpPort = 443,
            UseTls = true,
            ConnectionString = connectionString
        };
        
        var startResult = await serverManagement.StartServerAsync(defaultConfig);
        if (startResult.Success)
        {
            Console.WriteLine("🚀 Racing server started automatically!");
            Console.WriteLine($"   TCP Port: {defaultConfig.TcpPort}");
            Console.WriteLine($"   UDP Port: {defaultConfig.UdpPort}");
            Console.WriteLine($"   TLS: {(defaultConfig.UseTls ? "Enabled" : "Disabled")}");
        }
        else
        {
            Console.WriteLine($"⚠️  Could not auto-start racing server: {startResult.Message}");
            Console.WriteLine("   You can start it manually from the dashboard.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️  Failed to auto-start racing server: {ex.Message}");
        Console.WriteLine("   You can start it manually from the dashboard.");
    }

    Console.WriteLine("🌐 Web dashboard started! Access it at: http://localhost:8080");
    Console.WriteLine("📊 Dashboard ready - server logs and controls available.");

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
    // Clean shutdown - check if server management service has a running server
    var serverManagement = app.Services.GetRequiredService<ServerManagementService>();
    if (serverManagement.IsServerRunning)
    {
        await serverManagement.StopServerAsync();
    }
        
    serverCts.Dispose();
    appCts.Dispose();
}