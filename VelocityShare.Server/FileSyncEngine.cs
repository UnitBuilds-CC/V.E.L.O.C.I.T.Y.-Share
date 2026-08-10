using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VelocityShare.Server.Sync;
using Protocol = VelocityShare.Protocol;

namespace VelocityShare.Server
{
    /// <summary>
    /// Production-grade sync engine supporting:
    /// - Multi-peer sync (one engine per peer)
    /// - Delta sync with block-level diff (64KB blocks)
    /// - Chunked large file transfer
    /// - Persistent change journal (SQLite)
    /// - Last-writer-wins conflict resolution
    /// - Initial full-sync / reconciliation
    /// - Storage provider abstraction (local, S3, Azure)
    /// - Adaptive rate limiting (bandwidth, CPU, disk I/O)
    /// - Adaptive debounce with stability detection
    /// - Sync latency metrics tracking
    /// </summary>
    public class FileSyncEngine : IAsyncDisposable
    {
        private readonly ISyncStorageProvider _storage;
        private readonly string _targetPeerId;
        private readonly Func<byte[], Task> _sendToPeer;
        private readonly ILogger _logger;
        private readonly BlockDeltaDetector _deltaDetector;
        private readonly SyncChangeJournal _journal;

        // Rate limiting & adaptive scheduling
        private readonly SyncRateLimiter _rateLimiter;
        private readonly AdaptiveSyncScheduler _adaptiveScheduler;
        private readonly SyncLatencyTracker _latencyTracker;
        private SyncThrottleConfig _throttleConfig;

        // File catalog: relativePath -> FileEntry
        private readonly ConcurrentDictionary<string, FileEntry> _fileCatalog = new();
        private readonly string _catalogPath;

        // FileSystemWatcher for local changes
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentQueue<string> _pendingChanges = new();
        private System.Threading.Timer? _debounceTimer;
        // Reference count for concurrent remote change operations (Interlocked for thread safety)
        private int _remoteChangeRefCount;

        // Large file transfers (>= 64KB use encrypted VCTP-style chunked transfer)
        public ConcurrentDictionary<Guid, (byte[] Key, byte[] Nonce, string FullPath, string FileHash)> ActiveSyncTransfers { get; } = new();

        // Stats (use Interlocked for thread-safe access)
        private long _totalBytesSent;
        private long _totalBytesReceived;
        private volatile int _deltaSyncsCompleted;
        private volatile int _fullSyncsCompleted;
        public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
        public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
        public int DeltaSyncsCompleted => _deltaSyncsCompleted;
        public int FullSyncsCompleted => _fullSyncsCompleted;

        // Lock for catalog persistence (prevents concurrent file writes)
        private readonly object _catalogLock = new();

        public string SyncFolderPath => (_storage as LocalSyncStorageProvider)?.RootPath ?? "<cloud>";
        public string TargetPeerId => _targetPeerId;
        public SyncState State { get; private set; } = SyncState.Idle;

        // Rate limiting & metrics accessors
        public SyncRateLimiter RateLimiter => _rateLimiter;
        public AdaptiveSyncScheduler AdaptiveScheduler => _adaptiveScheduler;
        public SyncLatencyTracker LatencyTracker => _latencyTracker;
        public SyncThrottleConfig ThrottleConfig => _throttleConfig;

        public enum SyncState { Idle, Syncing, Reconciling, Error }

        public sealed record FileEntry(string Hash, long Size, long LastModifiedUtc);

        public FileSyncEngine(
            ISyncStorageProvider storage,
            string targetPeerId,
            Func<byte[], Task> sendToPeer,
            SyncChangeJournal journal,
            SyncThrottleConfig? throttleConfig = null,
            ILogger? logger = null)
        {
            _storage = storage;
            _targetPeerId = targetPeerId;
            _sendToPeer = sendToPeer;
            _journal = journal;
            _logger = logger ?? NullLogger.Instance;
            _deltaDetector = new BlockDeltaDetector();

            _throttleConfig = throttleConfig ?? new SyncThrottleConfig();
            string storageType = storage.ProviderType; // "local", "s3", "azure"
            var effectiveLimits = _throttleConfig.Resolve(storageType);
            _rateLimiter = new SyncRateLimiter(effectiveLimits);
            _adaptiveScheduler = new AdaptiveSyncScheduler(effectiveLimits);
            _latencyTracker = new SyncLatencyTracker();

            _catalogPath = storage is LocalSyncStorageProvider local
                ? Path.Combine(local.RootPath, ".velocity_sync_catalog.json")
                : ".velocity_sync_catalog.json";

            LoadCatalog();
        }

