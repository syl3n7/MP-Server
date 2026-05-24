using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    double   AvgJitterMs
);

/// <summary>
/// Background singleton that samples CPU, RAM, GC pressure, and network bandwidth
/// every 5 seconds and keeps a 5-minute rolling history.
/// </summary>
public sealed class MetricsService : BackgroundService
{
    private const int HistorySize = 60; // 60 × 5 s = 5 min

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

    private volatile MetricsSnapshot _current =
        new(DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public MetricsSnapshot Current => _current;

    public MetricsService(GameServer gameServer)
    {
        _gameServer = gameServer;
    }

    /// <summary>Returns up to 60 snapshots ordered oldest → newest.</summary>
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime baselines before first delta
        _process.Refresh();
        _lastCpuTime    = _process.TotalProcessorTime;
        _lastCpuMeasure = DateTime.UtcNow;
        _prevSent       = _gameServer.TotalBytesSent;
        _prevReceived   = _gameServer.TotalBytesReceived;
        _prevBwTicks    = Stopwatch.GetTimestamp();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(5_000, stoppingToken).ConfigureAwait(false);
            Sample();
        }
    }

    private void Sample()
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
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);

        // ── Bandwidth ─────────────────────────────────────────────────────────
        var nowTicks      = Stopwatch.GetTimestamp();
        var secs          = (double)(nowTicks - _prevBwTicks) / Stopwatch.Frequency;
        var totalSent     = _gameServer.TotalBytesSent;
        var totalReceived = _gameServer.TotalBytesReceived;

        long bytesSentPerSec = 0, bytesReceivedPerSec = 0;
        if (secs > 0)
        {
            bytesSentPerSec     = (long)Math.Max(0, (totalSent     - _prevSent)     / secs);
            bytesReceivedPerSec = (long)Math.Max(0, (totalReceived - _prevReceived) / secs);
        }
        _prevSent     = totalSent;
        _prevReceived = totalReceived;
        _prevBwTicks  = nowTicks;

        // ── RTT / Jitter ──────────────────────────────────────────────────────
        var allSessions = _gameServer.GetAllSessions();
        var rttSamples  = allSessions.Where(s => s.LastRttMs > 0).Select(s => s.LastRttMs).ToList();
        var jitSamples  = allSessions.Where(s => s.JitterMs  > 0).Select(s => s.JitterMs).ToList();
        var avgRttMs    = rttSamples.Count > 0 ? Math.Round(rttSamples.Average(), 1) : 0.0;
        var avgJitterMs = jitSamples.Count > 0 ? Math.Round(jitSamples.Average(), 1) : 0.0;

        // ── Store snapshot ────────────────────────────────────────────────────
        var snap = new MetricsSnapshot(
            now, cpuPct, workingSetMb, gcHeapMb, gcAllocatedMb,
            gc0, gc1, gc2, bytesSentPerSec, bytesReceivedPerSec,
            avgRttMs, avgJitterMs);

        lock (_lock)
        {
            _ring[_head % HistorySize] = snap;
            _head++;
            if (_count < HistorySize) _count++;
        }

        _current = snap;
    }
}
