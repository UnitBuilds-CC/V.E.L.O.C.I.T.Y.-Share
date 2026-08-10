using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Persistent change journal backed by SQLite.
/// Tracks file changes that need to be synced, surviving process restarts.
/// Each entry records: peer_id, relative_path, change_type, timestamp, block_count, status.
/// </summary>
public sealed class SyncChangeJournal : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public enum ChangeType { Create, Modify, Delete, BlockUpdate }
    public enum JournalStatus { Pending, InFlight, Completed, Failed }

    public sealed record JournalEntry(
        long Id,
        string PeerId,
        string RelativePath,
        ChangeType Type,
        JournalStatus Status,
        DateTimeOffset Timestamp,
        int RetryCount,
        string? ExtraData
    );

    private static bool _initialized;
    private static readonly object _initLock = new();

    public SyncChangeJournal(string dbPath)
    {
        EnsureInitialized();
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitializeSchema();
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            SQLitePCL.Batteries.Init();
            _initialized = true;
        }
    }

    private void InitializeSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS change_journal (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                peer_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                change_type INTEGER NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                timestamp TEXT NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                extra_data TEXT,
                UNIQUE(peer_id, relative_path, change_type, status)
            );
            CREATE INDEX IF NOT EXISTS idx_journal_peer_status
                ON change_journal(peer_id, status);
            CREATE INDEX IF NOT EXISTS idx_journal_peer_path
                ON change_journal(peer_id, relative_path);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Record a file change. If a pending entry for the same peer+path exists, update it.
    /// </summary>
    public async Task RecordChangeAsync(string peerId, string relativePath, ChangeType type, string? extraData = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Check for existing pending entry
            using var checkCmd = _conn.CreateCommand();
            checkCmd.CommandText = "SELECT id FROM change_journal WHERE peer_id = @peer AND relative_path = @path AND status = 0";
            checkCmd.Parameters.AddWithValue("@peer", peerId);
            checkCmd.Parameters.AddWithValue("@path", relativePath);
            var existingId = checkCmd.ExecuteScalar();

            if (existingId != null)
            {
                // Update existing entry (coalesce: delete trumps modify)
                using var updateCmd = _conn.CreateCommand();
                updateCmd.CommandText = @"UPDATE change_journal
                    SET change_type = @type, timestamp = @ts, extra_data = @extra
                    WHERE id = @id";
                updateCmd.Parameters.AddWithValue("@type", (int)type);
                updateCmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
                updateCmd.Parameters.AddWithValue("@extra", (object?)extraData ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@id", (long)existingId);
                updateCmd.ExecuteNonQuery();
            }
            else
            {
                using var insertCmd = _conn.CreateCommand();
                insertCmd.CommandText = @"INSERT INTO change_journal (peer_id, relative_path, change_type, status, timestamp, extra_data)
                    VALUES (@peer, @path, @type, 0, @ts, @extra)";
                insertCmd.Parameters.AddWithValue("@peer", peerId);
                insertCmd.Parameters.AddWithValue("@path", relativePath);
                insertCmd.Parameters.AddWithValue("@type", (int)type);
                insertCmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
                insertCmd.Parameters.AddWithValue("@extra", (object?)extraData ?? DBNull.Value);
                insertCmd.ExecuteNonQuery();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Get all pending entries for a peer, ordered by timestamp.
    /// </summary>
    public async Task<List<JournalEntry>> GetPendingAsync(string peerId, int maxEntries = 100, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entries = new List<JournalEntry>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT id, peer_id, relative_path, change_type, status, timestamp, retry_count, extra_data
                FROM change_journal
                WHERE peer_id = @peer AND status = 0
                ORDER BY timestamp ASC
                LIMIT @max";
            cmd.Parameters.AddWithValue("@peer", peerId);
            cmd.Parameters.AddWithValue("@max", maxEntries);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                entries.Add(new JournalEntry(
                    Id: reader.GetInt64(0),
                    PeerId: reader.GetString(1),
                    RelativePath: reader.GetString(2),
                    Type: (ChangeType)reader.GetInt32(3),
                    Status: (JournalStatus)reader.GetInt32(4),
                    Timestamp: DateTimeOffset.Parse(reader.GetString(5)),
                    RetryCount: reader.GetInt32(6),
                    ExtraData: reader.IsDBNull(7) ? null : reader.GetString(7)
                ));
            }
            return entries;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Mark an entry as in-flight (being processed).
    /// </summary>
    public async Task MarkInFlightAsync(long entryId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE change_journal SET status = 1 WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", entryId);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Mark an entry as completed and remove it.
    /// </summary>
    public async Task MarkCompletedAsync(long entryId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM change_journal WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", entryId);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Mark an entry as failed, incrementing retry count.
    /// Resets to pending status if under max retries, otherwise deletes.
    /// </summary>
    public async Task MarkFailedAsync(long entryId, int maxRetries = 5, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE change_journal
                SET status = CASE WHEN retry_count >= @max THEN 2 ELSE 0 END,
                    retry_count = retry_count + 1
                WHERE id = @id";
            cmd.Parameters.AddWithValue("@max", maxRetries);
            cmd.Parameters.AddWithValue("@id", entryId);
            cmd.ExecuteNonQuery();

            // Clean up permanently failed entries
            using var cleanCmd = _conn.CreateCommand();
            cleanCmd.CommandText = "DELETE FROM change_journal WHERE status = 2 AND retry_count > @max";
            cleanCmd.Parameters.AddWithValue("@max", maxRetries);
            cleanCmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Count pending entries for a peer.
    /// </summary>
    public async Task<int> CountPendingAsync(string peerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM change_journal WHERE peer_id = @peer AND status = 0";
            cmd.Parameters.AddWithValue("@peer", peerId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Remove all pending entries for a peer (e.g., on sync stop).
    /// </summary>
    public async Task ClearPeerAsync(string peerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM change_journal WHERE peer_id = @peer";
            cmd.Parameters.AddWithValue("@peer", peerId);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _conn.CloseAsync();
        await _conn.DisposeAsync();
    }
}
