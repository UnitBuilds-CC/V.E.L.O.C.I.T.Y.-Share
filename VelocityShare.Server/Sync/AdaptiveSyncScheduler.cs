using System;
using System.Threading;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Adaptive sync scheduler that detects stable states during rapid file changes.
/// Instead of syncing on every FileSystemWatcher event, it dynamically adjusts
/// the debounce interval based on change frequency:
/// - Files changing rapidly → extend debounce, wait for stability
/// - Files settling → trigger sync after stability window
/// - Idle → use minimum debounce for fast response
/// Zero-allocation hot path: uses circular buffer instead of ConcurrentQueue.
/// </summary>
public sealed class AdaptiveSyncScheduler : IDisposable
{
    private EffectiveLimits _limits;
    // Circular buffer for change timestamps (zero-allocation alternative to ConcurrentQueue)
    private readonly DateTimeOffset[] _changeTimestamps;
    private int _timestampHead;
    private int _timestampCount;
    private readonly Timer _stabilityTimer;
    private readonly object _lock = new();

    private int _currentDebounceMs;
    private DateTimeOffset _lastChangeTime;
    private bool _isStable = true;
    private volatile bool _disposed;
    private int _totalChanges;
    private int _totalSyncTriggers;
    private int _rapidChangeEvents;

    /// <summary>Fired when the scheduler determines files have reached a stable state and sync should proceed.</summary>
    public event Action? OnStableStateReached;

    /// <summary>Fired when rapid file changes are detected and sync is being deferred.</summary>
    public event Action<int>? OnRapidChangesDetected;

    public int CurrentDebounceMs
    {
        get { lock (_lock) return _currentDebounceMs; }
    }

    public bool IsStable
    {
        get { lock (_lock) return _isStable; }
    }

