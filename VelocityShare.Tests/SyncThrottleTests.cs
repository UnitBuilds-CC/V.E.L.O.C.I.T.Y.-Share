using VelocityShare.Server.Sync;

namespace VelocityShare.Tests;

public class SyncThrottleTests
{
    // ── SyncThrottleConfig ──────────────────────────────────────────────

    [Fact]
    public void Resolve_Unthrottled_NoLimits()
    {
        var config = new SyncThrottleConfig { Profile = SyncThrottleProfile.Unthrottled };
        var limits = config.Resolve();

        Assert.Equal(long.MaxValue, limits.MaxBandwidthBytesPerSec);
        Assert.Equal(100, limits.MaxCpuPercent);
        Assert.False(limits.IsAutoAdaptive);
    }

    [Fact]
    public void Resolve_Balanced_HasModerateLimits_Local()
    {
        var config = new SyncThrottleConfig { Profile = SyncThrottleProfile.Balanced };
        var limits = config.Resolve("local");

        Assert.Equal(100L * 1024 * 1024, limits.MaxBandwidthBytesPerSec); // 100 MB/s for local
        Assert.Equal(25, limits.MaxCpuPercent);
        Assert.Equal(500, limits.MaxDiskIops);
        Assert.True(limits.IsAutoAdaptive); // auto-adaptive is on by default
    }

    [Fact]
    public void Resolve_Balanced_HasLowerLimits_Network()
    {
        var config = new SyncThrottleConfig { Profile = SyncThrottleProfile.Balanced };
        var limits = config.Resolve("s3");

        Assert.Equal(50L * 1024 * 1024, limits.MaxBandwidthBytesPerSec); // 50 MB/s for network
        Assert.Equal(200, limits.MaxDiskIops);
    }

    [Fact]
    public void Resolve_Background_MinimalLimits_Local()
    {
        var config = new SyncThrottleConfig { Profile = SyncThrottleProfile.Background };
        var limits = config.Resolve("local");

        Assert.Equal(5L * 1024 * 1024, limits.MaxBandwidthBytesPerSec); // 5 MB/s for local
        Assert.Equal(3, limits.MaxCpuPercent);
        Assert.Equal(50, limits.MaxDiskIops);
    }

    [Fact]
    public void Resolve_Background_MinimalLimits_Network()
    {
        var config = new SyncThrottleConfig { Profile = SyncThrottleProfile.Background };
        var limits = config.Resolve("azure");

        Assert.Equal(2L * 1024 * 1024, limits.MaxBandwidthBytesPerSec); // 2 MB/s for network
        Assert.Equal(3, limits.MaxCpuPercent);
        Assert.Equal(20, limits.MaxDiskIops);
    }

    [Fact]
    public void Resolve_Custom_UsesManualLimits()
    {
        var config = new SyncThrottleConfig
        {
            Profile = SyncThrottleProfile.Custom,
            ManualLimits = new SyncManualLimits
            {
                MaxBandwidthBytesPerSec = 5_000_000,
                MaxCpuPercent = 10,
                MaxDiskIops = 30
            }
        };
        var limits = config.Resolve();

        Assert.Equal(5_000_000, limits.MaxBandwidthBytesPerSec);
        Assert.Equal(10, limits.MaxCpuPercent);
        Assert.Equal(30, limits.MaxDiskIops);
    }

    [Fact]
    public void Resolve_Custom_ZeroMeansUnlimited()
    {
        var config = new SyncThrottleConfig
        {
            Profile = SyncThrottleProfile.Custom,
            ManualLimits = new SyncManualLimits() // all zeros
        };
        var limits = config.Resolve();

        Assert.Equal(long.MaxValue, limits.MaxBandwidthBytesPerSec);
        Assert.Equal(100, limits.MaxCpuPercent);
    }

    [Fact]
    public void Resolve_AutoAdaptive_DisabledForUnthrottled()
    {
        var config = new SyncThrottleConfig
        {
            Profile = SyncThrottleProfile.Unthrottled,
            AutoAdaptive = true
        };
        var limits = config.Resolve();

        Assert.False(limits.IsAutoAdaptive); // Unthrottled overrides auto-adaptive
    }

    // ── SyncRateLimiter ─────────────────────────────────────────────────

