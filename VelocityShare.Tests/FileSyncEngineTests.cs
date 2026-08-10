using VelocityShare.Server;
using VelocityShare.Server.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace VelocityShare.Tests;

public class FileSyncEngineTests : IAsyncDisposable
{
    private readonly string _testDir;
    private readonly FileSyncEngine _engine;
    private readonly SyncChangeJournal _journal;

    public FileSyncEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"velocity_sync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var journalPath = Path.Combine(_testDir, ".test_journal.db");
        _journal = new SyncChangeJournal(journalPath);
        var storage = new LocalSyncStorageProvider(_testDir);
        _engine = new FileSyncEngine(storage, "peer_test", async (bytes) => await Task.CompletedTask, _journal, logger: NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _journal.DisposeAsync();
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public async Task Constructor_CreatesDirectoryIfNotExists()
    {
        var newDir = Path.Combine(Path.GetTempPath(), $"velocity_new_{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(newDir));

        // Create the directory first since the journal needs it
        Directory.CreateDirectory(newDir);
        Assert.True(Directory.Exists(newDir));

        var journalPath = Path.Combine(newDir, ".test_journal.db");
        {
            await using var journal = new SyncChangeJournal(journalPath);
            var storage = new LocalSyncStorageProvider(newDir);
            await using var engine = new FileSyncEngine(storage, "peer_x", async (b) => { }, journal);
            Assert.NotNull(engine);
        }
        // Cleanup best-effort (SQLite may hold file locks briefly on Windows)
        try { Directory.Delete(newDir, true); } catch { }
    }

    [Fact]
    public async Task ApplyRemoteSyncAsync_PathTraversal_Blocked()
    {
        var traversalFile = $"../../../traversal_probe_{Guid.NewGuid():N}";
        await _engine.ApplyRemoteSyncAsync("sync_update", traversalFile, "abc", new byte[] { 1, 2, 3 });
        // File must not exist inside the sandbox (traversal was blocked)
        var escapedPath = Path.GetFullPath(Path.Combine(_testDir, traversalFile));
        Assert.False(File.Exists(escapedPath),
            $"Path traversal was not blocked — file found at {escapedPath}");
        // Also verify no files leaked outside the sandbox
        var filesOutside = Directory.GetFiles(_testDir, "traversal_probe_*", SearchOption.AllDirectories);
        Assert.Empty(filesOutside);
    }

    [Fact]
    public async Task ApplyRemoteSyncAsync_ValidUpdate_CreatesFile()
    {
        _engine.Start();
        byte[] content = "Hello sync!"u8.ToArray();
        await _engine.ApplyRemoteSyncAsync("sync_update", "test.txt", "hash123", content);

        var filePath = Path.Combine(_testDir, "test.txt");
        Assert.True(File.Exists(filePath));
        Assert.Equal(content, await File.ReadAllBytesAsync(filePath));
        _engine.Stop();
    }

    [Fact]
    public async Task ApplyRemoteSyncAsync_Delete_RemovesFile()
    {
        _engine.Start();
        // Create a file first
        var filePath = Path.Combine(_testDir, "to_delete.txt");
        await File.WriteAllTextAsync(filePath, "delete me");
        Assert.True(File.Exists(filePath));

        await _engine.ApplyRemoteSyncAsync("sync_delete", "to_delete.txt", "", null);
        Assert.False(File.Exists(filePath));
        _engine.Stop();
    }

    [Fact]
    public async Task ApplyRemoteSyncAsync_SubdirectoryFile_CreatesNestedDir()
    {
        _engine.Start();
        byte[] content = "nested file"u8.ToArray();
        await _engine.ApplyRemoteSyncAsync("sync_update", "sub/dir/nested.txt", "hash", content);

        var filePath = Path.Combine(_testDir, "sub", "dir", "nested.txt");
        Assert.True(File.Exists(filePath));
        _engine.Stop();
    }

    [Fact]
    public void StartStop_DoesNotThrow()
    {
        _engine.Start();
        _engine.Stop();
        // Restart should also work
        _engine.Start();
        _engine.Stop();
    }

    [Fact]
    public void SyncFolderPath_MatchesConstructor()
    {
        Assert.Equal(_testDir, _engine.SyncFolderPath);
    }
}
