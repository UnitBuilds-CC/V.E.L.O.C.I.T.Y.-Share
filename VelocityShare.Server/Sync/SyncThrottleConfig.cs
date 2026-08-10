using System;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Throttle profile presets for common usage scenarios.
/// </summary>
public enum SyncThrottleProfile
{
    /// <summary>No limits — sync as fast as possible.</summary>
    Unthrottled,

    /// <summary>Balanced: moderate limits, good for normal use.</summary>
    Balanced,

    /// <summary>Throttled: low resource usage, won't interrupt work.</summary>
    Throttled,

    /// <summary>Background: minimal resource usage, sync only when idle.</summary>
    Background,

    /// <summary>Custom: user-specified limits (see ManualLimits).</summary>
    Custom
}

/// <summary>
/// Manual resource limits for sync operations.
/// When a value is null or 0, that dimension is unconstrained.
/// </summary>
public sealed class SyncManualLimits
{
    /// <summary>Max network bandwidth in bytes/sec. 0 = unlimited.</summary>
    public long MaxBandwidthBytesPerSec { get; set; }

    /// <summary>Max CPU utilization percentage (0-100). 0 = unlimited.</summary>
    public int MaxCpuPercent { get; set; }

    /// <summary>Max disk I/O operations per second. 0 = unlimited.</summary>
    public int MaxDiskIops { get; set; }

    /// <summary>Max disk throughput in bytes/sec. 0 = unlimited.</summary>
    public long MaxDiskBytesPerSec { get; set; }

    /// <summary>Minimum free disk space in bytes before sync pauses. 0 = disabled.</summary>
    public long MinFreeDiskSpaceBytes { get; set; }
}

/// <summary>
/// Complete throttle configuration combining a profile preset with optional manual overrides.
/// </summary>
public sealed class SyncThrottleConfig
{
    public SyncThrottleProfile Profile { get; set; } = SyncThrottleProfile.Balanced;
    public SyncManualLimits ManualLimits { get; set; } = new();

    /// <summary>Whether auto-adaptive mode is enabled (adjusts limits based on system load).</summary>
    public bool AutoAdaptive { get; set; } = true;

    /// <summary>Auto-adaptive: target CPU utilization percentage.</summary>
    public int AutoTargetCpuPercent { get; set; } = 30;

    /// <summary>Auto-adaptive: target bandwidth utilization percentage of available.</summary>
    public int AutoTargetBandwidthPercent { get; set; } = 50;

    /// <summary>Adaptive debounce: minimum debounce interval in ms.</summary>
    public int MinDebounceMs { get; set; } = 200;

    /// <summary>Adaptive debounce: maximum debounce interval in ms (during rapid changes).</summary>
    public int MaxDebounceMs { get; set; } = 5000;

    /// <summary>Adaptive debounce: stability window in ms — no changes for this long triggers sync.</summary>
    public int StabilityWindowMs { get; set; } = 2000;

    /// <summary>Adaptive debounce: rapid change threshold — changes per second above this extends debounce.</summary>
    public int RapidChangeThreshold { get; set; } = 10;