    /// <summary>Changes per second over the recent window.</summary>
    public double CurrentChangeRate
    {
        get
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                int count = 0;
                for (int i = 0; i < _timestampCount; i++)
                {
                    int idx = (_timestampHead + i) % _changeTimestamps.Length;
                    if ((now - _changeTimestamps[idx]).TotalSeconds <= 5.0) count++;
                }
                return count / 5.0;
            }
        }
    }

    public AdaptiveSyncScheduler(EffectiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _currentDebounceMs = limits.DebounceMs;
        _lastChangeTime = DateTimeOffset.UtcNow;
        _stabilityTimer = new Timer(CheckStability, null, Timeout.Infinite, Timeout.Infinite);
        // Circular buffer: 1024 entries covers ~10s at high event rates
        _changeTimestamps = new DateTimeOffset[1024];
    }

    /// <summary>
    /// Update the effective limits at runtime (e.g., from throttle config change).
    /// </summary>
    public void UpdateLimits(EffectiveLimits newLimits)
    {
        ArgumentNullException.ThrowIfNull(newLimits);
        lock (_lock)
        {
            _limits = newLimits;
            _currentDebounceMs = newLimits.DebounceMs;
        }
    }

    /// <summary>
    /// Notify the scheduler that a file change was detected.
    /// Returns the recommended debounce delay in milliseconds before processing.
    /// Zero-allocation: uses circular buffer instead of ConcurrentQueue.
    /// </summary>
    public int NotifyChange()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int debounceMs;
        int rapidCount = 0;
        bool fireRapid = false;

        lock (_lock)
        {
            _totalChanges++;
            _lastChangeTime = DateTimeOffset.UtcNow;

            // Add to circular buffer (overwrites oldest if full)
            int tail = (_timestampHead + _timestampCount) % _changeTimestamps.Length;
            _changeTimestamps[tail] = _lastChangeTime;
            if (_timestampCount < _changeTimestamps.Length)
                _timestampCount++;
            else
                _timestampHead = (_timestampHead + 1) % _changeTimestamps.Length;

            // Prune entries older than 10s
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
            while (_timestampCount > 0 && _changeTimestamps[_timestampHead] < cutoff)
            {
                _timestampHead = (_timestampHead + 1) % _changeTimestamps.Length;
                _timestampCount--;
            }

            // Calculate change rate (changes per second over last 5 seconds)
            int recentCount = 0;
            for (int i = 0; i < _timestampCount; i++)
            {
                int idx = (_timestampHead + i) % _changeTimestamps.Length;
                if ((_lastChangeTime - _changeTimestamps[idx]).TotalSeconds <= 5.0)
                    recentCount++;
            }
            double rate = recentCount / 5.0;

            // Adaptive debounce: scale debounce based on change frequency
            double threshold = _limits.StabilityWindowMs > 0 ? _limits.StabilityWindowMs / 1000.0 * _limits.DebounceMs : 10;
            if (rate > threshold)
            {
                // Rapid changes — extend debounce toward max
                _currentDebounceMs = Math.Min(
                    _currentDebounceMs + 200,
                    _limits.DebounceMs > 0 ? _limits.DebounceMs * 4 : 5000);
                _isStable = false;
                _rapidChangeEvents++;
                fireRapid = true;
                rapidCount = recentCount;
            }
            else if (rate < 2)
            {
                // Low activity — use minimum debounce for fast response
                _currentDebounceMs = _limits.DebounceMs;
                _isStable = true;
            }
            else
            {
                // Moderate activity — scale proportionally
                double factor = rate / 10.0;
                _currentDebounceMs = (int)(_limits.DebounceMs + factor * (_limits.DebounceMs * 3));
                _isStable = false;
            }

            // Reset stability timer
            _stabilityTimer.Change(_limits.StabilityWindowMs > 0 ? _limits.StabilityWindowMs : 2000, Timeout.Infinite);
            debounceMs = _currentDebounceMs;
        }

        // Fire events OUTSIDE lock to prevent reentrancy deadlocks
        if (fireRapid) OnRapidChangesDetected?.Invoke(rapidCount);
        return debounceMs;
    }

    /// <summary>
    /// Notify that a sync trigger actually happened (for stats).
    /// </summary>
    public void NotifySyncTriggered()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock) { _totalSyncTriggers++; }
    }

    private void CheckStability(object? state)
    {
        if (_disposed) return;
        bool fireStable = false;
        lock (_lock)
        {
            int stabilityMs = _limits.StabilityWindowMs > 0 ? _limits.StabilityWindowMs : 2000;
            var elapsed = (DateTimeOffset.UtcNow - _lastChangeTime).TotalMilliseconds;

            if (elapsed >= stabilityMs && !_isStable)
            {
                _isStable = true;
                _currentDebounceMs = _limits.DebounceMs;
                fireStable = true;
            }
        }

        // Fire event OUTSIDE lock to prevent reentrancy deadlocks
        if (fireStable) OnStableStateReached?.Invoke();
    }

    /// <summary>
    /// Get scheduler statistics.
    /// </summary>
    public SchedulerStats GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            // Inline change rate calculation (avoids re-acquiring lock via CurrentChangeRate property)
            var now = DateTimeOffset.UtcNow;
            int recentCount = 0;
            for (int i = 0; i < _timestampCount; i++)
            {
                int idx = (_timestampHead + i) % _changeTimestamps.Length;
                if ((now - _changeTimestamps[idx]).TotalSeconds <= 5.0) recentCount++;
            }
            double changeRate = recentCount / 5.0;

            return new SchedulerStats
            {
                TotalChanges = _totalChanges,
                TotalSyncTriggers = _totalSyncTriggers,
                RapidChangeEvents = _rapidChangeEvents,
                CurrentDebounceMs = _currentDebounceMs,
                CurrentChangeRate = changeRate,
                IsStable = _isStable,
                SyncReductionFactor = _totalChanges > 0
                    ? (double)_totalSyncTriggers / _totalChanges
                    : 1.0
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stabilityTimer.Dispose();
    }
}

/// <summary>
/// Statistics from the adaptive sync scheduler.
/// </summary>
public sealed class SchedulerStats
{
    public int TotalChanges { get; init; }
    public int TotalSyncTriggers { get; init; }
    public int RapidChangeEvents { get; init; }
    public int CurrentDebounceMs { get; init; }
    public double CurrentChangeRate { get; init; }
    public bool IsStable { get; init; }

    /// <summary>
    /// Ratio of sync triggers to total changes. Lower = more efficient batching.
    /// 1.0 means every change triggered a sync; 0.1 means 90% were batched.
    /// </summary>
    public double SyncReductionFactor { get; init; }
}
