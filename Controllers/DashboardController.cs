using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MP.Server.Data;
using MP.Server.Models;
using MP.Server.Transport;

namespace MP.Server.Controllers;

// ─── View-models ─────────────────────────────────────────────────────────────

public class DashboardViewModel
{
    // Server identity
    public DateTime StartTime  { get; set; }
    public TimeSpan Uptime     => DateTime.UtcNow - StartTime;
    public int      TcpPort    { get; set; }
    public int      UdpPort    { get; set; }
    public bool     UseTls     { get; set; }
    public string   PublicIp   { get; set; } = "";
    public string   Hostname   { get; set; } = "";
    public bool     IsOnline   { get; set; }

    // Live data
    public List<SessionViewModel> ActiveSessions { get; set; } = new();
    public List<RoomViewModel>    ActiveRooms    { get; set; } = new();

    // User stats
    public int TotalUsers  { get; set; }
    public int ActiveUsers { get; set; }
    public int BannedUsers { get; set; }
    public int LockedUsers { get; set; }
    public int TodayLogins { get; set; }

    // Security
    public int SecurityEventsToday     { get; set; }
    public int UnresolvedSecurityEvents { get; set; }
    public int CriticalSecurityEvents  { get; set; }
    public List<SecurityLog> RecentSecurityEvents { get; set; } = new();

    // Logs
    public List<ServerLog>     RecentServerLogs  { get; set; } = new();
    public List<ConnectionLog> RecentConnections { get; set; } = new();

    // 24 h connection stats
    public int ConnectionsLast24h    { get; set; }
    public int TlsConnectionsLast24h { get; set; }
    public int DisconnectionsLast24h { get; set; }

    // 24 h log counts
    public int ErrorLogsToday   { get; set; }
    public int WarningLogsToday { get; set; }

    // Busiest room
    public string? BusiestRoomName  { get; set; }
    public int     BusiestRoomCount { get; set; }
}

public class SessionViewModel
{
    public string    Id              { get; set; } = "";
    public string    PlayerName      { get; set; } = "";
    public bool      IsAuthenticated { get; set; }
    public string?   CurrentRoomId   { get; set; }
    public DateTime  LastActivity    { get; set; }
}

public class RoomViewModel
{
    public string    Id          { get; set; } = "";
    public string    Name        { get; set; } = "";
    public int       PlayerCount { get; set; }
    public int       MaxPlayers  { get; set; }
    public DateTime  CreatedAt   { get; set; }
    public string?   HostId      { get; set; }
    public bool      IsActive    { get; set; }
}

// ─── Controller ──────────────────────────────────────────────────────────────

public class DashboardController : Controller
{
    private readonly GameServer                    _gameServer;
    private readonly IDbContextFactory<UserDbContext> _dbFactory;

    public DashboardController(GameServer gameServer, IDbContextFactory<UserDbContext> dbFactory)
    {
        _gameServer = gameServer;
        _dbFactory  = dbFactory;
    }

    // GET /Dashboard
    public async Task<IActionResult> Index()
    {
        var model = new DashboardViewModel
        {
            StartTime = _gameServer.StartTime,
            TcpPort   = _gameServer.TcpPort,
            UdpPort   = _gameServer.UdpPort,
            UseTls    = _gameServer.UseTls,
            PublicIp  = _gameServer.PublicIp,
            Hostname  = _gameServer.Hostname,
            IsOnline  = _gameServer.StartTime != default,
        };

        // ── Live data from the game server ────────────────────────────────────
        model.ActiveSessions = _gameServer.GetAllSessions()
            .Select(s => new SessionViewModel
            {
                Id              = s.Id,
                PlayerName      = s.PlayerName,
                IsAuthenticated = s.IsAuthenticated,
                CurrentRoomId   = s.CurrentRoomId,
                LastActivity    = s.LastActivity,
            })
            .OrderByDescending(s => s.LastActivity)
            .ToList();

        model.ActiveRooms = _gameServer.GetActiveRooms()
            .Select(r => new RoomViewModel
            {
                Id          = r.Id,
                Name        = r.Name,
                PlayerCount = r.Players.Count,
                MaxPlayers  = r.MaxPlayers,
                CreatedAt   = r.CreatedAt,
                HostId      = r.HostId,
                IsActive    = r.IsActive,
            })
            .ToList();

        var busiest = model.ActiveRooms.MaxBy(r => r.PlayerCount);
        if (busiest?.PlayerCount > 0)
        {
            model.BusiestRoomName  = busiest.Name;
            model.BusiestRoomCount = busiest.PlayerCount;
        }

        // ── Database stats ────────────────────────────────────────────────────
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var today   = DateTime.UtcNow.Date;
            var since24h = DateTime.UtcNow.AddHours(-24);

            // Users
            model.TotalUsers  = await db.Users.CountAsync();
            model.ActiveUsers = await db.Users.CountAsync(u => u.IsActive && !u.IsBanned);
            model.BannedUsers = await db.Users.CountAsync(u => u.IsBanned);
            model.LockedUsers = await db.Users.CountAsync(u => u.LockedUntil != null && u.LockedUntil > DateTime.UtcNow);
            model.TodayLogins = await db.LoginAuditLogs.CountAsync(l => l.Timestamp >= today && l.Success);

            // Security
            model.SecurityEventsToday      = await db.SecurityLogs.CountAsync(l => l.Timestamp >= today);
            model.UnresolvedSecurityEvents = await db.SecurityLogs.CountAsync(l => !l.IsResolved);
            model.CriticalSecurityEvents   = await db.SecurityLogs.CountAsync(l => l.Severity >= 3 && !l.IsResolved);
            model.RecentSecurityEvents     = await db.SecurityLogs
                .OrderByDescending(l => l.Timestamp).Take(10).ToListAsync();

            // Logs
            model.ErrorLogsToday   = await db.ServerLogs.CountAsync(l => l.Timestamp >= today && l.Level == "Error");
            model.WarningLogsToday = await db.ServerLogs.CountAsync(l => l.Timestamp >= today && l.Level == "Warning");
            model.RecentServerLogs = await db.ServerLogs
                .OrderByDescending(l => l.Timestamp).Take(20).ToListAsync();

            // Connections
            model.ConnectionsLast24h    = await db.ConnectionLogs
                .CountAsync(l => l.Timestamp >= since24h && l.EventType == "Connect");
            model.TlsConnectionsLast24h = await db.ConnectionLogs
                .CountAsync(l => l.Timestamp >= since24h && l.EventType == "Connect" && l.UsedTls);
            model.DisconnectionsLast24h = await db.ConnectionLogs
                .CountAsync(l => l.Timestamp >= since24h && l.EventType == "Disconnect");
            model.RecentConnections = await db.ConnectionLogs
                .OrderByDescending(l => l.Timestamp).Take(15).ToListAsync();
        }
        catch
        {
            // DB unavailable – show live data only; no crash.
        }

        return View(model);
    }

    // GET /Dashboard/Stats  (JSON endpoint for live stat counters)
    [HttpGet]
    public IActionResult Stats()
    {
        var sessions = _gameServer.GetAllSessions();
        var rooms    = _gameServer.GetActiveRooms();
        var uptime   = DateTime.UtcNow - _gameServer.StartTime;

        return Json(new
        {
            sessions = sessions.Count,
            rooms    = rooms.Count,
            uptime   = FormatUptime(uptime),
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1)  return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
        return $"{t.Minutes}m {t.Seconds}s";
    }
}