    /// <summary>
    /// Resolve the effective limits for the current profile, merging with any manual overrides.
    /// When storageType is "local", bandwidth limits are raised since there's no network bottleneck.
    /// </summary>
    public EffectiveLimits Resolve(string storageType = "local")
    {
        bool isLocal = storageType == "local";

        var limits = Profile switch
        {
            SyncThrottleProfile.Unthrottled => new EffectiveLimits
            {
                MaxBandwidthBytesPerSec = long.MaxValue,
                MaxCpuPercent = 100,
                MaxDiskIops = int.MaxValue,
                MaxDiskBytesPerSec = long.MaxValue,
                MinFreeDiskSpaceBytes = 0,
                DebounceMs = 100,
                StabilityWindowMs = 500,
            },
            SyncThrottleProfile.Balanced => new EffectiveLimits
            {
                // Local: 100 MB/s (comfortable for local, still a real cap)
                // Network: 50 MB/s (good LAN/WiFi speed)
                MaxBandwidthBytesPerSec = isLocal ? 100L * 1024 * 1024 : 50L * 1024 * 1024,
                MaxCpuPercent = 25,
                MaxDiskIops = isLocal ? 500 : 200,
                MaxDiskBytesPerSec = isLocal ? 200L * 1024 * 1024 : 100L * 1024 * 1024,
                MinFreeDiskSpaceBytes = 500 * 1024 * 1024,    // 500 MB
                DebounceMs = 800,
                StabilityWindowMs = 2000,
            },
            SyncThrottleProfile.Throttled => new EffectiveLimits
            {
                // Local: 25 MB/s (won't interrupt work, files still move)
                // Network: 10 MB/s (won't saturate link)
                MaxBandwidthBytesPerSec = isLocal ? 25L * 1024 * 1024 : 10L * 1024 * 1024,
                MaxCpuPercent = 10,
                MaxDiskIops = isLocal ? 200 : 50,
                MaxDiskBytesPerSec = isLocal ? 50L * 1024 * 1024 : 20L * 1024 * 1024,
                MinFreeDiskSpaceBytes = 1L * 1024 * 1024 * 1024, // 1 GB
                DebounceMs = 2000,
                StabilityWindowMs = 3000,
            },
            SyncThrottleProfile.Background => new EffectiveLimits
            {
                // Local: 5 MB/s (trickle — sync when idle)
                // Network: 2 MB/s (barely a trickle)
                MaxBandwidthBytesPerSec = isLocal ? 5L * 1024 * 1024 : 2L * 1024 * 1024,
                MaxCpuPercent = 3,
                MaxDiskIops = isLocal ? 50 : 20,
                MaxDiskBytesPerSec = isLocal ? 10L * 1024 * 1024 : 5L * 1024 * 1024,
                MinFreeDiskSpaceBytes = 2L * 1024 * 1024 * 1024, // 2 GB
                DebounceMs = 4000,
                StabilityWindowMs = 5000,
            },
            SyncThrottleProfile.Custom => new EffectiveLimits
            {
                MaxBandwidthBytesPerSec = ManualLimits.MaxBandwidthBytesPerSec > 0 ? ManualLimits.MaxBandwidthBytesPerSec : long.MaxValue,
                MaxCpuPercent = Math.Clamp(ManualLimits.MaxCpuPercent, 0, 100) is var cpu && cpu > 0 ? cpu : 100,
                MaxDiskIops = ManualLimits.MaxDiskIops > 0 ? ManualLimits.MaxDiskIops : int.MaxValue,
                MaxDiskBytesPerSec = ManualLimits.MaxDiskBytesPerSec > 0 ? ManualLimits.MaxDiskBytesPerSec : long.MaxValue,
                MinFreeDiskSpaceBytes = Math.Max(0, ManualLimits.MinFreeDiskSpaceBytes),
                DebounceMs = Math.Max(1, MinDebounceMs),
                StabilityWindowMs = Math.Max(100, StabilityWindowMs),
            },
            _ => new EffectiveLimits()
        };

        // Auto-adaptive overrides
        if (AutoAdaptive && Profile != SyncThrottleProfile.Unthrottled)
        {
            limits.AutoTargetCpuPercent = AutoTargetCpuPercent;
            limits.AutoTargetBandwidthPercent = AutoTargetBandwidthPercent;
            limits.IsAutoAdaptive = true;
        }

        return limits;
    }
}

/// <summary>
/// Resolved effective limits after profile + manual merge. Used by the rate limiter at runtime.
/// </summary>
public sealed class EffectiveLimits
{
    public long MaxBandwidthBytesPerSec { get; set; } = long.MaxValue;
    public int MaxCpuPercent { get; set; } = 100;
    public int MaxDiskIops { get; set; } = int.MaxValue;
    public long MaxDiskBytesPerSec { get; set; } = long.MaxValue;
    public long MinFreeDiskSpaceBytes { get; set; }
    public int DebounceMs { get; set; } = 500;
    public int StabilityWindowMs { get; set; } = 2000;
    public bool IsAutoAdaptive { get; set; }
    public int AutoTargetCpuPercent { get; set; } = 30;
    public int AutoTargetBandwidthPercent { get; set; } = 50;
}
