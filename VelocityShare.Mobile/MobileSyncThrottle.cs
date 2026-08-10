using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Mobile
{
    /// <summary>
    /// Lightweight throttle for the mobile sync client.
    /// Provides bandwidth token bucket, adaptive debounce, and latency tracking
    /// without depending on the Server project.
    /// </summary>
    public sealed class MobileSyncThrottle : IDisposable
    {
        // Bandwidth token bucket
        private double _bandwidthTokens;
        private long _lastRefill;
        private long _maxBandwidthBytesPerSec;

        // Adaptive debounce
        private int _currentDebounceMs;
        private DateTimeOffset _lastChangeTime;
        private int _recentChangeCount;
        private int _baseDebounceMs;
        private int _maxDebounceMs;
        private int _stabilityWindowMs;

        // Latency tracking
        private int _totalChangesDetected;
        private int _totalSyncsCompleted;
        private long _totalLatencyMs;
        private long _maxLatencyMs;
        private long _minLatencyMs = long.MaxValue;
        private readonly ConcurrentTimestamps _pendingChanges = new();

        // Stats
        public long TotalBytesThrottled { get; private set; }
        public long TotalThrottleDelayMs { get; private set; }

        public int CurrentDebounceMs => _currentDebounceMs;
        public double CurrentCpuPercent { get; private set; }
        public double AverageLatencyMs => _totalSyncsCompleted > 0 ? (double)_totalLatencyMs / _totalSyncsCompleted : 0;
        public long MaxLatencyMs => _maxLatencyMs;
        public int PendingChanges => _pendingChanges.Count;

        public MobileSyncThrottle(long maxBandwidthBytesPerSec = 10 * 1024 * 1024)
        {
            _maxBandwidthBytesPerSec = maxBandwidthBytesPerSec;
            _bandwidthTokens = maxBandwidthBytesPerSec;
            _lastRefill = Stopwatch.GetTimestamp();
            _baseDebounceMs = 500;
            _maxDebounceMs = 5000;
            _stabilityWindowMs = 2000;
            _currentDebounceMs = _baseDebounceMs;
            _lastChangeTime = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Set the bandwidth limit. 0 = unlimited.
        /// </summary>
        public void SetBandwidthLimit(long bytesPerSec)
        {
            _maxBandwidthBytesPerSec = bytesPerSec > 0 ? bytesPerSec : long.MaxValue;
        }

        /// <summary>
        /// Wait until bandwidth tokens are available.
        /// Returns delay applied in ms.
        /// </summary>
        public async Task<long> ThrottleBandwidthAsync(long bytes, CancellationToken ct = default)
        {
            long totalDelay = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                RefillTokens();

                if (_bandwidthTokens >= bytes)
                {
                    _bandwidthTokens -= bytes;
                    TotalBytesThrottled += bytes;
                    return totalDelay;
                }

                double deficit = bytes - _bandwidthTokens;
                long rate = _maxBandwidthBytesPerSec > 0 ? _maxBandwidthBytesPerSec : 1;
                int waitMs = (int)Math.Ceiling(deficit / (double)rate * 1000);
                waitMs = Math.Clamp(waitMs, 1, 1000);

                totalDelay += waitMs;
                TotalThrottleDelayMs += waitMs;
                await Task.Delay(waitMs, ct);
            }
        }

        /// <summary>
        /// Notify that a file change was detected.
        /// Returns the recommended debounce delay in ms.
        /// </summary>
        public int NotifyChange(string relativePath)
        {
            _totalChangesDetected++;
            _lastChangeTime = DateTimeOffset.UtcNow;
            _recentChangeCount++;
            _pendingChanges.Record(relativePath);

            // Calculate change rate
            double rate = _recentChangeCount / 5.0; // rough changes/sec over 5s window

            if (rate > 10)
            {
                // Rapid changes — extend debounce
                _currentDebounceMs = Math.Min(_currentDebounceMs + 200, _maxDebounceMs);
            }
            else if (rate < 2)
            {
                _currentDebounceMs = _baseDebounceMs;
            }
            else
            {
                double factor = rate / 10.0;
                _currentDebounceMs = (int)(_baseDebounceMs + factor * (_baseDebounceMs * 3));
            }

            return _currentDebounceMs;
        }

        /// <summary>
        /// Notify that a sync was triggered.
        /// </summary>
        public void NotifySyncTriggered()
        {
            _recentChangeCount = 0;
        }

        /// <summary>
        /// Record that a sync completed for a file.
        /// Returns latency in ms.
        /// </summary>
        public long RecordSyncCompleted(string relativePath)
        {
            if (_pendingChanges.TryRemove(relativePath, out var startTime))
            {
                long latencyMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                _totalSyncsCompleted++;
                _totalLatencyMs += latencyMs;
                if (latencyMs > _maxLatencyMs) _maxLatencyMs = latencyMs;
                if (latencyMs < _minLatencyMs) _minLatencyMs = latencyMs;
                return latencyMs;
            }
            return -1;
        }

        /// <summary>
        /// Check if files have been stable (no changes) for the stability window.
        /// </summary>
        public bool IsStable()
        {
            return (DateTimeOffset.UtcNow - _lastChangeTime).TotalMilliseconds >= _stabilityWindowMs;
        }

        private void RefillTokens()
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (double)(now - _lastRefill) / Stopwatch.Frequency;
            _bandwidthTokens = Math.Min(_bandwidthTokens + elapsed * _maxBandwidthBytesPerSec, _maxBandwidthBytesPerSec);
            _lastRefill = now;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Simple concurrent dictionary for tracking change timestamps.
    /// </summary>
    internal sealed class ConcurrentTimestamps
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _dict = new();

        public int Count => _dict.Count;

        public void Record(string key) => _dict[key] = DateTimeOffset.UtcNow;

        public bool TryRemove(string key, out DateTimeOffset value)
        {
            if (_dict.TryRemove(key, out var ts))
            {
                value = ts;
                return true;
            }
            value = default;
            return false;
        }
    }
}
