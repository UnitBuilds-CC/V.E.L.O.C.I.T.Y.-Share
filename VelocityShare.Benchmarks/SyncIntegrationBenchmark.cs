using System.Diagnostics;
using VelocityShare.Server;
using VelocityShare.Server.Sync;

namespace VelocityShare.Benchmarks;

/// <summary>
/// End-to-end sync integration benchmark.
/// Measures actual file sync throughput, latency, and throttle effectiveness
/// with real file I/O and all throttle profiles.
/// </summary>
public static class SyncIntegrationBenchmark
{
    public static async Task RunAsync()
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║     V.E.L.O.C.I.T.Y. Share — Sync Integration Benchmark            ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  OS:         {Environment.OSVersion.VersionString,-52} ║");
        Console.WriteLine($"║  CPUs:       {Environment.ProcessorCount,-52} ║");
        Console.WriteLine($"║  Runtime:    {Environment.Version,-52} ║");
        Console.WriteLine($"║  GC Memory:  {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1} MB initial{new string(' ', 39)} ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // ── Test 1: Raw file I/O throughput (baseline) ──────────────
        await BenchmarkRawFileIO();

        // ── Test 2: Block delta detection throughput ────────────────
        await BenchmarkBlockDeltaDetection();

        // ── Test 3: Rate limiter effective throughput ───────────────
        await BenchmarkRateLimiterThroughput();

        // ── Test 4: Adaptive scheduler under load ───────────────────
        BenchmarkAdaptiveScheduler();

        // ── Test 5: End-to-end sync pipeline ────────────────────────
        await BenchmarkEndToEndSync();

        // ── Test 6: Latency tracker overhead ────────────────────────
        BenchmarkLatencyTracker();

        // ── Test 7: Throttle profile comparison ─────────────────────
        await BenchmarkThrottleProfiles();

        // ── Test 8: Component overhead profile (RAM, CPU, allocations) ─
        await BenchmarkComponentOverhead();

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Benchmark complete.");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
    }

    static async Task BenchmarkRawFileIO()
    {
        PrintHeader("TEST 1: Raw File I/O Throughput (Baseline)");

        var sizes = new[] { ("Small (1KB)", 1024), ("Medium (64KB)", 64 * 1024), ("Large (1MB)", 1024 * 1024), ("Huge (16MB)", 16 * 1024 * 1024) };
        var tempDir = CreateTempDir();

        try
        {
            Console.WriteLine("  ┌─────────────────┬──────────┬──────────┬──────────────┐");
            Console.WriteLine("  │ File Size       │ Write    │ Read     │ Throughput   │");
            Console.WriteLine("  ├─────────────────┼──────────┼──────────┼──────────────┤");

            foreach (var (name, size) in sizes)
            {
                var data = RandomData(size);
                var filePath = Path.Combine(tempDir, $"bench_{size}.bin");

                // Write benchmark
                var sw = Stopwatch.StartNew();
                int iterations = Math.Max(1, 100_000_000 / size);
                for (int i = 0; i < iterations; i++)
                    await File.WriteAllBytesAsync(filePath, data);
                sw.Stop();
                double writeMBs = (size * iterations) / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
                double writeMs = sw.Elapsed.TotalMilliseconds / iterations;

                // Read benchmark
                sw.Restart();
                for (int i = 0; i < iterations; i++)
                    await File.ReadAllBytesAsync(filePath);
                sw.Stop();
                double readMBs = (size * iterations) / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
                double readMs = sw.Elapsed.TotalMilliseconds / iterations;

                Console.WriteLine($"  │ {name,-15} │ {writeMs,6:F2}ms │ {readMs,6:F2}ms │ W:{writeMBs,6:F1} R:{readMBs,6:F1} MB/s │");
            }
            Console.WriteLine("  └─────────────────┴──────────┴──────────┴──────────────┘");
        }
        finally { CleanupDir(tempDir); }
        Console.WriteLine();
    }