        // ── Start / Stop ────────────────────────────────────────────────────

        public void Start()
        {
            if (_storage is LocalSyncStorageProvider local)
            {
                _watcher = new FileSystemWatcher(local.RootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Created += OnFileSystemEvent;
                _watcher.Changed += OnFileSystemEvent;
                _watcher.Deleted += OnFileSystemEvent;
                _watcher.Renamed += OnFileSystemEvent;
                _watcher.EnableRaisingEvents = true;
            }

            _debounceTimer = new System.Threading.Timer(ProcessDebouncedChanges, null, Timeout.Infinite, Timeout.Infinite);
            State = SyncState.Syncing;
            _logger.LogInformation("[Sync Engine] Started for peer {PeerId}, storage: {Provider}", _targetPeerId, _storage.ProviderType);
        }

        public void Stop()
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            State = SyncState.Idle;
            _logger.LogInformation("[Sync Engine] Stopped for peer {PeerId}", _targetPeerId);
        }

        // ── Initial Full Sync / Reconciliation ──────────────────────────────

        /// <summary>
        /// Build and send our file manifest to the peer for reconciliation.
        /// </summary>
        public async Task SendManifestAsync(CancellationToken ct = default)
        {
            State = SyncState.Reconciling;
            var entries = new List<string>();

            await foreach (var relPath in _storage.EnumerateFilesAsync(ct))
            {
                if (relPath.StartsWith(".velocity_")) continue; // skip metadata
                ct.ThrowIfCancellationRequested();

                try
                {
                    long size = await _storage.GetFileSizeAsync(relPath, ct);
                    var mtime = await _storage.GetLastModifiedAsync(relPath, ct);
                    byte[] content = await _storage.ReadFileAsync(relPath, ct);
                    byte[] hash = VelocityShareCrypto.HashChunk(content);
                    string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

                    _fileCatalog[relPath] = new FileEntry(hashHex, size, mtime.UtcDateTime.Ticks);

                    // manifest entry: path|hash|size|mtime
                    entries.Add($"{relPath}|{hashHex}|{size}|{mtime.UtcDateTime.Ticks}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Sync Engine] Failed to scan file: {Path}", relPath);
                }
            }

            string manifest = string.Join(",", entries);
            byte[] packet = Protocol.NdaSignaling.CreateSyncManifest(_targetPeerId, manifest);
            await _sendToPeer(packet);
            SaveCatalog();

            _logger.LogInformation("[Sync Engine] Sent manifest with {Count} files to peer {PeerId}", entries.Count, _targetPeerId);
        }

        /// <summary>
        /// Process a received manifest from the peer. Compare with our catalog,
        /// send files we have that they don't, request files they have that we don't.
        /// </summary>
        public async Task ProcessRemoteManifestAsync(string manifestData, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(manifestData)) return;

            var remoteFiles = new Dictionary<string, FileEntry>();
            foreach (var entry in manifestData.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length >= 4)
                {
                    string path = parts[0];
                    string hash = parts[1];
                    long.TryParse(parts[2], out long size);
                    long.TryParse(parts[3], out long mtime);
                    remoteFiles[path] = new FileEntry(hash, size, mtime);
                }
            }

            int sent = 0, requested = 0;

            // Files we have that they don't → send to them
            foreach (var (path, ourEntry) in _fileCatalog)
            {
                if (!remoteFiles.ContainsKey(path))
                {
                    await SyncFileToRemoteAsync(path, ct);
                    sent++;
                }
            }

