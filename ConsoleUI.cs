using System;
using System.Threading;
using System.Threading.Tasks;

public class ConsoleUI
{
    private readonly RacingServer _server;
    private readonly CancellationTokenSource _cts;
    
    public ConsoleUI(RacingServer server, CancellationTokenSource cts)
    {
        _server = server;
        _cts = cts;
    }
    
    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("Racing Server Console UI");
        Console.WriteLine("------------------------");
        PrintHelp();
        
        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var command = await Task.Run(() => Console.ReadLine(), ct);
            
            if (string.IsNullOrEmpty(command))
                continue;
                
            await ProcessCommandAsync(command, ct);
        }
    }
    
    private async Task ProcessCommandAsync(string command, CancellationToken ct)
    {
        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = args[0].ToLower();
        
        switch (cmd)
        {
            case "help":
                PrintHelp();
                break;
                
            case "stats":
                PrintStats();
                break;
                
            case "rooms":
                PrintRooms();
                break;
                
            case "players":
                PrintPlayers();
                break;
                
            case "cleardead":
                // Not implemented yet
                Console.WriteLine("Not implemented");
                break;
                
            case "quit":
            case "exit":
                Console.WriteLine("Shutting down server...");
                _cts.Cancel();
                break;
                
            default:
                Console.WriteLine("Unknown command. Type 'help' for a list of commands.");
                break;
        }
    }
    
    private void PrintHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  help      - Show this help message");
        Console.WriteLine("  stats     - Show server statistics");
        Console.WriteLine("  rooms     - List all active rooms");
        Console.WriteLine("  players   - List all connected players");
        Console.WriteLine("  cleardead - Remove inactive rooms");
        Console.WriteLine("  exit      - Shutdown the server");
    }
    
    private void PrintStats()
    {
        var allRooms = _server.GetAllRooms();
        var activeRooms = _server.GetActiveRooms();
        
        int totalPlayers = 0;
        foreach (var room in allRooms)
        {
            totalPlayers += room.PlayerCount;
        }
        
        Console.WriteLine($"Server Statistics:");
        Console.WriteLine($"- Total rooms: {allRooms.Count}");
        Console.WriteLine($"- Active rooms: {activeRooms.Count}");
        Console.WriteLine($"- Connected players: {totalPlayers}");
    }
    
    private void PrintRooms()
    {
        var rooms = _server.GetAllRooms();
        
        if (rooms.Count == 0)
        {
            Console.WriteLine("No rooms available.");
            return;
        }
        
        Console.WriteLine("Room List:");
        Console.WriteLine("-------------------------------------------------------------------------");
        Console.WriteLine("| ID                 | Name             | Players | Status  | Host     |");
        Console.WriteLine("-------------------------------------------------------------------------");
        
        foreach (var room in rooms)
        {
            var status = room.IsActive ? "Active" : "Waiting";
            Console.WriteLine($"| {room.Id.PadRight(18)} | {room.Name.PadRight(16)} | {room.PlayerCount,7} | {status,-7} | {room.HostId?.Substring(0, 6) ?? "None"} |");
        }
        
        Console.WriteLine("-------------------------------------------------------------------------");
    }
    
    private void PrintPlayers()
    {
        var rooms = _server.GetAllRooms();
        int totalPlayers = 0;
        
        Console.WriteLine("Connected Players:");
        Console.WriteLine("----------------------------------------------------------------");
        Console.WriteLine("| ID                 | Name             | Room             |");
        Console.WriteLine("----------------------------------------------------------------");
        
        foreach (var room in rooms)
        {
            foreach (var player in room.Players)
            {
                Console.WriteLine($"| {player.Id.PadRight(18)} | {player.Name.PadRight(16)} | {room.Name.PadRight(16)} |");
                totalPlayers++;
            }
        }
        
        if (totalPlayers == 0)
        {
            Console.WriteLine("| No players currently connected                              |");
        }
        
        Console.WriteLine("----------------------------------------------------------------");
    }
}