    static async Task BenchmarkBlockDeltaDetection()
    {
        PrintHeader("TEST 2: Block Delta Detection (SHA-256 via Rust FFI)");

        var sizes = new[] { ("256KB", 256 * 1024), ("1MB", 1024 * 1024), ("4MB", 4 * 1024 * 1024), ("16MB", 16 * 1024 * 1024), ("64MB", 64 * 1024 * 1024) };
        var tempDir = CreateTempDir();

        try
        {
            Console.WriteLine("  ┌──────────┬──────────┬──────────┬──────────────┬──────────┐");
            Console.WriteLine("  │ File Size│ Blocks   │ Time     │ Throughput   │ MB/block │");
            Console.WriteLine("  ├──────────┼──────────┼──────────┼──────────────┼──────────┤");

            foreach (var (name, size) in sizes)
            {
                var filePath = Path.Combine(tempDir, $"delta_{size}.bin");
                File.WriteAllBytes(filePath, RandomData(size));

                var detector = new BlockDeltaDetector();
                var storage = new LocalSyncStorageProvider(tempDir);
                var fileName = $"delta_{size}.bin";

                var sw = Stopwatch.StartNew();
                int runs = size > 16 * 1024 * 1024 ? 3 : (size > 4 * 1024 * 1024 ? 5 : 10);
                for (int r = 0; r < runs; r++)
                {
                    var hashes = await detector.ComputeBlockHashesAsync(storage, fileName, size);
                }
                sw.Stop();
                await storage.DisposeAsync();

                double totalSec = sw.Elapsed.TotalSeconds;
                double mbPerSec = (size * runs) / 1024.0 / 1024.0 / totalSec;
                double msPerRun = totalSec * 1000.0 / runs;
                int blocks = (size + BlockDeltaDetector.DefaultBlockSize - 1) / BlockDeltaDetector.DefaultBlockSize;

                Console.WriteLine($"  │ {name,-8} │ {blocks,-8} │ {msPerRun,6:F1}ms │ {mbPerSec,7:F1} MB/s │ {BlockDeltaDetector.DefaultBlockSize / 1024.0:F0}KB    │");
            }
            Console.WriteLine("  └──────────┴──────────┴──────────┴──────────────┴──────────┘");
        }
        finally { CleanupDir(tempDir); }
        Console.WriteLine();
    }

