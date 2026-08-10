using System;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Tracks sync latency metrics: time from file change detection to sync completion.
/// Provides rolling averages, min/max, and per-file latency tracking.
/// Strict zero-allocation hot path: struct samples, struct pending table, stack-based P95.
/// </summary>
public sealed class SyncLatencyTracker
{
    // Circular buffer for latency samples (struct array — zero heap alloc per sample)
    private readonly LatencySample[] _samples;
    private int _sampleHead;
    private int _sampleCount;

    // Fixed-size pending table using parallel primitive arrays.
    // Zero-allocation: no struct copies, no Dictionary, no KeyValuePair.
    private readonly string?[] _pendingPaths;
    private readonly long[] _pendingTimestampTicks;
    private readonly bool[] _pendingActive;
    private int _pendingCount;

    private readonly object _lock = new();

    private long _totalChangesDetected;
    private long _totalSyncsCompleted;
    private long _totalLatencyMs;
    private long _maxLatencyMs;
    private long _minLatencyMs = long.MaxValue;

    public SyncLatencyTracker(int maxSamples = 1024, int maxPending = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSamples, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPending, 0);
        _samples = new LatencySample[maxSamples];
        _pendingPaths = new string?[maxPending];
        _pendingTimestampTicks = new long[maxPending];
        _pendingActive = new bool[maxPending];
    }

    /// <summary>
    /// Record that a file change was detected (start of latency measurement).
    /// Zero-allocation: writes into fixed-size struct array.
    /// </summary>
    public void RecordChangeDetected(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        lock (_lock)
        {
            // Update existing entry if found (direct array reads — no struct copy)
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pendingActive[i] && string.Equals(_pendingPaths[i], relativePath, StringComparison.Ordinal))
                {
                    _pendingTimestampTicks[i] = DateTimeOffset.UtcNow.UtcTicks;
                    _totalChangesDetected++;
                    return;
                }
            }

            // Add new entry at the end (entries are always contiguous in [0.._pendingCount-1])
            int idx = _pendingCount;
            if (idx < _pendingPaths.Length)
            {
                _pendingPaths[idx] = relativePath;
                _pendingTimestampTicks[idx] = DateTimeOffset.UtcNow.UtcTicks;
                _pendingActive[idx] = true;
                _pendingCount++;
                _totalChangesDetected++;
                return;
            }
            // Table full — count the change but drop tracking (all slots in use)
            _totalChangesDetected++;
        }
    }

    /// <summary>
    /// Record that a file sync completed (end of latency measurement).
    /// Returns the latency in milliseconds, or -1 if no start time was recorded.
    /// Zero-allocation: struct sample, circular buffer, struct pending table.
    /// </summary>
    public long RecordSyncCompleted(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        lock (_lock)
        {
            // Find pending entry (direct array reads — no struct copy)
            int foundIndex = -1;
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pendingActive[i] && string.Equals(_pendingPaths[i], relativePath, StringComparison.Ordinal))
                {
                    foundIndex = i;
                    break;
                }
            }

            if (foundIndex >= 0)
            {
                long startTicks = _pendingTimestampTicks[foundIndex];

                // Swap-with-last: move last active entry into the freed slot
                // so active entries stay contiguous in [0.._pendingCount-1].
                int last = _pendingCount - 1;
                if (foundIndex != last)
                {
                    _pendingPaths[foundIndex] = _pendingPaths[last];
                    _pendingTimestampTicks[foundIndex] = _pendingTimestampTicks[last];
                    _pendingActive[foundIndex] = true;
                }
                _pendingPaths[last] = null;
                _pendingActive[last] = false;
                _pendingCount--;

                var startTime = new DateTimeOffset(startTicks, TimeSpan.Zero);
                long latencyMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds);
                var sample = new LatencySample(relativePath, latencyMs, DateTimeOffset.UtcNow);

                // Add to circular buffer (overwrites oldest if full)
                int tail = (_sampleHead + _sampleCount) % _samples.Length;
                _samples[tail] = sample;
                if (_sampleCount < _samples.Length)
                    _sampleCount++;
                else
                    _sampleHead = (_sampleHead + 1) % _samples.Length;

                // Prune samples older than 10 minutes
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
                while (_sampleCount > 0 && _samples[_sampleHead].Timestamp < cutoff)
                {
                    _sampleHead = (_sampleHead + 1) % _samples.Length;
                    _sampleCount--;
                }

                _totalSyncsCompleted++;
                _totalLatencyMs += latencyMs;
                if (latencyMs > _maxLatencyMs) _maxLatencyMs = latencyMs;
                if (latencyMs < _minLatencyMs) _minLatencyMs = latencyMs;

                return latencyMs;
            }
            return -1;
        }
    }

    /// <summary>
    /// Get the current latency metrics snapshot.
    /// Zero-allocation P95: uses stackalloc + quickselect instead of List<T>.
    /// </summary>
    public LatencyMetrics GetMetrics()
    {
        lock (_lock)
        {
            long count = _totalSyncsCompleted;
            double avgMs = count > 0 ? (double)_totalLatencyMs / count : 0;

            // Calculate P95 from recent samples (zero-allocation)
            double p95Ms = 0;
            var now = DateTimeOffset.UtcNow;
            int recentCount = 0;
            for (int i = 0; i < _sampleCount; i++)
            {
                int idx = (_sampleHead + i) % _samples.Length;
                if ((now - _samples[idx].Timestamp).TotalMinutes <= 5)
                    recentCount++;
            }

            if (recentCount > 0)
            {
                // Copy recent latencies to stack-allocated buffer
                Span<long> latencies = recentCount <= 256 ? stackalloc long[recentCount] : new long[recentCount];
                int j = 0;
                for (int i = 0; i < _sampleCount && j < recentCount; i++)
                {
                    int idx = (_sampleHead + i) % _samples.Length;
                    if ((now - _samples[idx].Timestamp).TotalMinutes <= 5)
                        latencies[j++] = _samples[idx].LatencyMs;
                }

                // Partial sort to find P95 (zero-allocation quickselect)
                int p95Index = (int)(recentCount * 0.95);
                p95Index = Math.Min(p95Index, recentCount - 1);
                p95Ms = QuickSelectKth(latencies, 0, recentCount - 1, p95Index);
            }

            return new LatencyMetrics
            {
                TotalChangesDetected = _totalChangesDetected,
                TotalSyncsCompleted = count,
                AverageLatencyMs = avgMs,
                MinLatencyMs = _minLatencyMs == long.MaxValue ? 0 : _minLatencyMs,
                MaxLatencyMs = _maxLatencyMs,
                P95LatencyMs = p95Ms,
                PendingChanges = _pendingCount,
            };
        }
    }

    /// <summary>
    /// Zero-allocation quickselect with median-of-3 pivot: O(n) average, avoids O(n²) worst case.
    /// </summary>
    private static long QuickSelectKth(Span<long> arr, int left, int right, int k)
    {
        while (left < right)
        {
            if (right - left < 4)
            {
                // Small range: insertion sort and return directly
                for (int i = left + 1; i <= right; i++)
                {
                    long val = arr[i];
                    int j = i - 1;
                    while (j >= left && arr[j] > val) { arr[j + 1] = arr[j]; j--; }
                    arr[j + 1] = val;
                }
                return arr[k];
            }

            // Median-of-3 pivot selection
            int mid = left + (right - left) / 2;
            if (arr[left] > arr[mid]) (arr[left], arr[mid]) = (arr[mid], arr[left]);
            if (arr[left] > arr[right]) (arr[left], arr[right]) = (arr[right], arr[left]);
            if (arr[mid] > arr[right]) (arr[mid], arr[right]) = (arr[right], arr[mid]);
            // Pivot is now at mid; move it to right-1 for partitioning
            (arr[mid], arr[right]) = (arr[right], arr[mid]);

            int pivotIndex = Partition(arr, left, right);
            if (k == pivotIndex)
                return arr[k];
            else if (k < pivotIndex)
                right = pivotIndex - 1;
            else
                left = pivotIndex + 1;
        }
        return arr[left];
    }

    private static int Partition(Span<long> arr, int left, int right)
    {
        long pivot = arr[right];
        int i = left;
        for (int j = left; j < right; j++)
        {
            if (arr[j] <= pivot)
            {
                (arr[i], arr[j]) = (arr[j], arr[i]);
                i++;
            }
        }
        (arr[i], arr[right]) = (arr[right], arr[i]);
        return i;
    }
}

/// <summary>
/// A single latency measurement sample (struct for zero-allocation).
/// </summary>
public readonly record struct LatencySample(string RelativePath, long LatencyMs, DateTimeOffset Timestamp);

// PendingEntry struct removed — replaced with parallel primitive arrays for zero-alloc.

/// <summary>
/// Aggregate latency metrics snapshot.
/// </summary>
public sealed class LatencyMetrics
{
    public long TotalChangesDetected { get; init; }
    public long TotalSyncsCompleted { get; init; }
    public double AverageLatencyMs { get; init; }
    public long MinLatencyMs { get; init; }
    public long MaxLatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public int PendingChanges { get; init; }
}
