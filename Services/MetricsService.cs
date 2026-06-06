using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using MP.Server.Transport;

namespace MP.Server.Services;

/// <summary>
/// Point-in-time snapshot of server performance metrics.
/// </summary>
public sealed record MetricsSnapshot(
    DateTime Timestamp,
    double   CpuPercent,
    double   WorkingSetMb,
    double   GcHeapMb,
    double   GcAllocatedMb,
    int      Gc0,
    int      Gc1,
    int      Gc2,
    long     BytesSentPerSec,
    long     BytesReceivedPerSec,
    double   AvgRttMs,
    double   AvgJitterMs,
    int      PlayerCount,
    double   MaxRttMs,
    double   MinRttMs,
    double   MaxJitterMs
);

/// <summary>
/// Background singleton that samples CPU, RAM, GC pressure, and network bandwidth
/// every 5 seconds and keeps a 25-minute rolling history.
/// Also writes each sample to logs/custom_server_metrics.csv for offline analysis
/// in the same column format as the ENet enet_server_metrics.csv.
/// </summary>
public sealed class MetricsService : BackgroundService
{
    private const int HistorySize = 300; // 300 × 5 s = 25 min

    private readonly MetricsSnapshot[] _ring = new MetricsSnapshot[HistorySize];
    private int  _head;
    private int  _count;
    private readonly object _lock = new();

    private readonly Process  _process = Process.GetCurrentProcess();
    private TimeSpan  _lastCpuTime;
    private DateTime  _lastCpuMeasure;

    // Bandwidth state
    private readonly GameServer _gameServer;
    private long _prevSent;
    private long _prevReceived;
    private long _prevBwTicks;

    // Run identity — regenerated on Reset() so CSV rows are tagged per experiment
    public string RunTag { get; private set; } =
        DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

    private readonly DateTime _serviceStartTime = DateTime.UtcNow;

    // CSV path mirrors the Serilog log directory (relative to CWD)
    private const string CsvDir  = "logs";
    private const string CsvFile = "custom_server_metrics.csv";
    public  string CsvPath => Path.Combine(CsvDir, CsvFile);