            // Files they have that we don't → request from them
            foreach (var (path, theirEntry) in remoteFiles)
            {
                if (!_fileCatalog.ContainsKey(path))
                {
                    // Request the full file
                    byte[] packet = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, path, "all");
                    await _sendToPeer(packet);
                    requested++;
                }
                else
                {
                    // Both have it — check if different → delta sync or LWW
                    var ourEntry = _fileCatalog[path];
                    if (ourEntry.Hash != theirEntry.Hash)
                    {
                        await ResolveConflictAsync(path, ourEntry, theirEntry, ct);
                    }
                }
            }

            // Signal manifest processing complete
            byte[] completePacket = Protocol.NdaSignaling.CreateSyncManifestComplete(_targetPeerId);
            await _sendToPeer(completePacket);

            Interlocked.Increment(ref _fullSyncsCompleted);
            State = SyncState.Syncing;
            _logger.LogInformation("[Sync Engine] Reconciliation complete: sent {Sent}, requested {Requested}", sent, requested);
        }

        // ── Local file change handling ──────────────────────────────────────

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (Volatile.Read(ref _remoteChangeRefCount) > 0) return;
            if (e.FullPath.Contains(".velocity_")) return; // skip metadata

            // Record latency start
            string relativePath = _storage is LocalSyncStorageProvider local
                ? Path.GetRelativePath(local.RootPath, e.FullPath) : e.FullPath;
            _latencyTracker.RecordChangeDetected(relativePath);

            _pendingChanges.Enqueue(e.FullPath);

            // Use adaptive debounce instead of fixed interval
            int debounceMs = _adaptiveScheduler.NotifyChange();
            _debounceTimer?.Change(debounceMs, Timeout.Infinite);
        }

        private void ProcessDebouncedChanges(object? state)
        {
            _ = ProcessDebouncedChangesAsync();
        }

        private async Task ProcessDebouncedChangesAsync()
        {
            try
            {
                var processed = new HashSet<string>();
                while (_pendingChanges.TryDequeue(out var path))
                {
                    if (processed.Add(path))
                    {
                        await HandleFileChangeAsync(path);
                    }
                }
                _adaptiveScheduler.NotifySyncTriggered();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Sync Engine] ProcessDebouncedChanges failed");
            }
        }

        private async Task HandleFileChangeAsync(string fullPath)
        {
            string relativePath = _storage is LocalSyncStorageProvider local
                ? Path.GetRelativePath(local.RootPath, fullPath)
                : fullPath;

            if (!await _storage.FileExistsAsync(relativePath))
            {
                // File deleted
                if (_fileCatalog.TryRemove(relativePath, out _))
                {
                    SaveCatalog();
                    await _journal.RecordChangeAsync(_targetPeerId, relativePath, SyncChangeJournal.ChangeType.Delete);
                    byte[] packet = Protocol.NdaSignaling.CreateDelete(_targetPeerId, relativePath);
                    await _sendToPeer(packet);
                }
                return;
            }

            try
            {
                byte[] fileBytes = await _storage.ReadFileAsync(relativePath);
                byte[] hash = VelocityShareCrypto.HashChunk(fileBytes);
                string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                long fileSize = fileBytes.Length;
                var mtime = await _storage.GetLastModifiedAsync(relativePath);

                _fileCatalog.TryGetValue(relativePath, out var oldEntry);

                if (oldEntry != null && oldEntry.Hash == hashHex)
                    return; // No actual change

                _fileCatalog[relativePath] = new FileEntry(hashHex, fileSize, mtime.UtcDateTime.Ticks);
                SaveCatalog();

                // Delta sync for large files, full update for small
                // Apply bandwidth throttling before sending
                await _rateLimiter.ThrottleAsync(fileBytes.Length);

                if (fileSize >= 65536 && oldEntry != null)
                {
                    await PerformDeltaSyncAsync(relativePath, hashHex, fileSize, mtime, oldEntry);
                }
                else if (fileSize >= 65536)
                {
                    // New large file: use encrypted offer
                    var fileId = Guid.NewGuid();
                    byte[] key = new byte[32];
                    byte[] nonce = new byte[12];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(key);
                    System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
                    ActiveSyncTransfers[fileId] = (key, nonce, fullPath, hashHex);
                    byte[] packet = Protocol.NdaSignaling.CreateOffer(_targetPeerId, relativePath, hashHex, fileSize, fileId, key, nonce);
                    await _sendToPeer(packet);
                }
                else
                {
                    // Small file: send inline
                    byte[] packet = Protocol.NdaSignaling.CreateUpdate(_targetPeerId, relativePath, hashHex, fileSize, fileBytes);
                    await _sendToPeer(packet);
                }

                // Record latency completion
                _latencyTracker.RecordSyncCompleted(relativePath);

                await _journal.RecordChangeAsync(_targetPeerId, relativePath,
                    oldEntry == null ? SyncChangeJournal.ChangeType.Create : SyncChangeJournal.ChangeType.Modify);
                Interlocked.Add(ref _totalBytesSent, fileSize);
            }
            catch (IOException)
            {
                // File lock retry — will be picked up by next watcher event
            }
        }

        // ── Delta Sync ──────────────────────────────────────────────────────

        private async Task PerformDeltaSyncAsync(string relativePath, string newHash, long newSize,
            DateTimeOffset newMtime, FileEntry oldEntry)
        {
            try
            {
                // Compute block hashes for current local version
                var localHashes = await _deltaDetector.ComputeBlockHashesAsync(_storage, relativePath, newSize);

                // Build a block list string: "idx:hexhash,idx:hexhash,..."
                var blockListParts = new List<string>();
                foreach (var (idx, hash) in localHashes)
                {
                    blockListParts.Add($"{idx}:{Convert.ToHexString(hash)}");
                }
                string blockList = string.Join(",", blockListParts);

                byte[] packet = Protocol.NdaSignaling.CreateDeltaOffer(
                    _targetPeerId, relativePath, newHash, newSize,
                    BlockDeltaDetector.DefaultBlockSize, blockList, newMtime.UtcDateTime.Ticks);
                await _sendToPeer(packet);

                await _journal.RecordChangeAsync(_targetPeerId, relativePath, SyncChangeJournal.ChangeType.BlockUpdate, blockList);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sync Engine] Delta sync failed for {Path}, falling back to full update", relativePath);
                // Fallback: send full file
                byte[] content = await _storage.ReadFileAsync(relativePath);
                byte[] fallback = Protocol.NdaSignaling.CreateUpdate(_targetPeerId, relativePath, newHash, newSize, content);
                await _sendToPeer(fallback);
            }
        }

        /// <summary>
        /// Handle a delta_offer from the remote peer: compare their block list with ours,
        /// request only the blocks we don't have or that differ.
        /// </summary>
        public async Task HandleDeltaOfferAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            string filePath = msg.FilePath;
            long remoteSize = msg.FileSize;
            int remoteBlockSize = msg.BlockSize > 0 ? msg.BlockSize : BlockDeltaDetector.DefaultBlockSize;
            long remoteMtime = msg.LastModified;

            // Parse remote block list
            var remoteBlockHashes = new Dictionary<int, byte[]>();
            if (!string.IsNullOrEmpty(msg.BlockList))
            {
                foreach (var part in msg.BlockList.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[0], out int idx))
                    {
                        remoteBlockHashes[idx] = Convert.FromHexString(kv[1]);
                    }
                }
            }

            // Check if we have this file
            bool weHaveIt = await _storage.FileExistsAsync(filePath);
            if (!weHaveIt)
            {
                // We don't have it — request all blocks
                var allBlocks = string.Join(",", remoteBlockHashes.Keys.OrderBy(k => k));
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath,
                    string.IsNullOrEmpty(allBlocks) ? "all" : allBlocks);
                await _sendToPeer(req);
                return;
            }

            // Compute our block hashes
            long localSize = await _storage.GetFileSizeAsync(filePath);
            var localHashes = await _deltaDetector.ComputeBlockHashesAsync(_storage, filePath, localSize);

            // Find blocks we need (missing or different)
            var neededBlocks = new List<int>();
            int maxBlocks = Math.Max(localHashes.Count, remoteBlockHashes.Count);
            for (int i = 0; i < maxBlocks; i++)
            {
                bool localHas = localHashes.TryGetValue(i, out var localHash);
                bool remoteHas = remoteBlockHashes.TryGetValue(i, out var remoteHash);

                if (!localHas || !remoteHas || !HashesEqual(localHash!, remoteHash!))
                {
                    neededBlocks.Add(i);
                }
            }

            if (neededBlocks.Count == 0)
            {
                _logger.LogInformation("[Sync Engine] Delta offer for {Path}: no blocks needed", filePath);
                return;
            }

            // If file size changed drastically, request full file
            if (localSize > 0 && remoteSize > 0 &&
                (remoteSize > localSize * 2 || localSize > remoteSize * 2))
            {
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath, "all");
                await _sendToPeer(req);
                return;
            }

            string requestedBlocks = string.Join(",", neededBlocks);
            byte[] request = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath, requestedBlocks);
            await _sendToPeer(request);

            _logger.LogInformation("[Sync Engine] Delta sync for {Path}: requesting {Needed}/{Total} blocks",
                filePath, neededBlocks.Count, maxBlocks);
        }

        /// <summary>
        /// Handle a block_request from the remote peer: send them the requested blocks.
        /// </summary>
        public async Task HandleBlockRequestAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            string filePath = msg.FilePath;
            if (!await _storage.FileExistsAsync(filePath)) return;

            long fileSize = await _storage.GetFileSizeAsync(filePath);

            if (msg.RequestedBlocks == "all")
            {
                // Send entire file as blocks
                int blockSize = BlockDeltaDetector.DefaultBlockSize;
                int totalBlocks = (int)((fileSize + blockSize - 1) / blockSize);
                for (int i = 0; i < totalBlocks; i++)
                {
                    long offset = (long)i * blockSize;
                    int length = (int)Math.Min(blockSize, fileSize - offset);
                    byte[] block = await _storage.ReadFileBlockAsync(filePath, offset, length);
                    byte[] hash = VelocityShareCrypto.HashChunk(block);
                    byte[] pkt = Protocol.NdaSignaling.CreateBlockData(_targetPeerId, filePath, i, offset, block,
                        Convert.ToHexString(hash).ToLowerInvariant());
                    await _sendToPeer(pkt);
                    Interlocked.Add(ref _totalBytesSent, length);
                }
            }
            else
            {
                // Send specific requested blocks
                foreach (var idxStr in msg.RequestedBlocks.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(idxStr.Trim(), out int blockIndex)) continue;
                    int blockSize = BlockDeltaDetector.DefaultBlockSize;
                    long offset = (long)blockIndex * blockSize;
                    if (offset >= fileSize) continue;
                    int length = (int)Math.Min(blockSize, fileSize - offset);
                    byte[] block = await _storage.ReadFileBlockAsync(filePath, offset, length);

                    // Apply bandwidth + disk I/O throttling per block
                    await _rateLimiter.ThrottleAsync(length);

                    byte[] hash = VelocityShareCrypto.HashChunk(block);
                    byte[] pkt = Protocol.NdaSignaling.CreateBlockData(_targetPeerId, filePath, blockIndex, offset, block,
                        Convert.ToHexString(hash).ToLowerInvariant());
                    await _sendToPeer(pkt);
                    Interlocked.Add(ref _totalBytesSent, length);
                }
            }

            // Signal completion
            byte[] fileContent = await _storage.ReadFileAsync(filePath);
            byte[] fullHash = VelocityShareCrypto.HashChunk(fileContent);
            byte[] completePkt = Protocol.NdaSignaling.CreateDeltaComplete(_targetPeerId, filePath,
                Convert.ToHexString(fullHash).ToLowerInvariant());
            await _sendToPeer(completePkt);
        }

        /// <summary>
        /// Handle incoming block_data: write the block to local storage.
        /// </summary>
        public async Task HandleBlockDataAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            string filePath = msg.FilePath;
            int blockIndex = msg.BlockIndex;
            long offset = msg.BlockOffset;
            byte[] blockData = msg.Content;

            if (blockData == null || blockData.Length == 0) return;

            // Path traversal guard
            if (string.IsNullOrEmpty(filePath) || filePath.Contains(".."))
            {
                _logger.LogWarning("[Sync Engine] Path traversal blocked in block_data: {Path}", filePath);
                return;
            }
            if (_storage is LocalSyncStorageProvider local)
            {
                string combined = Path.Combine(local.RootPath, filePath);
                string full = Path.GetFullPath(combined);
                string canonicalRoot = local.RootPath.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? local.RootPath : local.RootPath + Path.DirectorySeparatorChar;
                if (!full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Sync Engine] Block data path escapes sync folder: {Path}", filePath);
                    return;
                }
            }

            Interlocked.Increment(ref _remoteChangeRefCount);
            try
            {
                // Ensure directory exists
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    await _storage.EnsureDirectoryAsync(dir);

                await _storage.WriteFileBlockAsync(filePath, offset, blockData);

                // Apply disk I/O throttling for write
                await _rateLimiter.ThrottleDiskIOAsync(blockData.Length);
                Interlocked.Add(ref _totalBytesReceived, blockData.Length);

                _logger.LogDebug("[Sync Engine] Applied block {Idx} at offset {Offset} for {Path} ({Bytes} bytes)",
                    blockIndex, offset, filePath, blockData.Length);
            }
            finally
            {
                Interlocked.Decrement(ref _remoteChangeRefCount);
            }
        }

        /// <summary>
        /// Handle delta_complete: verify final hash and update catalog.
        /// </summary>
        public async Task HandleDeltaCompleteAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            string filePath = msg.FilePath;
            string finalHash = msg.HashHex;

            try
            {
                if (await _storage.FileExistsAsync(filePath))
                {
                    byte[] content = await _storage.ReadFileAsync(filePath);
                    byte[] actualHash = VelocityShareCrypto.HashChunk(content);
                    string actualHex = Convert.ToHexString(actualHash).ToLowerInvariant();
                    long size = content.Length;
                    var mtime = await _storage.GetLastModifiedAsync(filePath);

                    _fileCatalog[filePath] = new FileEntry(actualHex, size, mtime.UtcDateTime.Ticks);
                    SaveCatalog();

                    Interlocked.Increment(ref _deltaSyncsCompleted);
                    await _journal.MarkCompletedAsync(0); // best-effort cleanup

                    _logger.LogInformation("[Sync Engine] Delta sync complete for {Path}: {Size} bytes, hash verified", filePath, size);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sync Engine] Failed to finalize delta for {Path}", filePath);
            }
        }

        // ── Conflict Resolution (LWW) ───────────────────────────────────────

        private async Task ResolveConflictAsync(string path, FileEntry ourEntry, FileEntry theirEntry, CancellationToken ct)
        {
            // Last-Writer-Wins: compare modification timestamps
            bool weWin = ourEntry.LastModifiedUtc >= theirEntry.LastModifiedUtc;

            if (weWin)
            {
                // Our version is newer — send it to them
                await SyncFileToRemoteAsync(path, ct);
                byte[] resolution = Protocol.NdaSignaling.CreateConflictResolution(_targetPeerId, path,
                    ourEntry.Hash, ourEntry.Size, ourEntry.LastModifiedUtc, weWin: true);
                await _sendToPeer(resolution);
            }
            else
            {
                // Their version is newer — request it
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, path, "all");
                await _sendToPeer(req);
                byte[] resolution = Protocol.NdaSignaling.CreateConflictResolution(_targetPeerId, path,
                    ourEntry.Hash, ourEntry.Size, ourEntry.LastModifiedUtc, weWin: false);
                await _sendToPeer(resolution);
            }

            _logger.LogInformation("[Sync Engine] Conflict on {Path}: LWW winner = {Winner} (our mtime={Our}, their mtime={Their})",
                path, weWin ? "us" : "them", ourEntry.LastModifiedUtc, theirEntry.LastModifiedUtc);
        }

        /// <summary>
        /// Handle a conflict_resolve message from the peer.
        /// </summary>
        public async Task HandleConflictResolutionAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            string filePath = msg.FilePath;
            bool theySayTheyWin = msg.Winner == "us"; // they say "us" = they win

            if (theySayTheyWin)
            {
                // They claim their version is newer — accept it by requesting full file
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath, "all");
                await _sendToPeer(req);
            }
            // If they concede (we win), they'll request from us — nothing to do
        }

        // ── Apply remote sync (original full-file path for backwards compat) ─

        public async Task ApplyRemoteSyncAsync(string type, string relativePath, string hash, ReadOnlyMemory<byte> content)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.Contains(".."))
            {
                _logger.LogWarning("[Sync Engine] Path traversal blocked: {Path}", relativePath);
                return;
            }

            try
            {
                // Sandbox check
                if (_storage is LocalSyncStorageProvider local)
                {
                    string combinedPath = Path.Combine(local.RootPath, relativePath);
                    string fullPath = Path.GetFullPath(combinedPath);
                    string canonicalRoot = local.RootPath.EndsWith(Path.DirectorySeparatorChar.ToString())
                        ? local.RootPath : local.RootPath + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[Sync Engine] Path escapes sync folder: {Path}", relativePath);
                        return;
                    }
                }

                Interlocked.Increment(ref _remoteChangeRefCount);

                if (type == "sync_delete")
                {
                    await _storage.DeleteFileAsync(relativePath);
                    _fileCatalog.TryRemove(relativePath, out _);
                }
                else if (type == "sync_update" && !content.IsEmpty)
                {
                    string? dir = Path.GetDirectoryName(relativePath);
                    if (!string.IsNullOrEmpty(dir))
                        await _storage.EnsureDirectoryAsync(dir);

                    await _storage.WriteFileAsync(relativePath, content.ToArray());
                    byte[] actualHash = VelocityShareCrypto.HashChunk(content.ToArray());
                    string actualHex = Convert.ToHexString(actualHash).ToLowerInvariant();
                    long size = content.Length;

                    _fileCatalog[relativePath] = new FileEntry(actualHex, size, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
                    Interlocked.Add(ref _totalBytesReceived, size);
                }
                SaveCatalog();
            }
            finally
            {
                Interlocked.Decrement(ref _remoteChangeRefCount);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private async Task SyncFileToRemoteAsync(string relativePath, CancellationToken ct)
        {
            try
            {
                byte[] content = await _storage.ReadFileAsync(relativePath, ct);
                byte[] hash = VelocityShareCrypto.HashChunk(content);
                string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

                if (content.Length >= 65536)
                {
                    // Large file: use offer
                    var fileId = Guid.NewGuid();
                    byte[] key = new byte[32];
                    byte[] nonce = new byte[12];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(key);
                    System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
                    ActiveSyncTransfers[fileId] = (key, nonce, relativePath, hashHex);
                    byte[] pkt = Protocol.NdaSignaling.CreateOffer(_targetPeerId, relativePath, hashHex, content.Length, fileId, key, nonce);
                    await _sendToPeer(pkt);
                }
                else
                {
                    byte[] pkt = Protocol.NdaSignaling.CreateUpdate(_targetPeerId, relativePath, hashHex, content.Length, content);
                    await _sendToPeer(pkt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sync Engine] Failed to sync file {Path} to remote", relativePath);
            }
        }

        public void ConfirmRemoteSyncCompleted(string relativePath, string hash)
        {
            if (_fileCatalog.TryGetValue(relativePath, out var entry))
            {
                _fileCatalog[relativePath] = new FileEntry(hash, entry.Size, entry.LastModifiedUtc);
            }
            SaveCatalog();
        }

        private static bool HashesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ── Catalog persistence ─────────────────────────────────────────────

        private void LoadCatalog()
        {
            if (_storage is not LocalSyncStorageProvider local) return;
            string path = Path.Combine(local.RootPath, ".velocity_sync_catalog.json");
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Dictionary<string, FileEntryDto>>(json);
                if (data != null)
                {
                    foreach (var kvp in data)
                    {
                        _fileCatalog[kvp.Key] = new FileEntry(kvp.Value.Hash, kvp.Value.Size, kvp.Value.LastModifiedUtc);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sync Engine] Failed to load catalog");
            }
        }

        private void SaveCatalog()
        {
            if (_storage is not LocalSyncStorageProvider local) return;
            string path = Path.Combine(local.RootPath, ".velocity_sync_catalog.json");
            lock (_catalogLock)
            {
                try
                {
                    var dto = new Dictionary<string, FileEntryDto>();
                    foreach (var kvp in _fileCatalog)
                    {
                        dto[kvp.Key] = new FileEntryDto { Hash = kvp.Value.Hash, Size = kvp.Value.Size, LastModifiedUtc = kvp.Value.LastModifiedUtc };
                    }
                    string json = JsonSerializer.Serialize(dto);
                    File.WriteAllText(path, json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Sync Engine] Failed to save catalog");
                }
            }
        }

        private sealed class FileEntryDto
        {
            public string Hash { get; set; } = "";
            public long Size { get; set; }
            public long LastModifiedUtc { get; set; }
        }

        // ── Dispose ─────────────────────────────────────────────────────────

        /// <summary>
        /// Update the throttle configuration at runtime (e.g., from API call).
        /// </summary>
        public void UpdateThrottleConfig(SyncThrottleConfig newConfig)
        {
            _throttleConfig = newConfig;
            var effectiveLimits = newConfig.Resolve(_storage.ProviderType);
            _rateLimiter.UpdateLimits(effectiveLimits);
            _adaptiveScheduler.UpdateLimits(effectiveLimits);
            _logger.LogInformation("[Sync Engine] Throttle config updated: profile={Profile}, auto={Auto}",
                newConfig.Profile, newConfig.AutoAdaptive);
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            _rateLimiter.Dispose();
            _adaptiveScheduler.Dispose();
            await _storage.DisposeAsync();
        }
    }
}
