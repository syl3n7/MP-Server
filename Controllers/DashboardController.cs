using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MP.Server.Data;
using MP.Server.Models.Dtos;
using MP.Server.Services;
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
    public List<SecurityLogDto> RecentSecurityEvents { get; set; } = new();

    // Logs
    public List<ServerLogDto>     RecentServerLogs  { get; set; } = new();
    public List<ConnectionLogDto> RecentConnections { get; set; } = new();

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
    public double    LastRttMs       { get; set; }
    public double    JitterMs        { get; set; }
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
    private readonly GameServer                       _gameServer;
    private readonly IDbContextFactory<UserDbContext> _dbFactory;
    private readonly DatabaseLoggingService           _loggingService;
    private readonly MetricsService                   _metrics;

    public DashboardController(
        GameServer gameServer,
        IDbContextFactory<UserDbContext> dbFactory,
        DatabaseLoggingService loggingService,
        MetricsService metrics)
    {
        _gameServer     = gameServer;
        _dbFactory      = dbFactory;
        _loggingService = loggingService;
        _metrics        = metrics;
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
                LastRttMs       = s.LastRttMs,
                JitterMs        = s.JitterMs,
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
            model.RecentSecurityEvents = await db.SecurityLogs
                .OrderByDescending(l => l.Timestamp).Take(10)
                .Select(l => new SecurityLogDto(l.Timestamp, l.EventType, l.IpAddress, l.Severity, l.IsResolved))
                .ToListAsync();

            // Logs
            model.ErrorLogsToday   = await db.ServerLogs.CountAsync(l => l.Timestamp >= today && l.Level == "Error");
            model.WarningLogsToday = await db.ServerLogs.CountAsync(l => l.Timestamp >= today && l.Level == "Warning");
            model.RecentServerLogs = await db.ServerLogs
                .OrderByDescending(l => l.Timestamp).Take(20)
                .Select(l => new ServerLogDto(l.Timestamp, l.Level, l.Category, l.Message))
                .ToListAsync();

            // Connections
            model.ConnectionsLast24h    = await db.ConnectionLogs
                .CountAsync(l => l.Timestamp >= since24h && l.EventType == "Connect");
            model.TlsConnectionsLast24h = await db.ConnectionLogs
                .CountAsync(l => l.Timestamp >= since24h && l.EventType == "Connect" && l.UsedTls);
            model.DisconnectionsLast24h = await db.ConnectionLogs
                .CountAsync(l => l.Timestamp >= since24h && l.EventType == "Disconnect");
            model.RecentConnections = await db.ConnectionLogs
                .OrderByDescending(l => l.Timestamp).Take(15)
                .Select(l => new ConnectionLogDto(l.Timestamp, l.EventType, l.IpAddress, l.PlayerName, l.UsedTls))
                .ToListAsync();
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

    // GET /Dashboard/Metrics  (JSON endpoint for live performance metrics)
    [HttpGet]
    public IActionResult Metrics()
    {
        var snap = _metrics.Current;
        return Json(new
        {
            cpuPercent          = snap.CpuPercent,
            workingSetMb        = snap.WorkingSetMb,
            gcHeapMb            = snap.GcHeapMb,
            gcAllocatedMb       = snap.GcAllocatedMb,
            gc0                 = snap.Gc0,
            gc1                 = snap.Gc1,
            gc2                 = snap.Gc2,
            bytesSentPerSec     = snap.BytesSentPerSec,
            bytesReceivedPerSec = snap.BytesReceivedPerSec,
            avgRttMs            = snap.AvgRttMs,
            avgJitterMs         = snap.AvgJitterMs,
        });
    }

    // GET /Dashboard/Sessions  (JSON endpoint for live sessions table)
    [HttpGet]
    public IActionResult Sessions()
    {
        var sessions = _gameServer.GetAllSessions()
            .OrderByDescending(s => s.LastActivity)
            .Select(s => new
            {
                id              = s.Id,
                playerName      = s.PlayerName,
                isAuthenticated = s.IsAuthenticated,
                currentRoomId   = s.CurrentRoomId,
                lastActivity    = s.LastActivity.ToString("HH:mm:ss"),
                lastRttMs       = s.LastRttMs,
                jitterMs        = s.JitterMs,
            });
        return Json(sessions);
    }

    // GET /Dashboard/MetricsHistory  (returns full 25-min ring buffer for export/analysis)
    [HttpGet]
    public IActionResult MetricsHistory()
    {
        var history = _metrics.GetHistory().Select(s => new
        {
            timestamp           = s.Timestamp.ToString("o"),
            cpuPercent          = s.CpuPercent,
            workingSetMb        = s.WorkingSetMb,
            gcHeapMb            = s.GcHeapMb,
            gcAllocatedMb       = s.GcAllocatedMb,
            gc0                 = s.Gc0,
            gc1                 = s.Gc1,
            gc2                 = s.Gc2,
            bytesSentPerSec     = s.BytesSentPerSec,
            bytesReceivedPerSec = s.BytesReceivedPerSec,
            avgRttMs            = s.AvgRttMs,
            avgJitterMs         = s.AvgJitterMs,
            playerCount         = s.PlayerCount,
            maxRttMs            = s.MaxRttMs,
            minRttMs            = s.MinRttMs,
            maxJitterMs         = s.MaxJitterMs,
        });
        return Json(history);
    }

    // POST /Dashboard/ResetMetrics  (starts a new run tag for CSV export)
    [HttpPost]
    public IActionResult ResetMetrics()
    {
        _metrics.Reset();
        return Json(new { ok = true, runTag = _metrics.RunTag });
    }

    // POST /Dashboard/WipeDatabase  (deletes all rows from all tables — test reset only)
    [HttpPost]
    public async Task<IActionResult> WipeDatabase()
    {
        try
        {
            var counts = await _loggingService.WipeAllDataAsync();
            return Json(new { ok = true, deleted = counts });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

    // GET /Dashboard/ExportLogs?type=connection&format=json&from=2026-05-01&to=2026-05-22
    [HttpGet]
    public async Task<IActionResult> ExportLogs(
        [FromQuery] string   type   = "connection",
        [FromQuery] string   format = "json",
        [FromQuery] DateTime? from  = null,
        [FromQuery] DateTime? to    = null)
    {
        object data;
        try
        {
            data = await _loggingService.ExportLogsAsync(type, from, to);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, validTypes = new[] { "server", "connection", "security", "room" } });
        }

        var fileName = $"logs_{type}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var json    = JsonSerializer.SerializeToUtf8Bytes(data);
            using var doc = JsonDocument.Parse(json);
            var csv     = BuildCsv(doc.RootElement);
            var bytes   = Encoding.UTF8.GetBytes(csv);
            return File(bytes, "text/csv", $"{fileName}.csv");
        }

        return File(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })),
            "application/json",
            $"{fileName}.json");
    }

    private static string BuildCsv(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            return string.Empty;

        var sb      = new StringBuilder();
        var headers = new List<string>();

        // Derive column headers from the first object
        foreach (var prop in array[0].EnumerateObject())
            headers.Add(prop.Name);

        sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));

        foreach (var row in array.EnumerateArray())
        {
            var values = headers.Select(h =>
            {
                if (row.TryGetProperty(h, out var val))
                    return CsvEscape(val.ToString());
                return "";
            });
            sb.AppendLine(string.Join(",", values));
        }

        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{ value.Replace("\"", "\"\"") }\"";
        return value;
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1)  return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
        return $"{t.Minutes}m {t.Seconds}s";
    }
}
