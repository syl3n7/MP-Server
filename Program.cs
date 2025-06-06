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

// Setup cancellation tokens for clean shutdown
var serverCts = new CancellationTokenSource();
var appCts = new CancellationTokenSource();

// Create builder for the web application
var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllersWithViews();
builder.Logging.AddConsole();

// Add Entity Framework with SQLite
builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Data Source=users.db"));

// Register user management services
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<EmailService>();

// Register the racing server as a singleton (with TLS enabled by default)
builder.Services.AddSingleton<RacingServer>(serviceProvider => 
{
    var logger = serviceProvider.GetRequiredService<ILogger<RacingServer>>();
    var userService = serviceProvider.GetRequiredService<UserManagementService>();
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
    
    // Initialize database
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await context.Database.EnsureCreatedAsync();
        Console.WriteLine("Database initialized.");
    }
    
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