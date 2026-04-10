using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MP.Server.Testing;
using MP.Server.Diagnostics;

public class ConsoleUI
{
    private readonly RacingServer _server;
    private readonly CancellationTokenSource _serverCts;

    public ConsoleUI(RacingServer server, CancellationTokenSource serverCts)
    {
        _server = server;
        _serverCts = serverCts;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("Server Console");
        Console.WriteLine("=====================");
        PrintHelp();

        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = await Task.Run(() => Console.ReadLine(), ct);

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var command = input.Trim().ToLower();
            
            switch (command)
            {
                case "help":
                    PrintHelp();
                    break;
                    
                case "rooms":
                    ListRooms();
                    break;
                    
                case "sessions":
                    ListSessions();
                    break;
                    
                case "stats":
                    ShowStats();
                    break;
                    
                case "logs":
                    await ShowRecentLogs();
                    break;
                    
                case "clear":
                    Console.Clear();
                    Console.WriteLine("Server Console");
                    Console.WriteLine("=====================");
                    break;
                    
                case "config":
                    ShowConfiguration();
                    break;
                    
                case "kick":
                    await HandleKickPlayer();
                    break;
                    
                case "test-wan":
                    await TestWanConnectivity();
                    break;
                    
                case "test-lan":
                    await TestLanConnectivity();
                    break;
                    
                case "network-info":
                    ShowNetworkInfo();
                    break;
                    
                case "quit":
                case "exit":
                    Console.WriteLine("Shutting down server...");
                    _serverCts.Cancel();
                    return;
                    
                default:
                    Console.WriteLine("Unknown command. Type 'help' for available commands.");
                    break;
            }
        }
    }

    private void PrintHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  help        - Show this help message");
        Console.WriteLine("  rooms       - List all game rooms");
        Console.WriteLine("  sessions    - List all active player sessions");
        Console.WriteLine("  stats       - Show server statistics");
        Console.WriteLine("  logs        - Show recent server logs");
        Console.WriteLine("  clear       - Clear console screen");
        Console.WriteLine("  config      - Show server configuration");
        Console.WriteLine("  kick        - Kick a player by ID");
        Console.WriteLine("  test-wan    - Test WAN connectivity (89.114.116.19:443)");
        Console.WriteLine("  test-lan    - Test LAN connectivity (192.168.3.123:443)");
        Console.WriteLine("  network-info- Show network interface information");
        Console.WriteLine("  quit        - Shut down the server");
    }

    private void ListRooms()
    {
        var rooms = _server.GetAllRooms();
        
        if (rooms.Count == 0)
        {
            Console.WriteLine("No active rooms.");
            return;
        }
        
        Console.WriteLine($"Total rooms: {rooms.Count}");
        Console.WriteLine("ID                               | Name                | Players | Active | Host");
        Console.WriteLine("----------------------------------|---------------------|---------|--------|----------------------------------");
        
        foreach (var room in rooms)
        {
            Console.WriteLine($"{room.Id,-34} | {TruncateString(room.Name, 19),-19} | {room.PlayerCount,7} | {(room.IsActive ? "Yes" : "No"),6} | {TruncateString(room.HostId ?? "none", 34)}");
        }
    }

    private void ListSessions()
    {
        var sessions = _server.GetAllSessions();
        
        if (sessions.Count == 0)
        {
            Console.WriteLine("No active sessions.");
            return;
        }
        
        Console.WriteLine($"Total active sessions: {sessions.Count}");
        Console.WriteLine("ID                               | Name                | In Room | Last Activity");
        Console.WriteLine("----------------------------------|---------------------|---------|-------------------------");
        
        foreach (var session in sessions)
        {
            Console.WriteLine($"{session.Id,-34} | {TruncateString(session.PlayerName, 19),-19} | {(string.IsNullOrEmpty(session.CurrentRoomId) ? "No" : "Yes"),7} | {session.LastActivity.ToLocalTime()}");
        }
    }

    private void ShowStats()
    {
        var sessions = _server.GetAllSessions();
        var rooms = _server.GetAllRooms();
        
        Console.WriteLine("Server Statistics");
        Console.WriteLine("-----------------");
        Console.WriteLine($"Server Uptime: {FormatUptime(DateTime.UtcNow - _server.StartTime)}");
        Console.WriteLine($"Active Sessions: {sessions.Count}");
        Console.WriteLine($"Total Rooms: {rooms.Count}");
        Console.WriteLine($"Active Games: {rooms.Count(r => r.IsActive)}");
        Console.WriteLine($"Players In Rooms: {rooms.Sum(r => r.PlayerCount)}");
    }
    
    private string FormatUptime(TimeSpan timeSpan)
    {
        if (timeSpan.TotalDays >= 1)
        {
            return $"{timeSpan.Days}d {timeSpan.Hours}h {timeSpan.Minutes}m";
        }
        else if (timeSpan.TotalHours >= 1)
        {
            return $"{timeSpan.Hours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";
        }
        else if (timeSpan.TotalMinutes >= 1)
        {
            return $"{timeSpan.Minutes}m {timeSpan.Seconds}s";
        }
        else
        {
            return $"{timeSpan.Seconds}s";
        }
    }

    private Task ShowRecentLogs()
    {
        Console.WriteLine("Recent Server Logs");
        Console.WriteLine("------------------");
        Console.WriteLine("(Database logging integration - logs stored in database)");
        Console.WriteLine("Note: Full log viewing requires database access tools or custom implementation");
        Console.WriteLine();
        Console.WriteLine("Recent console activity is shown above ↑");
        return Task.CompletedTask;
    }

    private string TruncateString(string str, int maxLength)
    {
        if (string.IsNullOrEmpty(str))
            return string.Empty;
            
        return str.Length <= maxLength ? str : str.Substring(0, maxLength - 3) + "...";
    }

    private async Task TestWanConnectivity()
    {
        Console.WriteLine("🧪 Testing WAN connectivity...");
        
        var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConnectionTester>();
        var tester = new ConnectionTester(logger);
        
        var result = await tester.TestWanConnectivity();
        var testIps = new[] { "8.8.8.8", "1.1.1.1", "9.9.9.9" };

        Console.WriteLine("🌍 Checking internet reachability...");
        var pingTasks = testIps.Select(async ip =>
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(ip, 1500);
                var success = reply.Status == System.Net.NetworkInformation.IPStatus.Success;

                Console.WriteLine(success
                    ? $"  {ip}: ✅ {reply.RoundtripTime}ms"
                    : $"  {ip}: ❌ {reply.Status}");

                return (Success: success, RoundtripTime: success ? reply.RoundtripTime : -1L);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {ip}: ❌ {ex.Message}");
                return (Success: false, RoundtripTime: -1L);
            }
        }).ToArray();

        var pingResults = await Task.WhenAll(pingTasks);
        var successfulPings = pingResults.Where(x => x.Success).ToArray();
        var isOnline = successfulPings.Length > 0;

        if (isOnline)
        {
            var avgLatency = successfulPings.Average(x => x.RoundtripTime);
            var minLatency = successfulPings.Min(x => x.RoundtripTime);
            var maxLatency = successfulPings.Max(x => x.RoundtripTime);
            Console.WriteLine($"🌍 Internet check: ONLINE ({successfulPings.Length}/{testIps.Length} hosts reachable)");
            Console.WriteLine($"   Latency — avg: {avgLatency:F0}ms, min: {minLatency}ms, max: {maxLatency}ms");
        }
        else
        {
            Console.WriteLine($"🌍 Internet check: OFFLINE (0/{testIps.Length} hosts reachable)");
        }

        // Keep WAN test strict: requires both WAN test and internet reachability
        result = result && isOnline;
        
        if (result)
        {
            Console.WriteLine("✅ WAN connectivity test PASSED");
        }
        else
        {
            Console.WriteLine("❌ WAN connectivity test FAILED");
            Console.WriteLine("Possible issues:");
            if (!isOnline)
            {
                Console.WriteLine("  • No internet connection detected — all ping targets unreachable");
            }
            Console.WriteLine("  • NAT port forwarding not configured correctly");
            Console.WriteLine("  • Firewall blocking port 443");
            Console.WriteLine("  • ISP blocking port 443");
            Console.WriteLine("  • Server not bound to correct interface");
        }
    }
    
    private async Task TestLanConnectivity()
    {
        Console.WriteLine("🧪 Testing LAN connectivity...");
        
        var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConnectionTester>();
        var tester = new ConnectionTester(logger);
        
        var result = await tester.TestLanConnectivity();
        
        if (result)
        {
            Console.WriteLine("✅ LAN connectivity test PASSED");
        }
        else
        {
            Console.WriteLine("❌ LAN connectivity test FAILED - Check local firewall");
        }
    }
    
    private void ShowNetworkInfo()
    {
        Console.WriteLine("🌐 Network Information:");
        var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsoleUI>();
        NetworkDiagnostics.PrintNetworkInfo(logger);
    }

    private void ShowConfiguration()
    {
        Console.WriteLine("Server Configuration");
        Console.WriteLine("-------------------");
        Console.WriteLine($"TCP Port: 443");
        Console.WriteLine($"UDP Port: 443");
        Console.WriteLine($"TLS Enabled: Yes");
        Console.WriteLine($"Database Logging: Enabled");
        Console.WriteLine($"Log Cleanup Service: Active");
        Console.WriteLine($"Server Start Time: {_server.StartTime.ToLocalTime()}");
        Console.WriteLine($"Process ID: {Environment.ProcessId}");
        Console.WriteLine($"Working Directory: {Environment.CurrentDirectory}");
        Console.WriteLine($".NET Version: {Environment.Version}");
    }

    private Task HandleKickPlayer()
    {
        var sessions = _server.GetAllSessions().ToList(); // Convert to List for indexing
        
        if (sessions.Count == 0)
        {
            Console.WriteLine("No active sessions to kick.");
            return Task.CompletedTask;
        }
        
        Console.WriteLine("Active Sessions:");
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            Console.WriteLine($"{i + 1}. {session.PlayerName} ({session.Id})");
        }
        
        Console.Write("Enter session number to kick (or 'cancel'): ");
        var input = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrEmpty(input) || input.ToLower() == "cancel")
        {
            Console.WriteLine("Kick cancelled.");
            return Task.CompletedTask;
        }
        
        if (int.TryParse(input, out int sessionIndex) && sessionIndex > 0 && sessionIndex <= sessions.Count)
        {
            var sessionToKick = sessions[sessionIndex - 1];
            Console.WriteLine($"Kicking player: {sessionToKick.PlayerName}");
            
            // Note: This would need to be implemented in RacingServer
            // _server.KickPlayer(sessionToKick.Id);
            Console.WriteLine("Note: Kick functionality needs to be implemented in RacingServer class.");
        }
        else
        {
            Console.WriteLine("Invalid selection.");
        }
        return Task.CompletedTask;
    }
}