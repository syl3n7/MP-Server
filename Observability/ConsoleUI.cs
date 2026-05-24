using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MP.Server.Testing;
using MP.Server.Diagnostics;
using MP.Server.Transport;

namespace MP.Server.Observability;

public class ConsoleUI
{
    private readonly GameServer _server;
    private readonly CancellationTokenSource _serverCts;

    public ConsoleUI(GameServer server, CancellationTokenSource serverCts)
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
        foreach (var room in rooms)
        {
            Console.WriteLine($"\n  Room: {room.Name}  [{room.Id[..8]}]  ({room.PlayerCount}/{room.MaxPlayers} players)");
            Console.WriteLine( "  ----------------------------------------------------------------");
            if (room.PlayerCount == 0)
            {
                Console.WriteLine("    (empty)");
            }
            else
            {
                foreach (var p in room.Players)
                    Console.WriteLine($"    {TruncateString(p.Name, 24),-24}  {p.Id[..8]}");
            }
        }
        Console.WriteLine();
    }

    private void ListSessions()
    {
        var sessions = _server.GetAllSessions();
        
        if (sessions.Count == 0)
        {
            Console.WriteLine("No active sessions.");
            return;
        }

        var rooms = _server.GetAllRooms();
        var roomNameById = rooms.ToDictionary(r => r.Id, r => r.Name);

        Console.WriteLine($"Total active sessions: {sessions.Count}");
        Console.WriteLine("Session  | Name                     | Room");
        Console.WriteLine("---------|--------------------------|--------------------------");
        
        foreach (var session in sessions)
        {
            string roomLabel = string.IsNullOrEmpty(session.CurrentRoomId)
                ? "(lobby)"
                : roomNameById.TryGetValue(session.CurrentRoomId, out var rName)
                    ? $"{rName} [{session.CurrentRoomId[..8]}]"
                    : $"[{session.CurrentRoomId[..8]}]";
            Console.WriteLine($"{session.Id[..8],-8} | {TruncateString(session.PlayerName, 24),-24} | {roomLabel}");
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
        bool result = await tester.TestWanConnectivity();
        Console.WriteLine(result ? "✅ WAN connectivity test PASSED" : "❌ WAN connectivity test FAILED — check internet/firewall");
    }
    
    private async Task TestLanConnectivity()
    {
        Console.WriteLine("🧪 Testing LAN connectivity...");
        var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConnectionTester>();
        var tester = new ConnectionTester(logger);
        var gateway = ConnectionTester.GetDefaultGateway();
        if (gateway != null)
            Console.WriteLine($"  Gateway detected: {gateway}");
        bool result = await tester.TestLanConnectivity();
        Console.WriteLine(result ? "✅ LAN connectivity test PASSED" : "❌ LAN connectivity test FAILED — gateway unreachable or not found");
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

    private async Task HandleKickPlayer()
    {
        var sessions = _server.GetAllSessions().ToList();
        
        if (sessions.Count == 0)
        {
            Console.WriteLine("No active sessions to kick.");
            return;
        }
        
        Console.WriteLine("Active Sessions:");
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            Console.WriteLine($"{i + 1}. {session.PlayerName} ({session.Id})");
        }
        
        Console.Write("Enter session number to kick (or 'cancel'): ");
        var input = await Task.Run(() => Console.ReadLine());
        input = input?.Trim();
        
        if (string.IsNullOrEmpty(input) || input.ToLower() == "cancel")
        {
            Console.WriteLine("Kick cancelled.");
            return;
        }
        
        if (int.TryParse(input, out int sessionIndex) && sessionIndex > 0 && sessionIndex <= sessions.Count)
        {
            var sessionToKick = sessions[sessionIndex - 1];
            Console.WriteLine($"Kicking player: {sessionToKick.PlayerName}");
            _server.SecurityManager.KickCallback?.Invoke(sessionToKick.Id);
            Console.WriteLine($"Kicked {sessionToKick.PlayerName}.");
        }
        else
        {
            Console.WriteLine("Invalid selection.");
        }
    }
}
