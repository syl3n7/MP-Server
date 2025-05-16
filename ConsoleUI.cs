using System;
using System.Threading;
using System.Threading.Tasks;

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
        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var command = await Task.Run(() => Console.ReadLine(), ct);
            
            if (string.IsNullOrEmpty(command))
                continue;

            switch (command.ToLower())
            {
                case "help":
                    PrintHelp();
                    break;
                
                case "status":
                    PrintStatus();
                    break;
                
                case "rooms":
                    PrintRooms();
                    break;
                
                case "exit":
                case "quit":
                    Console.WriteLine("Shutting down server...");
                    _serverCts.Cancel();
                    return;
                
                default:
                    Console.WriteLine($"Unknown command: {command}");
                    PrintHelp();
                    break;
            }
        }
    }

    private void PrintHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  help   - Show this help message");
        Console.WriteLine("  status - Show server status");
        Console.WriteLine("  rooms  - List active game rooms");
        Console.WriteLine("  exit   - Shutdown the server");
    }

    private void PrintStatus()
    {
        var rooms = _server.GetActiveRooms();
        Console.WriteLine($"Server Status:");
        Console.WriteLine($"  Active Rooms: {rooms.Count}");
        Console.WriteLine($"  TCP Port: 7777");
        Console.WriteLine($"  UDP Port: 7778");
    }

    private void PrintRooms()
    {
        var rooms = _server.GetActiveRooms();
        Console.WriteLine($"Active Game Rooms ({rooms.Count}):");
        
        if (rooms.Count == 0)
        {
            Console.WriteLine("  No active rooms");
            return;
        }

        foreach (var room in rooms)
        {
            Console.WriteLine($"  - {room.Name} (ID: {room.Id})");
            Console.WriteLine($"    Players: {room.PlayerCount}/{room.MaxPlayers}");
            Console.WriteLine($"    Host: {room.HostId}");
            Console.WriteLine($"    Created: {room.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
    }
}