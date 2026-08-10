using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Multi-dimensional rate limiter for sync operations.
/// Controls bandwidth (token bucket), CPU utilization, and disk I/O pacing.
/// Supports auto-adaptive mode that adjusts limits based on system load.
/// </summary>
public sealed class SyncRateLimiter : IDisposable
{
    private EffectiveLimits _limits;
    private readonly object _lock = new();
    private volatile bool _disposed;

    // Bandwidth token bucket
    private double _bandwidthTokens;
    private long _lastBandwidthRefill;

    // Disk I/O token bucket
    private double _diskTokens;
    private long _lastDiskRefill;

    // CPU monitoring
    private readonly Process _process;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuCheck;
    private double _currentCpuPercent;

    // Disk throughput tracking
    private long _diskBytesThisSecond;
    private long _lastDiskThroughputReset;

    // IOPS tracking
    private int _iopsThisSecond;
    private long _lastIopsReset;

    // Auto-adaptive state
    private double _adaptiveBandwidthFactor = 1.0;

    // Stats
    public long TotalBytesThrottled { get; private set; }
    public long TotalThrottleDelayMs { get; private set; }
    public long TotalOperationsThrottled { get; private set; }

    public SyncRateLimiter(EffectiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _process = Process.GetCurrentProcess();
        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuCheck = DateTime.UtcNow;
        _lastBandwidthRefill = Stopwatch.GetTimestamp();
        _lastDiskRefill = Stopwatch.GetTimestamp();
        _lastDiskThroughputReset = Stopwatch.GetTimestamp();
        _lastIopsReset = Stopwatch.GetTimestamp();

        _bandwidthTokens = limits.MaxBandwidthBytesPerSec;
        _diskTokens = limits.MaxDiskBytesPerSec;
    }