    static async Task BenchmarkRateLimiterThroughput()
    {
        PrintHeader("TEST 3: Rate Limiter Effective Throughput (bandwidth-only, no CPU/disk limits)");

        var profiles = new[]
        {
            ("Unlimited", long.MaxValue),
            ("100 MB/s (Balanced/local)", 100L * 1024 * 1024),
            ("50 MB/s (Balanced/network)", 50L * 1024 * 1024),
            ("25 MB/s (Throttled/local)", 25L * 1024 * 1024),
            ("10 MB/s (Throttled/network)", 10L * 1024 * 1024),
            ("5 MB/s (Background/local)", 5L * 1024 * 1024),
            ("2 MB/s (Background/net)", 2L * 1024 * 1024),
            ("500 KB/s", 500L * 1024),
        };

        Console.WriteLine("  ┌──────────────────────┬───────────────┬───────────┬───────────┬──────────┐");
        Console.WriteLine("  │ Profile              │ Limit         │ Sustained │ Throttle  │ Blocks   │");
        Console.WriteLine("  │                      │               │ MB/s      │ Delay     │          │");
        Console.WriteLine("  ├──────────────────────┼───────────────┼───────────┼───────────┼──────────┤");

        foreach (var (name, limit) in profiles)
        {
            var limits = new EffectiveLimits
            {
                MaxBandwidthBytesPerSec = limit,
                MaxCpuPercent = 100,
                MaxDiskIops = int.MaxValue,
                MaxDiskBytesPerSec = long.MaxValue
            };
            using var limiter = new SyncRateLimiter(limits);

            int blockSize = 65536; // 64KB blocks
            // Drain the initial burst tokens so we measure SUSTAINED throughput
            if (limit != long.MaxValue)
                await limiter.ThrottleBandwidthAsync(limit); // consume 1s of tokens

            // Now measure sustained throughput (3 seconds worth of data)
            int blockCount = limit == long.MaxValue ? 10000 : (int)(limit * 3 / blockSize);

            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blockCount; i++)
            {
                await limiter.ThrottleBandwidthAsync(blockSize);
                totalBytes += blockSize;
            }
            sw.Stop();

            double measuredMBs = totalBytes / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
            double limitMBs = limit == long.MaxValue ? double.PositiveInfinity : limit / 1024.0 / 1024.0;
            double overheadMs = limiter.TotalThrottleDelayMs;
            string limitStr = limit == long.MaxValue ? "none (baseline)" : $"{limitMBs:F0} MB/s";
            string throughputStr = limit == long.MaxValue ? $"{measuredMBs:F0}*" : $"{measuredMBs,6:F1}";

            Console.WriteLine($"  │ {name,-20} │ {limitStr,-15} │ {throughputStr,-9} │ {overheadMs,6:F0}ms   │ {blockCount,-7} │");
        }
        Console.WriteLine("  └──────────────────────┴───────────────┴───────────┴───────────┴──────────┘");
        Console.WriteLine("  * Unlimited baseline measures async call overhead, not real throughput");
        Console.WriteLine();
    }

    static void BenchmarkAdaptiveScheduler()
    {
        PrintHeader("TEST 4: Adaptive Scheduler — Debounce Behavior");

        var scenarios = new[]
        {
            ("Idle (1 change)", 1),
            ("Light (5 changes)", 5),
            ("Moderate (20 changes)", 20),
            ("Heavy (50 changes)", 50),
            ("Burst (200 changes)", 200),
            ("Storm (1000 changes)", 1000),
        };

        Console.WriteLine("  ┌────────────────────┬──────────┬───────────┬───────────┬──────────┐");
        Console.WriteLine("  │ Scenario           │ Changes  │ Debounce  │ Stable?   │ Overhead │");
        Console.WriteLine("  ├────────────────────┼──────────┼───────────┼───────────┼──────────┤");

        foreach (var (name, count) in scenarios)
        {
            var limits = new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 };
            using var scheduler = new AdaptiveSyncScheduler(limits);

            var sw = Stopwatch.StartNew();
            int lastDebounce = 0;
            for (int i = 0; i < count; i++)
            {
                lastDebounce = scheduler.NotifyChange();
            }
            sw.Stop();

            var stats = scheduler.GetStats();
            double overheadUs = sw.Elapsed.TotalMicroseconds / count;

            Console.WriteLine($"  │ {name,-18} │ {count,-8} │ {lastDebounce,5}ms   │ {(stats.IsStable ? "Yes      " : "No       ")} │ {overheadUs,5:F1}µs/op │");
        }
        Console.WriteLine("  └────────────────────┴──────────┴───────────┴───────────┴──────────┘");
        Console.WriteLine();
    }

    static async Task BenchmarkEndToEndSync()
    {
        PrintHeader("TEST 5: End-to-End Sync Pipeline (no network — local loopback)");

        var tempDir = CreateTempDir();
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);

        try
        {
            // Create test files
            var testFiles = new[] { ("tiny.txt", 100), ("small.txt", 1024), ("medium.txt", 64 * 1024), ("large.bin", 1024 * 1024), ("huge.bin", 16 * 1024 * 1024) };
            foreach (var (name, size) in testFiles)
                File.WriteAllBytes(Path.Combine(srcDir, name), RandomData(size));

            Console.WriteLine("  ┌──────────────────┬──────────┬──────────┬──────────────┬──────────┐");
            Console.WriteLine("  │ File             │ Size     │ Hash     │ Catalog      │ Total    │");
            Console.WriteLine("  ├──────────────────┼──────────┼──────────┼──────────────┼──────────┤");

            var journalPath = Path.Combine(tempDir, ".bench_journal.db");
            await using var journal = new SyncChangeJournal(journalPath);
            var storage = new LocalSyncStorageProvider(srcDir);

            foreach (var (name, size) in testFiles)
            {
                var sw = Stopwatch.StartNew();
                int runs = size > 1024 * 1024 ? 5 : 20;

                for (int r = 0; r < runs; r++)
                {
                    // Read file
                    byte[] content = await storage.ReadFileAsync(name);
                    // Hash
                    byte[] hash = VelocityShareCrypto.HashChunk(content);
                    string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                    // Catalog update
                    var mtime = await storage.GetLastModifiedAsync(name);
                    // Journal record
                    await journal.RecordChangeAsync("peer_bench", name, SyncChangeJournal.ChangeType.Modify);
                }
                sw.Stop();

                double msPerOp = sw.Elapsed.TotalMilliseconds / runs;
                double mbPerSec = (size * runs) / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
                string sizeStr = size >= 1024 * 1024 ? $"{size / 1024.0 / 1024.0:F1}MB" : $"{size / 1024.0:F0}KB";

                Console.WriteLine($"  │ {name,-16} │ {sizeStr,-8} │ {msPerOp,5:F2}ms │ {msPerOp,6:F2}ms  │ {mbPerSec,5:F1} MB/s │");
            }

            await storage.DisposeAsync();
            Console.WriteLine("  └──────────────────┴──────────┴──────────┴──────────────┴──────────┘");
        }
        finally { CleanupDir(tempDir); }
        Console.WriteLine();
    }

    static void BenchmarkLatencyTracker()
    {
        PrintHeader("TEST 6: Latency Tracker Overhead");

        var tracker = new SyncLatencyTracker();
        int count = 100_000;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            tracker.RecordChangeDetected($"file_{i % 1000}.txt");
            tracker.RecordSyncCompleted($"file_{i % 1000}.txt");
        }
        sw.Stop();

        double perOpUs = sw.Elapsed.TotalMicroseconds / (count * 2);
        var metrics = tracker.GetMetrics();

        Console.WriteLine($"  Operations:        {count * 2:N0} (record + complete)");
        Console.WriteLine($"  Total time:        {sw.Elapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  Per-operation:     {perOpUs:F2} µs");
        Console.WriteLine($"  Avg latency:       {metrics.AverageLatencyMs:F3}ms");
        Console.WriteLine($"  P95 latency:       {metrics.P95LatencyMs:F3}ms");
        Console.WriteLine($"  Memory (samples):  {count} rolling samples retained");
        Console.WriteLine();
    }

    static async Task BenchmarkThrottleProfiles()
    {
        PrintHeader("TEST 7: Throttle Profile — End-to-End Sync Simulation");

        var tempDir = CreateTempDir();
        try
        {
            // Create a 4MB test file
            var testFile = Path.Combine(tempDir, "sync_test.bin");
            File.WriteAllBytes(testFile, RandomData(4 * 1024 * 1024));
            int fileSize = 4 * 1024 * 1024;

            var profiles = new[]
            {
                ("Unthrottled", SyncThrottleProfile.Unthrottled),
                ("Balanced", SyncThrottleProfile.Balanced),
                ("Throttled", SyncThrottleProfile.Throttled),
                ("Background", SyncThrottleProfile.Background),
            };

            Console.WriteLine("  ┌────────────────┬───────────┬───────────┬───────────┬──────────┬──────────┐");
            Console.WriteLine("  │ Profile        │ Sync Time │ Throughput│ Throttle  │ Debounce │ Scheduler│");
            Console.WriteLine("  │                │ (4MB)     │ (MB/s)    │ Delay     │ (ms)     │ Changes  │");
            Console.WriteLine("  ├────────────────┼───────────┼───────────┼───────────┼──────────┼──────────┤");

            foreach (var (name, profile) in profiles)
            {
                var config = new SyncThrottleConfig { Profile = profile, AutoAdaptive = false };
                var limits = config.Resolve();
                using var rateLimiter = new SyncRateLimiter(limits);
                using var scheduler = new AdaptiveSyncScheduler(limits);
                var latencyTracker = new SyncLatencyTracker();

                // Drain the initial burst tokens so we measure SUSTAINED throughput
                if (limits.MaxBandwidthBytesPerSec != long.MaxValue)
                    await rateLimiter.ThrottleBandwidthAsync(limits.MaxBandwidthBytesPerSec);

                // Simulate: 10 file changes, then sync each
                for (int i = 0; i < 10; i++)
                {
                    int debounce = scheduler.NotifyChange();
                    latencyTracker.RecordChangeDetected($"file_{i}.bin");
                }

                // Now sync — measure sustained throughput
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++)
                {
                    // Read + hash + throttle + send
                    byte[] content = File.ReadAllBytes(testFile);
                    byte[] hash = VelocityShareCrypto.HashChunk(content);
                    await rateLimiter.ThrottleAsync(content.Length);
                    latencyTracker.RecordSyncCompleted($"file_{i}.bin");
                    scheduler.NotifySyncTriggered();
                }
                sw.Stop();

                double totalMB = (fileSize * 10) / 1024.0 / 1024.0;
                double mbPerSec = totalMB / sw.Elapsed.TotalSeconds;
                var throttleStatus = rateLimiter.GetStatus();
                var schedStats = scheduler.GetStats();
                var latMetrics = latencyTracker.GetMetrics();

                Console.WriteLine($"  │ {name,-14} │ {sw.Elapsed.TotalMilliseconds,6:F0}ms │ {mbPerSec,6:F1}    │ {throttleStatus.TotalThrottleDelayMs,6:F0}ms  │ {schedStats.CurrentDebounceMs,-7} │ {schedStats.TotalChanges,-7} │");
            }
            Console.WriteLine("  └────────────────┴───────────┴───────────┴───────────┴──────────┴──────────┘");
        }
        finally { CleanupDir(tempDir); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    static async Task BenchmarkComponentOverhead()
    {
        PrintHeader("TEST 8: Component Overhead Profile (RAM, CPU, Allocations)");

        // Force a GC before measuring baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baselineMemory = GC.GetTotalMemory(forceFullCollection: false);

        Console.WriteLine("  ── 8a: Memory Footprint (heap bytes per component) ──");
        Console.WriteLine();

        // Measure each component's memory footprint in isolation
        long memBefore, memAfter;

        // RateLimiter (unthrottled)
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        memBefore = GC.GetTotalMemory(false);
        var unthrottledLimits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = long.MaxValue, MaxCpuPercent = 100,
            MaxDiskIops = int.MaxValue, MaxDiskBytesPerSec = long.MaxValue
        };
        var limiter = new SyncRateLimiter(unthrottledLimits);
        // Let CPU monitor run for a bit to stabilize
        await Task.Delay(200);
        memAfter = GC.GetTotalMemory(false);
        long limiterMem = memAfter - memBefore;
        var limiterStatus = limiter.GetStatus();
        Console.WriteLine($"  │ SyncRateLimiter (unthrottled)  │ {limiterMem,10:N0} bytes ({limiterMem / 1024.0:F1} KB) │");
        limiter.Dispose();

        // RateLimiter (balanced — with auto-adaptive CPU monitor)
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        memBefore = GC.GetTotalMemory(false);
        var balancedLimits = new EffectiveLimits
        {
            MaxBandwidthBytesPerSec = 100L * 1024 * 1024, MaxCpuPercent = 25,
            MaxDiskIops = 500, MaxDiskBytesPerSec = 200L * 1024 * 1024,
            IsAutoAdaptive = true, AutoTargetCpuPercent = 30
        };
        var limiter2 = new SyncRateLimiter(balancedLimits);
        await Task.Delay(200);
        memAfter = GC.GetTotalMemory(false);
        long limiter2Mem = memAfter - memBefore;
        Console.WriteLine($"  │ SyncRateLimiter (balanced+auto)│ {limiter2Mem,10:N0} bytes ({limiter2Mem / 1024.0:F1} KB) │");
        limiter2.Dispose();

        // AdaptiveSyncScheduler
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        memBefore = GC.GetTotalMemory(false);
        var scheduler = new AdaptiveSyncScheduler(new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 });
        memAfter = GC.GetTotalMemory(false);
        long schedulerMem = memAfter - memBefore;
        Console.WriteLine($"  │ AdaptiveSyncScheduler          │ {schedulerMem,10:N0} bytes ({schedulerMem / 1024.0:F1} KB) │");
        scheduler.Dispose();

        // SyncLatencyTracker
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        memBefore = GC.GetTotalMemory(false);
        var tracker = new SyncLatencyTracker();
        memAfter = GC.GetTotalMemory(false);
        long trackerMem = memAfter - memBefore;
        Console.WriteLine($"  │ SyncLatencyTracker (empty)     │ {trackerMem,10:N0} bytes ({trackerMem / 1024.0:F1} KB) │");

        // SyncLatencyTracker with 10K samples
        for (int i = 0; i < 10_000; i++)
        {
            tracker.RecordChangeDetected($"file_{i}.txt");
            tracker.RecordSyncCompleted($"file_{i}.txt");
        }
        memAfter = GC.GetTotalMemory(false);
        long trackerFullMem = memAfter - memBefore;
        Console.WriteLine($"  │ SyncLatencyTracker (10K samples)│{trackerFullMem,10:N0} bytes ({trackerFullMem / 1024.0:F1} KB) │");
        Console.WriteLine();

        // Total pipeline footprint
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        memBefore = GC.GetTotalMemory(false);
        var pLimiter = new SyncRateLimiter(unthrottledLimits);
        var pScheduler = new AdaptiveSyncScheduler(new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 });
        var pTracker = new SyncLatencyTracker();
        await Task.Delay(200);
        memAfter = GC.GetTotalMemory(false);
        long totalPipelineMem = memAfter - memBefore;
        Console.WriteLine($"  Total unthrottled pipeline footprint: {totalPipelineMem:N0} bytes ({totalPipelineMem / 1024.0:F1} KB)");
        Console.WriteLine($"  Process baseline (GC heap):           {baselineMemory:N0} bytes ({baselineMemory / 1024.0 / 1024.0:F1} MB)");
        Console.WriteLine();

        // ── 8b: Per-operation allocation cost ──
        Console.WriteLine("  ── 8b: Per-Operation Allocation Cost ──");
        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────────┬───────────┬──────────────┬──────────┐");
        Console.WriteLine("  │ Operation                    │ Ops       │ Alloc/op     │ CPU/op   │");
        Console.WriteLine("  ├──────────────────────────────┼───────────┼──────────────┼──────────┤");

        // ThrottleBandwidthAsync (unthrottled — no delay)
        {
            using var l = new SyncRateLimiter(unthrottledLimits);
            int ops = 100_000;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBefore = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ops; i++)
                await l.ThrottleBandwidthAsync(65536);
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes();
            long allocPerOp = (allocAfter - allocBefore) / ops;
            double cpuPerOp = sw.Elapsed.TotalMicroseconds / ops;
            Console.WriteLine($"  │ ThrottleBandwidth (unlimited) │ {ops,9:N0} │ {allocPerOp,8} B │ {cpuPerOp,6:F2} µs │");
        }

        // ThrottleAsync (unthrottled — combined bandwidth+disk+cpu)
        {
            using var l = new SyncRateLimiter(unthrottledLimits);
            int ops = 100_000;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBefore = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ops; i++)
                await l.ThrottleAsync(65536);
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes();
            long allocPerOp = (allocAfter - allocBefore) / ops;
            double cpuPerOp = sw.Elapsed.TotalMicroseconds / ops;
            Console.WriteLine($"  │ ThrottleAsync (unlimited)     │ {ops,9:N0} │ {allocPerOp,8} B │ {cpuPerOp,6:F2} µs │");
        }

        // Scheduler.NotifyChange
        {
            var s = new AdaptiveSyncScheduler(new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 });
            int ops = 100_000;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBefore = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ops; i++)
                s.NotifyChange();
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes();
            long allocPerOp = (allocAfter - allocBefore) / ops;
            double cpuPerOp = sw.Elapsed.TotalMicroseconds / ops;
            Console.WriteLine($"  │ Scheduler.NotifyChange        │ {ops,9:N0} │ {allocPerOp,8} B │ {cpuPerOp,6:F2} µs │");
            s.Dispose();
        }

        // LatencyTracker record+complete
        {
            var t = new SyncLatencyTracker();
            int ops = 100_000;
            // Pre-allocate paths to measure component allocations only
            var paths = new string[1000];
            for (int p = 0; p < 1000; p++) paths[p] = $"file_{p}.txt";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBefore = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ops; i++)
            {
                t.RecordChangeDetected(paths[i % 1000]);
                t.RecordSyncCompleted(paths[i % 1000]);
            }
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes();
            long allocPerOp = (allocAfter - allocBefore) / (ops * 2);
            double cpuPerOp = sw.Elapsed.TotalMicroseconds / (ops * 2);
            Console.WriteLine($"  │ LatencyTracker record+complete│ {ops * 2,9:N0} │ {allocPerOp,8} B │ {cpuPerOp,6:F2} µs │");
        }

        // GetStatus snapshot
        {
            using var l = new SyncRateLimiter(unthrottledLimits);
            int ops = 10_000;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBefore = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ops; i++)
                l.GetStatus();
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes();
            long allocPerOp = (allocAfter - allocBefore) / ops;
            double cpuPerOp = sw.Elapsed.TotalMicroseconds / ops;
            Console.WriteLine($"  │ RateLimiter.GetStatus         │ {ops,9:N0} │ {allocPerOp,8} B │ {cpuPerOp,6:F2} µs │");
        }

        Console.WriteLine("  └──────────────────────────────┴───────────┴──────────────┴──────────┘");
        Console.WriteLine();

        // ── 8c: CPU monitor overhead ──
        Console.WriteLine("  ── 8c: CPU Monitor Sampling Cost ──");
        Console.WriteLine();
        {
            using var l = new SyncRateLimiter(balancedLimits);
            // Measure CPU over 2 seconds of idle (just the background monitor running)
            var proc = Process.GetCurrentProcess();
            var cpuBefore = proc.TotalProcessorTime;
            var wallSw = Stopwatch.StartNew();
            await Task.Delay(2000);
            wallSw.Stop();
            var cpuAfter = proc.TotalProcessorTime;
            double cpuUsedMs = (cpuAfter - cpuBefore).TotalMilliseconds;
            double cpuPercent = cpuUsedMs / wallSw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            Console.WriteLine($"  Rate limiter CPU monitor (idle, 2s sample):");
            Console.WriteLine($"    CPU time consumed:  {cpuUsedMs:F1} ms over {wallSw.Elapsed.TotalMilliseconds:F0} ms wall");
            Console.WriteLine($"    CPU utilization:    {cpuPercent:F2}% of total system");
            Console.WriteLine($"    Threads active:     {proc.Threads.Count}");
        }
        Console.WriteLine();

        // ── 8d: Full pipeline simulation (unthrottled) ──
        Console.WriteLine("  ── 8d: Full Pipeline Overhead (1000 file syncs, unthrottled) ──");
        Console.WriteLine();
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memStart = GC.GetTotalMemory(false);

            using var l = new SyncRateLimiter(unthrottledLimits);
            using var s = new AdaptiveSyncScheduler(new EffectiveLimits { DebounceMs = 500, StabilityWindowMs = 2000 });
            var t = new SyncLatencyTracker();

            int fileCount = 1000;
            // Pre-allocate paths to measure component allocations only
            var filePaths = new string[fileCount];
            for (int p = 0; p < fileCount; p++) filePaths[p] = $"file_{p}.txt";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memAfterInit = GC.GetTotalMemory(false);
            long allocBefore = GC.GetTotalAllocatedBytes();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < fileCount; i++)
            {
                string path = filePaths[i];
                s.NotifyChange();
                t.RecordChangeDetected(path);
                await l.ThrottleAsync(65536); // simulate 64KB block
                t.RecordSyncCompleted(path);
                s.NotifySyncTriggered();
            }
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes();
            long memEnd = GC.GetTotalMemory(false);

            long totalAlloc = allocAfter - allocBefore;
            long allocPerSync = totalAlloc / fileCount;
            double cpuPerSync = sw.Elapsed.TotalMicroseconds / fileCount;
            long liveMem = memEnd - memAfterInit;

            Console.WriteLine($"  Sync operations:       {fileCount:N0}");
            Console.WriteLine($"  Total wall time:       {sw.Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine($"  Per-sync CPU:          {cpuPerSync:F2} µs");
            Console.WriteLine($"  Total allocations:     {totalAlloc:N0} bytes ({totalAlloc / 1024.0:F1} KB)");
            Console.WriteLine($"  Allocations per sync:  {allocPerSync:N0} bytes");
            Console.WriteLine($"  Live memory delta:     {liveMem:N0} bytes ({liveMem / 1024.0:F1} KB)");
            Console.WriteLine($"  Throughput equivalent:  {fileCount * 65536.0 / 1024 / 1024 / sw.Elapsed.TotalSeconds:F0} MB/s (64KB blocks, no I/O)");
        }
        Console.WriteLine();
    }

    static void PrintHeader(string title)
    {
        Console.WriteLine($"  ── {title} ──");
        Console.WriteLine();
    }

    static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"veloci_ibench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    static byte[] RandomData(int size)
    {
        var data = new byte[size];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        return data;
    }
}