    [Fact]
    public async Task RateLimiter_Bandwidth_ThrottlesWhenExceeded()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 1000, // 1 KB/s
            MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue,
            MaxDiskBytesPerSec = long.MaxValue
        };

        using var limiter = new SyncRateLimiter(limits);

        // First call should succeed (tokens start full)
        var delay1 = await limiter.ThrottleBandwidthAsync(500);
        Assert.True(delay1 >= 0);

        // Second call consuming more than remaining should throttle
        var delay2 = await limiter.ThrottleBandwidthAsync(800);
        Assert.True(delay2 > 0, "Should have been throttled");
    }

    [Fact]
    public async Task RateLimiter_Combined_Throttle_Works()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 10_000_000, // 10 MB/s
            MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue,
            MaxDiskBytesPerSec = 100_000_000
        };

        using var limiter = new SyncRateLimiter(limits);
        var delay = await limiter.ThrottleAsync(1000);
        Assert.True(delay >= 0);
        Assert.True(limiter.TotalBytesThrottled >= 1000);
    }

    [Fact]
    public void RateLimiter_GetStatus_ReturnsValidSnapshot()
    {
        var limits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 5_000_000,
            MaxCpuPercent = 25,
            MaxDiskIops = 100,
            MaxDiskBytesPerSec = 10_000_000
        };

        using var limiter = new SyncRateLimiter(limits);
        var status = limiter.GetStatus();

        Assert.Equal(5_000_000, status.MaxBandwidthBytesPerSec);
        Assert.Equal(100, status.MaxDiskIops);
        Assert.True(status.CpuPercent >= 0);
    }

    [Fact]
    public void RateLimiter_UpdateLimits_ChangesAtRuntime()
    {
        var limits = new EffectiveLimits { MaxBandwidthBytesPerSec = 1_000_000 };
        using var limiter = new SyncRateLimiter(limits);

        var newLimits = new EffectiveLimits { MaxBandwidthBytesPerSec = 5_000_000 };
        limiter.UpdateLimits(newLimits);

        var status = limiter.GetStatus();
        Assert.Equal(5_000_000, status.MaxBandwidthBytesPerSec);
    }

    // ── AdaptiveSyncScheduler ───────────────────────────────────────────

    [Fact]
    public void Scheduler_SingleChange_ReturnsBaseDebounce()
    {
        var limits = new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 };
        using var scheduler = new AdaptiveSyncScheduler(limits);

        int debounce = scheduler.NotifyChange();
        Assert.True(debounce >= 200); // Should be at least the base debounce
    }

    [Fact]
    public void Scheduler_RapidChanges_ExtendsDebounce()
    {
        var limits = new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 };
        using var scheduler = new AdaptiveSyncScheduler(limits);

        // Simulate rapid changes
        for (int i = 0; i < 20; i++)
        {
            scheduler.NotifyChange();
        }

        int debounce = scheduler.CurrentDebounceMs;
        Assert.True(debounce > 500, $"Debounce should increase during rapid changes, got {debounce}");
    }

    [Fact]
    public void Scheduler_GetStats_TracksChanges()
    {
        var limits = new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 };
        using var scheduler = new AdaptiveSyncScheduler(limits);

        scheduler.NotifyChange();
        scheduler.NotifyChange();
        scheduler.NotifySyncTriggered();

        var stats = scheduler.GetStats();
        Assert.Equal(2, stats.TotalChanges);
        Assert.Equal(1, stats.TotalSyncTriggers);
    }

    [Fact]
    public void Scheduler_StabilityDetected_FiresEvent()
    {
        var limits = new EffectiveLimits { DebounceMs = 200, StabilityWindowMs = 300 };
        using var scheduler = new AdaptiveSyncScheduler(limits);

        bool eventFired = false;
        scheduler.OnStableStateReached += () => eventFired = true;

        // Trigger rapid changes to become unstable
        for (int i = 0; i < 10; i++)
            scheduler.NotifyChange();

        Assert.False(scheduler.IsStable);

        // Wait for stability window to pass (poll with generous timeout for CI)
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!eventFired && !scheduler.IsStable && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.True(eventFired || scheduler.IsStable);
    }

    // ── SyncLatencyTracker ──────────────────────────────────────────────

    [Fact]
    public void LatencyTracker_RecordChangeAndCompletion_TracksLatency()
    {
        var tracker = new SyncLatencyTracker();

        tracker.RecordChangeDetected("test.txt");
        Thread.Sleep(50); // Simulate some processing time
        long latency = tracker.RecordSyncCompleted("test.txt");

        Assert.True(latency >= 40, $"Latency should be at least 40ms, got {latency}");
    }

    [Fact]
    public void LatencyTracker_CompletionWithoutStart_ReturnsNegative()
    {
        var tracker = new SyncLatencyTracker();
        long latency = tracker.RecordSyncCompleted("unknown.txt");
        Assert.Equal(-1, latency);
    }

    [Fact]
    public void LatencyTracker_GetMetrics_ReturnsAggregates()
    {
        var tracker = new SyncLatencyTracker();

        tracker.RecordChangeDetected("a.txt");
        tracker.RecordChangeDetected("b.txt");
        Thread.Sleep(30);
        tracker.RecordSyncCompleted("a.txt");
        tracker.RecordSyncCompleted("b.txt");

        var metrics = tracker.GetMetrics();
        Assert.Equal(2, metrics.TotalChangesDetected);
        Assert.Equal(2, metrics.TotalSyncsCompleted);
        Assert.True(metrics.AverageLatencyMs >= 20);
        Assert.True(metrics.MinLatencyMs >= 0);
        Assert.True(metrics.MaxLatencyMs >= metrics.MinLatencyMs);
    }

    [Fact]
    public void LatencyTracker_PendingChanges_CountsCorrectly()
    {
        var tracker = new SyncLatencyTracker();

        tracker.RecordChangeDetected("a.txt");
        tracker.RecordChangeDetected("b.txt");
        tracker.RecordChangeDetected("c.txt");

        var metrics = tracker.GetMetrics();
        Assert.Equal(3, metrics.PendingChanges);

        tracker.RecordSyncCompleted("a.txt");
        metrics = tracker.GetMetrics();
        Assert.Equal(2, metrics.PendingChanges);
    }
}