    private volatile MetricsSnapshot _current =
        new(DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public MetricsSnapshot Current => _current;

    public MetricsService(GameServer gameServer)
    {
        _gameServer = gameServer;
    }

    /// <summary>Returns up to 300 snapshots ordered oldest → newest.</summary>
    public IReadOnlyList<MetricsSnapshot> GetHistory()
    {
        lock (_lock)
        {
            var result = new MetricsSnapshot[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _ring[(_head - _count + i + HistorySize) % HistorySize];
            return result;
        }
    }

    /// <summary>
    /// Starts a new run tag so subsequent CSV rows are marked as a fresh experiment.
    /// Existing CSV data and the in-memory ring buffer are preserved.
    /// </summary>
    public void Reset()
    {
        RunTag = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime baselines before first delta
        _process.Refresh();
        _lastCpuTime    = _process.TotalProcessorTime;
        _lastCpuMeasure = DateTime.UtcNow;
        _prevSent       = _gameServer.TotalBytesSent;
        _prevReceived   = _gameServer.TotalBytesReceived;
        _prevBwTicks    = Stopwatch.GetTimestamp();

        // Write CSV header if the file does not exist yet
        Directory.CreateDirectory(CsvDir);
        if (!File.Exists(CsvPath))
            await File.WriteAllTextAsync(CsvPath,
                "timestamp_utc,run_tag,solution,uptime_s,player_count," +
                "avg_rtt_ms,max_rtt_ms,min_rtt_ms,avg_jitter_ms,max_jitter_ms," +
                "avg_packet_loss_pct,bytes_sent_delta,bytes_recv_delta," +
                "ram_bytes,gc_heap_bytes,gc_gen0_delta,gc_gen1_delta,gc_gen2_delta,cpu_pct\n",
                stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(5_000, stoppingToken).ConfigureAwait(false);
            await SampleAsync().ConfigureAwait(false);
        }
    }

    private async Task SampleAsync()
    {
        _process.Refresh();
        var now = DateTime.UtcNow;

        // ── CPU ───────────────────────────────────────────────────────────────
        var cpuNow   = _process.TotalProcessorTime;
        var elapsed  = (now - _lastCpuMeasure).TotalSeconds;
        var cpuPct   = elapsed > 0
            ? Math.Min(100.0, Math.Round(
                (cpuNow - _lastCpuTime).TotalSeconds / (Environment.ProcessorCount * elapsed) * 100.0, 1))
            : 0;
        _lastCpuTime    = cpuNow;
        _lastCpuMeasure = now;

        // ── Memory ────────────────────────────────────────────────────────────
        const double MiB = 1_048_576.0;
        var workingSetMb    = Math.Round(_process.WorkingSet64    / MiB, 1);
        var gcHeapMb        = Math.Round(GC.GetTotalMemory(false) / MiB, 1);
        var gcAllocatedMb   = Math.Round(GC.GetTotalAllocatedBytes() / MiB, 1);

        // ── GC collections ────────────────────────────────────────────────────
        var prevSnap = _current;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var gc0Delta = Math.Max(0, gc0 - prevSnap.Gc0);
        var gc1Delta = Math.Max(0, gc1 - prevSnap.Gc1);
        var gc2Delta = Math.Max(0, gc2 - prevSnap.Gc2);

        // ── Bandwidth ─────────────────────────────────────────────────────────
        var nowTicks      = Stopwatch.GetTimestamp();
        var secs          = (double)(nowTicks - _prevBwTicks) / Stopwatch.Frequency;
        var totalSent     = _gameServer.TotalBytesSent;
        var totalReceived = _gameServer.TotalBytesReceived;

        long bytesSentPerSec = 0, bytesReceivedPerSec = 0;
        long bytesSentDelta  = 0, bytesReceivedDelta  = 0;
        if (secs > 0)
        {
            bytesSentDelta      = Math.Max(0, totalSent     - _prevSent);
            bytesReceivedDelta  = Math.Max(0, totalReceived - _prevReceived);
            bytesSentPerSec     = (long)(bytesSentDelta    / secs);
            bytesReceivedPerSec = (long)(bytesReceivedDelta / secs);
        }
        _prevSent     = totalSent;
        _prevReceived = totalReceived;
        _prevBwTicks  = nowTicks;

        // ── RTT / Jitter ──────────────────────────────────────────────────────
        var allSessions = _gameServer.GetAllSessions();
        var rttSamples  = allSessions.Where(s => s.LastRttMs > 0).Select(s => s.LastRttMs).ToList();
        var jitSamples  = allSessions.Where(s => s.JitterMs  > 0).Select(s => s.JitterMs).ToList();
        var playerCount = rttSamples.Count;
        var avgRttMs    = rttSamples.Count > 0 ? Math.Round(rttSamples.Average(), 1) : 0.0;
        var maxRttMs    = rttSamples.Count > 0 ? Math.Round(rttSamples.Max(),     1) : 0.0;
        var minRttMs    = rttSamples.Count > 0 ? Math.Round(rttSamples.Min(),     1) : 0.0;
        var avgJitterMs = jitSamples.Count > 0 ? Math.Round(jitSamples.Average(), 1) : 0.0;
        var maxJitterMs = jitSamples.Count > 0 ? Math.Round(jitSamples.Max(),     1) : 0.0;

        // ── Store snapshot ────────────────────────────────────────────────────
        var snap = new MetricsSnapshot(
            now, cpuPct, workingSetMb, gcHeapMb, gcAllocatedMb,
            gc0, gc1, gc2, bytesSentPerSec, bytesReceivedPerSec,
            avgRttMs, avgJitterMs, playerCount, maxRttMs, minRttMs, maxJitterMs);

        lock (_lock)
        {
            _ring[_head % HistorySize] = snap;
            _head++;
            if (_count < HistorySize) _count++;
        }

        _current = snap;

        // ── Write CSV row (ENet-compatible format) ────────────────────────────
        var uptimeSecs = Math.Round((now - _serviceStartTime).TotalSeconds, 2);
        var ramBytes   = _process.WorkingSet64;
        var gcBytes    = GC.GetTotalMemory(false);

        var row = new StringBuilder(256);
        row.Append(now.ToString("o")).Append(',')
           .Append(RunTag).Append(',')
           .Append("custom").Append(',')
           .Append(uptimeSecs).Append(',')
           .Append(playerCount).Append(',')
           .Append(avgRttMs).Append(',')
           .Append(maxRttMs).Append(',')
           .Append(minRttMs).Append(',')
           .Append(avgJitterMs).Append(',')
           .Append(maxJitterMs).Append(',')
           .Append('0').Append(',')          // avg_packet_loss_pct — not tracked
           .Append(bytesSentDelta).Append(',')
           .Append(bytesReceivedDelta).Append(',')
           .Append(ramBytes).Append(',')
           .Append(gcBytes).Append(',')
           .Append(gc0Delta).Append(',')
           .Append(gc1Delta).Append(',')
           .Append(gc2Delta).Append(',')
           .Append(cpuPct)
           .Append('\n');

        try
        {
            await File.AppendAllTextAsync(CsvPath, row.ToString()).ConfigureAwait(false);
        }
        catch { /* don't crash the metrics service on IO errors */ }
    }
}
