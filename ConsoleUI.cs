using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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
        Console.WriteLine("Racing Server Console");
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
                    
                case "dashboard":
                    ShowDashboardInfo();
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
        Console.WriteLine("  help      - Show this help message");
        Console.WriteLine("  rooms     - List all game rooms");
        Console.WriteLine("  sessions  - List all active player sessions");
        Console.WriteLine("  stats     - Show server statistics");
        Console.WriteLine("  dashboard - Show web dashboard information");
        Console.WriteLine("  quit      - Shut down the server");
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
        Console.WriteLine($"Server Uptime: {DateTime.UtcNow - _server.StartTime}");
        Console.WriteLine($"Active Sessions: {sessions.Count}");
        Console.WriteLine($"Total Rooms: {rooms.Count}");
        Console.WriteLine($"Active Games: {rooms.Count(r => r.IsActive)}");
        Console.WriteLine($"Players In Rooms: {rooms.Sum(r => r.PlayerCount)}");
    }

    private void ShowDashboardInfo()
    {
        Console.WriteLine("Web Dashboard Information");
        Console.WriteLine("------------------------");
        Console.WriteLine("Dashboard URL: http://localhost:8080");
        Console.WriteLine("");
        Console.WriteLine("The web dashboard provides a graphical interface to monitor:");
        Console.WriteLine("- Server statistics (uptime, active sessions, rooms)");
        Console.WriteLine("- Active game rooms and their status");
        Console.WriteLine("- Connected player sessions");
        Console.WriteLine("");
        Console.WriteLine("The dashboard refreshes automatically every 10 seconds");
        Console.WriteLine("or you can click the Refresh button for immediate updates.");
    }

    private string TruncateString(string str, int maxLength)
    {
        if (string.IsNullOrEmpty(str))
            return string.Empty;
            
        return str.Length <= maxLength ? str : str.Substring(0, maxLength - 3) + "...";
    }
}