    /// <summary>
    /// Update the effective limits (e.g., when user changes profile).
    /// </summary>
    public void UpdateLimits(EffectiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            _limits = limits;
            _bandwidthTokens = Math.Min(_bandwidthTokens, limits.MaxBandwidthBytesPerSec);
            _diskTokens = Math.Min(_diskTokens, limits.MaxDiskBytesPerSec);
        }
    }

    /// <summary>
    /// Wait until bandwidth tokens are available, then consume them.
    /// Returns the actual delay applied in milliseconds.
    /// Zero-alloc fast path: non-async when tokens are immediately available.
    /// </summary>
    public ValueTask<long> ThrottleBandwidthAsync(long bytes, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes <= 0) return default;
        if (ct.IsCancellationRequested) return new ValueTask<long>(Task.FromCanceled<long>(ct));

        RefillTokens();

        // Single lock: check + consume atomically (no TOCTOU race)
        lock (_lock)
        {
            if (_bandwidthTokens >= bytes)
            {
                _bandwidthTokens -= bytes;
                TotalBytesThrottled += bytes;
                return default;
            }
        }

        // Slow path: need to wait for token refill
        return ThrottleBandwidthAsyncSlow(bytes, ct);
    }

    private async ValueTask<long> ThrottleBandwidthAsyncSlow(long bytes, CancellationToken ct)
    {
        long totalDelay = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RefillTokens();

            lock (_lock)
            {
                if (_bandwidthTokens >= bytes)
                {
                    _bandwidthTokens -= bytes;
                    TotalBytesThrottled += bytes;
                    return totalDelay;
                }
            }

            double deficit;
            long maxRate;
            lock (_lock)
            {
                deficit = bytes - _bandwidthTokens;
                maxRate = GetEffectiveBandwidth();
            }
            if (maxRate <= 0) maxRate = 1;

            int waitMs = (int)Math.Ceiling(deficit / (double)maxRate * 1000);
            waitMs = Math.Clamp(waitMs, 1, 1000);
            totalDelay += waitMs;
            lock (_lock) { TotalThrottleDelayMs += waitMs; }
            await Task.Delay(waitMs, ct);
        }
    }

    /// <summary>
    /// Wait until disk I/O tokens are available (both IOPS and throughput).
    /// Zero-alloc fast path: non-async when tokens are immediately available.
    /// </summary>
    public ValueTask<long> ThrottleDiskIOAsync(long bytes, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes <= 0) return default;
        if (ct.IsCancellationRequested) return new ValueTask<long>(Task.FromCanceled<long>(ct));

        RefillDiskTokens();

        // Single lock: check + consume atomically (no TOCTOU race)
        lock (_lock)
        {
            if (_diskTokens >= bytes && _iopsThisSecond < _limits.MaxDiskIops)
            {
                _diskTokens -= bytes;
                _diskBytesThisSecond += bytes;
                _iopsThisSecond++;
                TotalOperationsThrottled++;
                return default;
            }
        }

        // Slow path: need to wait
        return ThrottleDiskIOAsyncSlow(bytes, ct);
    }

    private async ValueTask<long> ThrottleDiskIOAsyncSlow(long bytes, CancellationToken ct)
    {
        long totalDelay = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RefillDiskTokens();

            lock (_lock)
            {
                if (_diskTokens >= bytes && _iopsThisSecond < _limits.MaxDiskIops)
                {
                    _diskTokens -= bytes;
                    _diskBytesThisSecond += bytes;
                    _iopsThisSecond++;
                    TotalOperationsThrottled++;
                    return totalDelay;
                }
            }

            int waitMs = 50;
            totalDelay += waitMs;
            lock (_lock) { TotalThrottleDelayMs += waitMs; }
            await Task.Delay(waitMs, ct);
        }
    }

    /// <summary>
    /// Check if CPU usage is within limits. If not, returns a delay to apply.
    /// Zero-alloc fast path: non-async when CPU is within limits.
    /// </summary>
    public ValueTask<long> ThrottleCpuAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateCpuUsage();

        int maxCpu;
        lock (_lock) { maxCpu = _limits.MaxCpuPercent; }

        // Fast path: CPU within limits (zero allocation)
        if (_currentCpuPercent <= maxCpu || maxCpu <= 0)
            return default;

        // Slow path: need to wait for CPU to cool down
        return ThrottleCpuAsyncSlow(maxCpu, ct);
    }

    private async ValueTask<long> ThrottleCpuAsyncSlow(int maxCpu, CancellationToken ct)
    {
        long totalDelay = 0;
        while (_currentCpuPercent > maxCpu && maxCpu > 0)
        {
            ct.ThrowIfCancellationRequested();
            int waitMs = 100;
            totalDelay += waitMs;
            lock (_lock) { TotalThrottleDelayMs += waitMs; }
            await Task.Delay(waitMs, ct);
            UpdateCpuUsage();
        }
        return totalDelay;
    }

    /// <summary>
    /// Combined throttle call: applies bandwidth, disk I/O, and CPU limits.
    /// Returns total delay applied in milliseconds.
    /// Zero-alloc fast path: non-async when no throttling needed.
    /// </summary>
    public ValueTask<long> ThrottleAsync(long bytes, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes <= 0) return default;
        // Synchronous fast path: all checks + consume in single lock (no TOCTOU race)
        ct.ThrowIfCancellationRequested();
        RefillTokens();
        RefillDiskTokens();
        UpdateCpuUsage();

        lock (_lock)
        {
            bool bwOk = _bandwidthTokens >= bytes;
            bool diskOk = _diskTokens >= bytes && _iopsThisSecond < _limits.MaxDiskIops;
            int maxCpu = _limits.MaxCpuPercent;
            bool cpuOk = _currentCpuPercent <= maxCpu || maxCpu <= 0;

            if (bwOk && diskOk && cpuOk)
            {
                // Consume all tokens atomically
                _bandwidthTokens -= bytes;
                TotalBytesThrottled += bytes;
                _diskTokens -= bytes;
                _diskBytesThisSecond += bytes;
                _iopsThisSecond++;
                TotalOperationsThrottled++;

                if (_limits.IsAutoAdaptive) AdjustAdaptive();
                return new ValueTask<long>(0L);
            }
        }

        // Async slow path: actual throttling needed
        return ThrottleAsyncSlow(bytes, ct);
    }

    private async ValueTask<long> ThrottleAsyncSlow(long bytes, CancellationToken ct)
    {
        long delay = 0;
        delay += await ThrottleBandwidthAsync(bytes, ct);
        delay += await ThrottleDiskIOAsync(bytes, ct);
        delay += await ThrottleCpuAsync(ct);

        if (_limits.IsAutoAdaptive)
        {
            AdjustAdaptive();
        }

        return delay;
    }

    /// <summary>
    /// Current CPU utilization percentage of this process.
    /// </summary>
    public double CurrentCpuPercent => _currentCpuPercent;

    /// <summary>
    /// Current throttle status summary.
    /// </summary>
    public ThrottleStatus GetStatus()
    {
        UpdateCpuUsage();
        lock (_lock)
        {
            return new ThrottleStatus
            {
                Profile = _limits.IsAutoAdaptive ? "Auto-Adaptive" : "Manual",
                CpuPercent = _currentCpuPercent,
                MaxBandwidthBytesPerSec = GetEffectiveBandwidth(),
                MaxDiskIops = _limits.MaxDiskIops,
                MaxDiskBytesPerSec = _limits.MaxDiskBytesPerSec,
                TotalBytesThrottled = TotalBytesThrottled,
                TotalThrottleDelayMs = TotalThrottleDelayMs,
                TotalOperationsThrottled = TotalOperationsThrottled,
                AdaptiveBandwidthFactor = _adaptiveBandwidthFactor,
            };
        }
    }

    // ── Internal ────────────────────────────────────────────────────────────

    private long GetEffectiveBandwidth()
    {
        long baseRate = _limits.MaxBandwidthBytesPerSec;
        if (_limits.IsAutoAdaptive)
        {
            return (long)(baseRate * _adaptiveBandwidthFactor);
        }
        return baseRate;
    }

    private void RefillTokens()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            double elapsed = (double)(now - _lastBandwidthRefill) / Stopwatch.Frequency;
            long rate = GetEffectiveBandwidth();
            _bandwidthTokens = Math.Min(_bandwidthTokens + elapsed * rate, rate);
            _lastBandwidthRefill = now;
        }
    }

    private void RefillDiskTokens()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            // Reset per-second counters
            double elapsedSec = (double)(now - _lastDiskThroughputReset) / Stopwatch.Frequency;
            if (elapsedSec >= 1.0)
            {
                _diskBytesThisSecond = 0;
                _iopsThisSecond = 0;
                _lastDiskThroughputReset = now;
            }

            double elapsed = (double)(now - _lastDiskRefill) / Stopwatch.Frequency;
            _diskTokens = Math.Min(_diskTokens + elapsed * _limits.MaxDiskBytesPerSec, _limits.MaxDiskBytesPerSec);
            _lastDiskRefill = now;
        }
    }

    private void UpdateCpuUsage()
    {
        try
        {
            _process.Refresh();
            var cpuTime = _process.TotalProcessorTime;
            var now = DateTime.UtcNow;
            double elapsedMs = (now - _lastCpuCheck).TotalMilliseconds;

            if (elapsedMs > 100) // Avoid too-frequent updates
            {
                double cpuUsedMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                int processorCount = Environment.ProcessorCount;
                _currentCpuPercent = Math.Min(100, cpuUsedMs / (elapsedMs * processorCount) * 100);
                _lastCpuTime = cpuTime;
                _lastCpuCheck = now;
            }
        }
        catch
        {
            // CPU monitoring is best-effort
        }
    }

    private void AdjustAdaptive()
    {
        // Auto-adaptive: reduce bandwidth if CPU is too high, increase if CPU is low
        lock (_lock)
        {
            if (_currentCpuPercent > _limits.AutoTargetCpuPercent * 1.5)
            {
                _adaptiveBandwidthFactor = Math.Max(0.1, _adaptiveBandwidthFactor * 0.9);
            }
            else if (_currentCpuPercent < _limits.AutoTargetCpuPercent * 0.5)
            {
                _adaptiveBandwidthFactor = Math.Min(1.0, _adaptiveBandwidthFactor * 1.1);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _process.Dispose();
    }
}

/// <summary>
/// Snapshot of current throttle status for reporting (struct for zero-allocation).
/// </summary>
public struct ThrottleStatus
{
    public string Profile { get; set; }
    public double CpuPercent { get; set; }
    public long MaxBandwidthBytesPerSec { get; set; }
    public int MaxDiskIops { get; set; }
    public long MaxDiskBytesPerSec { get; set; }
    public long TotalBytesThrottled { get; set; }
    public long TotalThrottleDelayMs { get; set; }
    public long TotalOperationsThrottled { get; set; }
    public double AdaptiveBandwidthFactor { get; set; }
}
