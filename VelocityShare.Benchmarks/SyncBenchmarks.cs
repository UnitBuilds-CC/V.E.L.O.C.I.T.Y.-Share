using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using VelocityShare.Server.Sync;

namespace VelocityShare.Benchmarks;

/// <summary>
/// Benchmarks for the sync rate limiting subsystem:
/// - Rate limiter throughput at various bandwidth limits
/// - Adaptive scheduler debounce behavior
/// - Latency tracker overhead
/// - Block delta detection throughput
/// - Throttle profile effective throughput
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SyncBenchmarks
{
    // ── Rate Limiter ────────────────────────────────────────────────────

    [BenchmarkCategory("RateLimiter")]
    [Benchmark(Description = "Bandwidth: Unlimited (no throttle delay)")]
    public async Task RateLimiter_Unlimited_Throughput()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = long.MaxValue,
            MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue,
            MaxDiskBytesPerSec = long.MaxValue
        };
        using var limiter = new SyncRateLimiter(limits);

        for (int i = 0; i < 1000; i++)
            await limiter.ThrottleBandwidthAsync(65536); // 64KB blocks
    }

    [BenchmarkCategory("RateLimiter")]
    [Benchmark(Description = "Bandwidth: 50 MB/s (Balanced)")]
    public async Task RateLimiter_Balanced_Throughput()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 50L * 1024 * 1024,
            MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue,
            MaxDiskBytesPerSec = long.MaxValue
        };
        using var limiter = new SyncRateLimiter(limits);

        for (int i = 0; i < 100; i++)
            await limiter.ThrottleBandwidthAsync(65536);
    }

    [BenchmarkCategory("RateLimiter")]
    [Benchmark(Description = "Bandwidth: 10 MB/s (Throttled)")]
    public async Task RateLimiter_Throttled_Throughput()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 10L * 1024 * 1024,
            MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue,
            MaxDiskBytesPerSec = long.MaxValue
        };
        using var limiter = new SyncRateLimiter(limits);

        for (int i = 0; i < 100; i++)
            await limiter.ThrottleBandwidthAsync(65536);
    }

    [BenchmarkCategory("RateLimiter")]
    [Benchmark(Description = "Bandwidth: 2 MB/s (Background)")]
    public async Task RateLimiter_Background_Throughput()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 2L * 1024 * 1024,
            MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue,
            MaxDiskBytesPerSec = long.MaxValue
        };
        using var limiter = new SyncRateLimiter(limits);

        for (int i = 0; i < 50; i++)
            await limiter.ThrottleBandwidthAsync(65536);
    }

    [BenchmarkCategory("RateLimiter")]
    [Benchmark(Description = "Disk I/O: Throttle 1000 ops")]
    public async Task RateLimiter_DiskIO_Throttle()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = long.MaxValue,
            MaxCpuPercent = 100,
            MaxDiskIops = 10_000,
            MaxDiskBytesPerSec = 200L * 1024 * 1024
        };
        using var limiter = new SyncRateLimiter(limits);

        for (int i = 0; i < 1000; i++)
            await limiter.ThrottleDiskIOAsync(4096);
    }

    [BenchmarkCategory("RateLimiter")]
    [Benchmark(Description = "GetStatus snapshot")]
    public void RateLimiter_GetStatus()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 50L * 1024 * 1024,
            MaxCpuPercent = 30,
            MaxDiskIops = 200
        };
        using var limiter = new SyncRateLimiter(limits);

        for (int i = 0; i < 100; i++)
            limiter.GetStatus();
    }

    // ── Adaptive Scheduler ──────────────────────────────────────────────

    [BenchmarkCategory("Scheduler")]
    [Benchmark(Description = "Scheduler: 1000 change notifications")]
    public void Scheduler_RapidChanges()
    {
        var limits = new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 };
        using var scheduler = new AdaptiveSyncScheduler(limits);

        for (int i = 0; i < 1000; i++)
            scheduler.NotifyChange();
    }

    [BenchmarkCategory("Scheduler")]
    [Benchmark(Description = "Scheduler: GetStats snapshot")]
    public void Scheduler_GetStats()
    {
        var limits = new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 };
        using var scheduler = new AdaptiveSyncScheduler(limits);

        for (int i = 0; i < 100; i++)
        {
            scheduler.NotifyChange();
            scheduler.GetStats();
        }
    }

    // ── Latency Tracker ─────────────────────────────────────────────────

    [BenchmarkCategory("Latency")]
    [Benchmark(Description = "Latency: 1000 record+complete cycles")]
    public void LatencyTracker_RecordAndComplete()
    {
        var tracker = new SyncLatencyTracker();

        for (int i = 0; i < 1000; i++)
        {
            tracker.RecordChangeDetected($"file_{i}.txt");
            tracker.RecordSyncCompleted($"file_{i}.txt");
        }
    }

    [BenchmarkCategory("Latency")]
    [Benchmark(Description = "Latency: GetMetrics with 10K samples")]
    public void LatencyTracker_GetMetrics()
    {
        var tracker = new SyncLatencyTracker();

        for (int i = 0; i < 10_000; i++)
        {
            tracker.RecordChangeDetected($"file_{i}.txt");
            tracker.RecordSyncCompleted($"file_{i}.txt");
        }

        for (int i = 0; i < 100; i++)
            tracker.GetMetrics();
    }

    // ── Block Delta Detection ───────────────────────────────────────────

    private string _tempDir = null!;
    private string _testFile = null!;

    [GlobalSetup(Target = nameof(BlockDelta_SmallFile))]
    public void SetupSmallFile()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"veloci_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _testFile = Path.Combine(_tempDir, "small.bin");
        File.WriteAllBytes(_testFile, RandomData(256 * 1024)); // 256KB
    }

    [GlobalSetup(Target = nameof(BlockDelta_MediumFile))]
    public void SetupMediumFile()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"veloci_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _testFile = Path.Combine(_tempDir, "medium.bin");
        File.WriteAllBytes(_testFile, RandomData(4 * 1024 * 1024)); // 4MB
    }

    [GlobalSetup(Target = nameof(BlockDelta_LargeFile))]
    public void SetupLargeFile()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"veloci_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _testFile = Path.Combine(_tempDir, "large.bin");
        File.WriteAllBytes(_testFile, RandomData(64 * 1024 * 1024)); // 64MB
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [BenchmarkCategory("Delta")]
    [Benchmark(Description = "Delta: 256KB file block hashes")]
    public async Task BlockDelta_SmallFile()
    {
        var detector = new BlockDeltaDetector();
        var storage = new LocalSyncStorageProvider(_tempDir);
        await detector.ComputeBlockHashesAsync(storage, "small.bin", 256 * 1024);
        await storage.DisposeAsync();
    }

    [BenchmarkCategory("Delta")]
    [Benchmark(Description = "Delta: 4MB file block hashes")]
    public async Task BlockDelta_MediumFile()
    {
        var detector = new BlockDeltaDetector();
        var storage = new LocalSyncStorageProvider(_tempDir);
        await detector.ComputeBlockHashesAsync(storage, "medium.bin", 4 * 1024 * 1024);
        await storage.DisposeAsync();
    }

    [BenchmarkCategory("Delta")]
    [Benchmark(Description = "Delta: 64MB file block hashes")]
    public async Task BlockDelta_LargeFile()
    {
        var detector = new BlockDeltaDetector();
        var storage = new LocalSyncStorageProvider(_tempDir);
        await detector.ComputeBlockHashesAsync(storage, "large.bin", 64 * 1024 * 1024);
        await storage.DisposeAsync();
    }

    // ── Throttle Config Resolution ──────────────────────────────────────

    [BenchmarkCategory("Config")]
    [Benchmark(Description = "Config: Resolve all 5 profiles")]
    public void Config_ResolveAllProfiles()
    {
        foreach (var profile in Enum.GetValues<SyncThrottleProfile>())
        {
            var config = new SyncThrottleConfig { Profile = profile };
            config.Resolve();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static byte[] RandomData(int size)
    {
        var data = new byte[size];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        return data;
    }
}
