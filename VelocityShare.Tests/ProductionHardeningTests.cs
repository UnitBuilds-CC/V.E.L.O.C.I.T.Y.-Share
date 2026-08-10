using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using VelocityShare.Server;
using VelocityShare.Server.Sync;

namespace VelocityShare.Tests;

/// <summary>
/// Tests for production hardening fixes: concurrency safety, TOCTOU, journal consistency.
/// </summary>
public class ProductionHardeningTests
{
    // ── ShareLinkManager CAS Loop (TOCTOU Fix) ──

    [Fact]
    public void RecordDownload_ConcurrentUpdates_NoLostCounts()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), maxDownloads: 10_000);

        // Fire 100 concurrent downloads
        const int concurrentDownloads = 100;
        var tasks = new Task[concurrentDownloads];
        for (int i = 0; i < concurrentDownloads; i++)
            tasks[i] = Task.Run(() => manager.RecordDownload(link.Id));

        Task.WaitAll(tasks);

        // All 100 should be counted — no lost updates from TOCTOU race
        var result = manager.ValidateLink(link.Id);
        Assert.NotNull(result);
        Assert.Equal(concurrentDownloads, result.DownloadCount);
    }

    [Fact]
    public void RecordDownload_ConcurrentReachesMax_RemovesLink()
    {
        var manager = new ShareLinkManager();
        const int maxDownloads = 10;
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), maxDownloads: maxDownloads);

        // Fire more concurrent downloads than the max
        var tasks = new Task[maxDownloads * 2];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() => manager.RecordDownload(link.Id));

        Task.WaitAll(tasks);

        // Link should have been removed once maxDownloads was reached
        Assert.Equal(0, manager.ActiveLinkCount);
    }

    // ── SyncChangeJournal Consistency ──

    [Fact]
    public async Task Journal_RecordAndGetPending_Roundtrip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_journal_{Guid.NewGuid():N}.db");
        await using var journal = new SyncChangeJournal(dbPath);

        await journal.RecordChangeAsync("peer1", "docs/report.pdf", SyncChangeJournal.ChangeType.Create);
        await journal.RecordChangeAsync("peer1", "docs/notes.txt", SyncChangeJournal.ChangeType.Modify);

        var pending = await journal.GetPendingAsync("peer1");
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, e => e.RelativePath == "docs/report.pdf" && e.Type == SyncChangeJournal.ChangeType.Create);
        Assert.Contains(pending, e => e.RelativePath == "docs/notes.txt" && e.Type == SyncChangeJournal.ChangeType.Modify);

        // Cleanup temp file
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task Journal_RecordChange_DeduplicatesPendingEntry()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_journal_{Guid.NewGuid():N}.db");
        await using var journal = new SyncChangeJournal(dbPath);

        // Record Create then Modify for same path — should update, not insert
        await journal.RecordChangeAsync("peer1", "file.txt", SyncChangeJournal.ChangeType.Create);
        await journal.RecordChangeAsync("peer1", "file.txt", SyncChangeJournal.ChangeType.Modify);

        var count = await journal.CountPendingAsync("peer1");
        Assert.Equal(1, count);

        var pending = await journal.GetPendingAsync("peer1");
        Assert.Single(pending);
        Assert.Equal(SyncChangeJournal.ChangeType.Modify, pending[0].Type);

        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task Journal_MarkInFlight_RemovesFromPending()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_journal_{Guid.NewGuid():N}.db");
        await using var journal = new SyncChangeJournal(dbPath);

        await journal.RecordChangeAsync("peer1", "file.txt", SyncChangeJournal.ChangeType.Create);
        var pending = await journal.GetPendingAsync("peer1");
        Assert.Single(pending);

        await journal.MarkInFlightAsync(pending[0].Id);

        // Should no longer appear as pending (status is now InFlight)
        var afterMark = await journal.GetPendingAsync("peer1");
        Assert.Empty(afterMark);

        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task Journal_MarkCompleted_RemovesEntry()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_journal_{Guid.NewGuid():N}.db");
        await using var journal = new SyncChangeJournal(dbPath);

        await journal.RecordChangeAsync("peer1", "file.txt", SyncChangeJournal.ChangeType.Create);
        var pending = await journal.GetPendingAsync("peer1");

        await journal.MarkInFlightAsync(pending[0].Id);
        await journal.MarkCompletedAsync(pending[0].Id);

        Assert.Equal(0, await journal.CountPendingAsync("peer1"));

        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task Journal_ConcurrentRecordAndRead_ConsistentResults()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_journal_{Guid.NewGuid():N}.db");
        await using var journal = new SyncChangeJournal(dbPath);

        // Concurrently record 20 changes and read pending — should not corrupt
        var writeTasks = new Task[20];
        for (int i = 0; i < 20; i++)
        {
            int idx = i;
            writeTasks[i] = journal.RecordChangeAsync("peer1", $"file{idx}.txt", SyncChangeJournal.ChangeType.Create);
        }
        Task.WaitAll(writeTasks);

        var count = await journal.CountPendingAsync("peer1");
        Assert.Equal(20, count);

        var pending = await journal.GetPendingAsync("peer1");
        Assert.Equal(20, pending.Count);

        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task Journal_ClearPeer_RemovesAllEntries()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_journal_{Guid.NewGuid():N}.db");
        await using var journal = new SyncChangeJournal(dbPath);

        await journal.RecordChangeAsync("peer1", "a.txt", SyncChangeJournal.ChangeType.Create);
        await journal.RecordChangeAsync("peer1", "b.txt", SyncChangeJournal.ChangeType.Modify);
        await journal.RecordChangeAsync("peer2", "c.txt", SyncChangeJournal.ChangeType.Delete);

        await journal.ClearPeerAsync("peer1");

        Assert.Equal(0, await journal.CountPendingAsync("peer1"));
        Assert.Equal(1, await journal.CountPendingAsync("peer2")); // peer2 unaffected

        try { File.Delete(dbPath); } catch { }
    }

    // ── FileSyncEngine Ref Count (Concurrency Safety) ──

    [Fact]
    public async Task FileSyncEngine_ConcurrentRemoteChanges_TracksRefCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var journalPath = Path.Combine(tempDir, ".test_journal.db");
            await using var journal = new SyncChangeJournal(journalPath);
            var storage = new LocalSyncStorageProvider(tempDir);
            await using var engine = new FileSyncEngine(storage, "peer_test", async (bytes) => await Task.CompletedTask, journal, logger: NullLogger.Instance);
            engine.Start();

            // Simulate concurrent remote file writes — the ref count should handle them
            var tasks = new Task[5];
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                tasks[idx] = Task.Run(async () =>
                {
                    byte[] content = System.Text.Encoding.UTF8.GetBytes($"content {idx}");
                    await engine.ApplyRemoteSyncAsync("sync_update", $"concurrent_{idx}.txt", $"hash_{idx}", content);
                });
            }
            await Task.WhenAll(tasks);

            // All 5 files should exist — no crash from ref count issues
            for (int i = 0; i < 5; i++)
                Assert.True(File.Exists(Path.Combine(tempDir, $"concurrent_{i}.txt")));

            engine.Stop();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
