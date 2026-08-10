using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Protocol = VelocityShare.Protocol;

namespace VelocityShare.Mobile
{
    /// <summary>
    /// Production-grade mobile sync client with:
    /// - Auto-reconnect with exponential backoff
    /// - Initial full-sync / manifest exchange
    /// - Delta sync (block-level diff)
    /// - LWW conflict resolution
    /// - Persistent catalog with file hashes
    /// - Adaptive rate limiting (bandwidth, debounce, latency)
    /// </summary>
    public class FileSyncClient : IDisposable
    {
        private string? _localPath;
        private string? _serverUrl;
        private string? _myPeerId;
        private string? _targetPeerId;
        private ClientWebSocket? _webSocket;
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, FileEntry> _catalog = new();
        private readonly ConcurrentQueue<string> _pendingChanges = new();
        private System.Threading.Timer? _debounceTimer;
        private CancellationTokenSource? _cts;
        private string? _metadataPath;
        private string? _catalogPath;

        // Reconnection state
        private int _reconnectAttempts;
        private const int MaxReconnectDelayMs = 30_000;
        private const int BaseReconnectDelayMs = 1_000;
        private bool _intentionalStop;

        // Rate limiting & adaptive scheduling
        private MobileSyncThrottle? _throttle;

        // Block delta detector
        private const int BlockSize = 64 * 1024; // 64KB

        // Stats
        public long TotalBytesSent { get; private set; }
        public long TotalBytesReceived { get; private set; }
        public int ReconnectCount { get; private set; }

        // Throttle metrics
        public MobileSyncThrottle? Throttle => _throttle;
        public double AverageSyncLatencyMs => _throttle?.AverageLatencyMs ?? 0;
        public long MaxSyncLatencyMs => _throttle?.MaxLatencyMs ?? 0;
        public int PendingSyncChanges => _throttle?.PendingChanges ?? 0;

        public event Action<string>? OnLog;
        public event Action<string>? OnStatusChanged;
        public event Action<string, long>? OnFileSynced;

        public sealed record FileEntry(string Hash, long Size, long LastModifiedUtc);

        public async Task StartAsync(string localPath, string serverUrl, string myPeerId, string targetPeerId)
        {
            _localPath = localPath;
            _serverUrl = serverUrl;
            _myPeerId = myPeerId;
            _targetPeerId = targetPeerId;
            _intentionalStop = false;
            _cts = new CancellationTokenSource();
            _throttle = new MobileSyncThrottle();

            if (!Directory.Exists(_localPath))
                Directory.CreateDirectory(_localPath);

            _metadataPath = Path.Combine(_localPath, ".velocity_sync_metadata.json");
            _catalogPath = Path.Combine(_localPath, ".velocity_sync_catalog.json");

            LoadCatalog();
            await ConnectAndRunAsync();
        }

        public async Task StopAsync()
        {
            _intentionalStop = true;
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Sync stopped", CancellationToken.None); }
                    catch { }
                }
                _webSocket.Dispose();
                _webSocket = null;
            }

            OnStatusChanged?.Invoke("INACTIVE");
            OnLog?.Invoke("[Sync Client] Sync stopped.");
        }

        // ── Connection + Auto-Reconnect ─────────────────────────────────────

        private async Task ConnectAndRunAsync()
        {
            while (!_intentionalStop && _cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    await ConnectAsync();
                    _reconnectAttempts = 0; // Reset on successful connect
                    OnStatusChanged?.Invoke("ACTIVE");
                    OnLog?.Invoke("[Sync Client] Connected. Starting sync...");

                    // Send initial manifest for reconciliation
                    await SendManifestAsync();

                    // Start file watcher
                    StartWatcher();

                    // Run receive loop
                    await ReceiveLoopAsync(_cts);

                    // If we get here, connection was lost
                    OnLog?.Invoke("[Sync Client] Connection lost.");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Sync Client] Connection error: {ex.Message}");
                }
                finally
                {
                    _watcher?.Dispose();
                    _watcher = null;
                }

                if (_intentionalStop || _cts == null || _cts.IsCancellationRequested)
                    break;

                // Exponential backoff reconnect
                _reconnectAttempts++;
                ReconnectCount++;
                int delay = Math.Min(BaseReconnectDelayMs * (int)Math.Pow(2, _reconnectAttempts - 1), MaxReconnectDelayMs);
                OnStatusChanged?.Invoke("RECONNECTING");
                OnLog?.Invoke($"[Sync Client] Reconnecting in {delay}ms (attempt {_reconnectAttempts})...");

                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ConnectAsync()
        {
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            _webSocket.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                return VelocityShare.Server.CertificateValidator.Validate(_serverUrl, certificate, chain, sslPolicyErrors);
            };

            Uri serverUri = new Uri($"{_serverUrl!.Replace("http", "ws")}/ws/share?peerId={_myPeerId}");
            OnLog?.Invoke($"[Sync Client] Connecting to: {serverUri}");
            OnStatusChanged?.Invoke("CONNECTING");

            await _webSocket.ConnectAsync(serverUri, _cts!.Token);
            OnLog?.Invoke("[Sync Client] WebSocket connected.");
        }

        // ── Initial Full Sync / Manifest ────────────────────────────────────

        private async Task SendManifestAsync()
        {
            if (_localPath == null || _targetPeerId == null) return;

            var entries = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(_localPath, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }))
                {
                    string relPath = Path.GetRelativePath(_localPath, file);
                    if (relPath.StartsWith(".velocity_")) continue;

                    try
                    {
                        byte[] content = await File.ReadAllBytesAsync(file);
                        byte[] hashBytes = VelocityShareCrypto.HashChunk(content);
                        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
                        var fi = new FileInfo(file);

                        _catalog[relPath] = new FileEntry(hashHex, fi.Length, fi.LastWriteTimeUtc.Ticks);

                        entries.Add($"{relPath}|{hashHex}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                    }
                    catch (IOException) { } // file locked
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Sync Client] Manifest scan error: {ex.Message}");
            }

            string manifest = string.Join(",", entries);
            byte[] packet = Protocol.NdaSignaling.CreateSyncManifest(_targetPeerId, manifest);
            await SendBinaryAsync(packet);
            SaveCatalog();

            OnLog?.Invoke($"[Sync Client] Sent manifest with {entries.Count} files");
        }

        // ── File Watcher ────────────────────────────────────────────────────

        private void StartWatcher()
        {
            if (_localPath == null) return;

            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(_localPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Created += OnFileSystemEvent;
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(ProcessDebouncedChanges, null, Timeout.Infinite, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;

            OnLog?.Invoke($"[Sync Client] Watching: {_localPath}");
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.Contains(".velocity_")) return;

            string relativePath = _localPath != null ? Path.GetRelativePath(_localPath, e.FullPath) : e.FullPath;

            _pendingChanges.Enqueue(e.FullPath);

            // Use adaptive debounce
            int debounceMs = _throttle?.NotifyChange(relativePath) ?? 500;
            _debounceTimer?.Change(debounceMs, Timeout.Infinite);
        }

        private async void ProcessDebouncedChanges(object? state)
        {
            var processed = new HashSet<string>();
            while (_pendingChanges.TryDequeue(out var path))
            {
                if (processed.Add(path))
                    await HandleFileChangeAsync(path);
            }
            _throttle?.NotifySyncTriggered();
        }

        private async Task HandleFileChangeAsync(string fullPath)
        {
            if (_localPath == null || _targetPeerId == null) return;

            string relativePath = Path.GetRelativePath(_localPath, fullPath);

            if (!File.Exists(fullPath))
            {
                if (_catalog.TryRemove(relativePath, out _))
                {
                    SaveCatalog();
                    byte[] packet = Protocol.NdaSignaling.CreateDelete(_targetPeerId, relativePath);
                    await SendBinaryAsync(packet);
                }
                return;
            }

            try
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
                byte[] hash = VelocityShareCrypto.HashChunk(fileBytes);
                string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                var fi = new FileInfo(fullPath);

                _catalog.TryGetValue(relativePath, out var oldEntry);
                if (oldEntry?.Hash == hashHex) return; // No change

                _catalog[relativePath] = new FileEntry(hashHex, fi.Length, fi.LastWriteTimeUtc.Ticks);
                SaveCatalog();

                // Apply bandwidth throttling before sending
                if (_throttle != null)
                    await _throttle.ThrottleBandwidthAsync(fileBytes.Length);

                if (fileBytes.Length >= 65536 && oldEntry != null)
                {
                    // Delta sync for large files
                    await PerformDeltaSyncAsync(relativePath, hashHex, fileBytes, fi);
                }
                else if (fileBytes.Length >= 65536)
                {
                    // New large file: use VCTP offer
                    var fileId = Guid.NewGuid();
                    byte[] key = new byte[32];
                    byte[] nonce = new byte[12];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(key);
                    System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
                    byte[] packet = Protocol.NdaSignaling.CreateOffer(_targetPeerId, relativePath, hashHex, fileBytes.Length, fileId, key, nonce);
                    await SendBinaryAsync(packet);
                }
                else
                {
                    byte[] packet = Protocol.NdaSignaling.CreateUpdate(_targetPeerId, relativePath, hashHex, fileBytes.Length, fileBytes);
                    await SendBinaryAsync(packet);
                }

                TotalBytesSent += fileBytes.Length;
                _throttle?.RecordSyncCompleted(relativePath);
                OnFileSynced?.Invoke(relativePath, fi.Length);
            }
            catch (IOException) { } // File lock
        }

        // ── Delta Sync ──────────────────────────────────────────────────────

        private async Task PerformDeltaSyncAsync(string relativePath, string newHash, byte[] fileContent, FileInfo fi)
        {
            if (_targetPeerId == null) return;

            // Compute block hashes for current file
            var blockHashes = new Dictionary<int, byte[]>();
            int totalBlocks = (fileContent.Length + BlockSize - 1) / BlockSize;
            for (int i = 0; i < totalBlocks; i++)
            {
                int offset = i * BlockSize;
                int length = Math.Min(BlockSize, fileContent.Length - offset);
                byte[] block = new byte[length];
                Buffer.BlockCopy(fileContent, offset, block, 0, length);
                blockHashes[i] = VelocityShareCrypto.HashChunk(block);
            }

            var blockListParts = new List<string>();
            foreach (var (idx, hash) in blockHashes)
                blockListParts.Add($"{idx}:{Convert.ToHexString(hash)}");
            string blockList = string.Join(",", blockListParts);

            byte[] packet = Protocol.NdaSignaling.CreateDeltaOffer(
                _targetPeerId, relativePath, newHash, fileContent.Length,
                BlockSize, blockList, fi.LastWriteTimeUtc.Ticks);
            await SendBinaryAsync(packet);
        }

        // ── Receive Loop ────────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(CancellationTokenSource cts)
        {
            var buffer = new byte[1024 * 64];
            while (_webSocket?.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string rawMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleTextMessageAsync(rawMsg);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await HandleBinaryMessageAsync(buffer, result.Count);
                }
            }
        }

        // ── Text Message Handler ────────────────────────────────────────────

        private async Task HandleTextMessageAsync(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                string type = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

                if (type == "folder_sync_payload" && _localPath != null)
                {
                    string innerData = doc.RootElement.GetProperty("data").GetString() ?? "";
                    var innerDoc = JsonDocument.Parse(innerData);
                    string syncType = innerDoc.RootElement.GetProperty("type").GetString() ?? "";
                    string file = innerDoc.RootElement.GetProperty("file").GetString() ?? "";
                    string hash = innerDoc.RootElement.TryGetProperty("hash", out var hp) ? hp.GetString() ?? "" : "";

                    byte[]? contentBytes = null;
                    if (innerDoc.RootElement.TryGetProperty("content", out var cp))
                    {
                        string b64 = cp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(b64)) contentBytes = Convert.FromBase64String(b64);
                    }

                    await ApplyFileSyncAsync(file, syncType, hash, contentBytes);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Sync Client Text Error] {ex.Message}");
            }
        }

        // ── Binary (NDA) Message Handler ────────────────────────────────────

        private async Task HandleBinaryMessageAsync(byte[] buffer, int count)
        {
            try
            {
                var message = new Protocol.NdaSignaling.ParsedMessage(buffer.AsSpan(0, count));

                switch (message.Action)
                {
                    case "delete":
                        await ApplyFileSyncAsync(message.FilePath, "sync_delete", "", null);
                        break;

                    case "update":
                        await ApplyFileSyncAsync(message.FilePath, "sync_update", message.HashHex, message.Content);
                        break;

                    case "delta_offer":
                        await HandleRemoteDeltaOfferAsync(message);
                        break;

                    case "block_request":
                        await HandleRemoteBlockRequestAsync(message);
                        break;

                    case "block_data":
                        await HandleRemoteBlockDataAsync(message);
                        break;

                    case "delta_complete":
                        OnLog?.Invoke($"[Sync Client] Delta sync complete for: {message.FilePath}");
                        break;

                    case "sync_manifest":
                        await ProcessRemoteManifest(message.Manifest);
                        break;

                    case "sync_manifest_complete":
                        OnLog?.Invoke("[Sync Client] Remote peer completed manifest processing");
                        break;

                    case "conflict_resolve":
                        await HandleConflictResolutionAsync(message);
                        break;

                    case "offer":
                    case "accept":
                        // VCTP handling would go here (same as before)
                        OnLog?.Invoke($"[Sync Client] Received VCTP {message.Action} for file: {message.FilePath}");
                        break;
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Sync Client Binary Error] {ex.Message}");
            }
        }

        // ── Apply File Sync ─────────────────────────────────────────────────

        private async Task ApplyFileSyncAsync(string relativePath, string syncType, string hash, byte[]? content)
        {
            if (_localPath == null || string.IsNullOrEmpty(relativePath)) return;

            string fullPath = Path.Combine(_localPath, relativePath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _watcher?.Dispose(); // Pause watcher during remote apply

            try
            {
                if (syncType == "sync_delete")
                {
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    _catalog.TryRemove(relativePath, out _);
                }
                else if (syncType == "sync_update" && content != null)
                {
                    await File.WriteAllBytesAsync(fullPath, content);
                    byte[] actualHash = VelocityShareCrypto.HashChunk(content);
                    string actualHex = Convert.ToHexString(actualHash).ToLowerInvariant();
                    var fi = new FileInfo(fullPath);
                    _catalog[relativePath] = new FileEntry(actualHex, fi.Length, fi.LastWriteTimeUtc.Ticks);
                    TotalBytesReceived += content.Length;
                }
                SaveCatalog();
                OnLog?.Invoke($"[Sync Client] Applied {syncType}: {relativePath}");
            }
            finally
            {
                StartWatcher(); // Resume watcher
            }
        }

        // ── Delta Sync Handlers ─────────────────────────────────────────────

        private async Task HandleRemoteDeltaOfferAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            if (_localPath == null || _targetPeerId == null) return;

            string filePath = msg.FilePath;
            long remoteSize = msg.FileSize;

            // Parse remote block hashes
            var remoteBlockHashes = new Dictionary<int, byte[]>();
            if (!string.IsNullOrEmpty(msg.BlockList))
            {
                foreach (var part in msg.BlockList.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[0], out int idx))
                        remoteBlockHashes[idx] = Convert.FromHexString(kv[1]);
                }
            }

            string fullPath = Path.Combine(_localPath, filePath);
            if (!File.Exists(fullPath))
            {
                // We don't have it — request all
                string allBlocks = string.Join(",", remoteBlockHashes.Keys.OrderBy(k => k));
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath,
                    string.IsNullOrEmpty(allBlocks) ? "all" : allBlocks);
                await SendBinaryAsync(req);
                return;
            }

            // Compute our block hashes
            byte[] localContent = await File.ReadAllBytesAsync(fullPath);
            var localHashes = new Dictionary<int, byte[]>();
            int localBlocks = (localContent.Length + BlockSize - 1) / BlockSize;
            for (int i = 0; i < localBlocks; i++)
            {
                int offset = i * BlockSize;
                int length = Math.Min(BlockSize, localContent.Length - offset);
                byte[] block = new byte[length];
                Buffer.BlockCopy(localContent, offset, block, 0, length);
                localHashes[i] = VelocityShareCrypto.HashChunk(block);
            }

            // Find needed blocks
            var needed = new List<int>();
            int maxBlocks = Math.Max(localHashes.Count, remoteBlockHashes.Count);
            for (int i = 0; i < maxBlocks; i++)
            {
                bool localHas = localHashes.TryGetValue(i, out var lh);
                bool remoteHas = remoteBlockHashes.TryGetValue(i, out var rh);
                if (!localHas || !remoteHas || !BytesEqual(lh!, rh!))
                    needed.Add(i);
            }

            if (needed.Count == 0) return;

            // If size differs drastically, request full file
            if (localContent.Length > 0 && remoteSize > 0 &&
                (remoteSize > localContent.Length * 2 || localContent.Length > remoteSize * 2))
            {
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath, "all");
                await SendBinaryAsync(req);
                return;
            }

            string requested = string.Join(",", needed);
            byte[] request = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, filePath, requested);
            await SendBinaryAsync(request);
            OnLog?.Invoke($"[Sync Client] Delta: requesting {needed.Count}/{maxBlocks} blocks for {filePath}");
        }

        private async Task HandleRemoteBlockRequestAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            if (_localPath == null || _targetPeerId == null) return;

            string filePath = msg.FilePath;
            string fullPath = Path.Combine(_localPath, filePath);
            if (!File.Exists(fullPath)) return;

            byte[] fileContent = await File.ReadAllBytesAsync(fullPath);

            if (msg.RequestedBlocks == "all")
            {
                int totalBlocks = (fileContent.Length + BlockSize - 1) / BlockSize;
                for (int i = 0; i < totalBlocks; i++)
                {
                    int offset = i * BlockSize;
                    int length = Math.Min(BlockSize, fileContent.Length - offset);
                    byte[] block = new byte[length];
                    Buffer.BlockCopy(fileContent, offset, block, 0, length);
                    byte[] hash = VelocityShareCrypto.HashChunk(block);
                    byte[] pkt = Protocol.NdaSignaling.CreateBlockData(_targetPeerId, filePath, i, offset, block,
                        Convert.ToHexString(hash).ToLowerInvariant());
                    await SendBinaryAsync(pkt);
                    TotalBytesSent += length;
                }
            }
            else
            {
                foreach (var idxStr in msg.RequestedBlocks.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(idxStr.Trim(), out int idx)) continue;
                    int offset = idx * BlockSize;
                    if (offset >= fileContent.Length) continue;
                    int length = Math.Min(BlockSize, fileContent.Length - offset);
                    byte[] block = new byte[length];
                    Buffer.BlockCopy(fileContent, offset, block, 0, length);
                    byte[] hash = VelocityShareCrypto.HashChunk(block);
                    byte[] pkt = Protocol.NdaSignaling.CreateBlockData(_targetPeerId, filePath, idx, offset, block,
                        Convert.ToHexString(hash).ToLowerInvariant());
                    await SendBinaryAsync(pkt);
                    TotalBytesSent += length;
                }
            }

            // Signal completion
            byte[] fullHash = VelocityShareCrypto.HashChunk(fileContent);
            byte[] complete = Protocol.NdaSignaling.CreateDeltaComplete(_targetPeerId, filePath,
                Convert.ToHexString(fullHash).ToLowerInvariant());
            await SendBinaryAsync(complete);
        }

        private async Task HandleRemoteBlockDataAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            if (_localPath == null) return;

            string filePath = msg.FilePath;
            long offset = msg.BlockOffset;
            byte[] blockData = msg.Content;
            if (blockData == null || blockData.Length == 0) return;

            string fullPath = Path.Combine(_localPath, filePath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _watcher?.Dispose();
            try
            {
                using var fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                fs.Seek(offset, SeekOrigin.Begin);
                await fs.WriteAsync(blockData);
                TotalBytesReceived += blockData.Length;
            }
            finally
            {
                StartWatcher();
            }
        }

        // ── Full Sync / Manifest Processing ─────────────────────────────────

        private async Task ProcessRemoteManifest(string manifestData)
        {
            if (_localPath == null || _targetPeerId == null || string.IsNullOrEmpty(manifestData)) return;

            var remoteFiles = new Dictionary<string, FileEntry>();
            foreach (var entry in manifestData.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length >= 4)
                {
                    long.TryParse(parts[2], out long size);
                    long.TryParse(parts[3], out long mtime);
                    remoteFiles[parts[0]] = new FileEntry(parts[1], size, mtime);
                }
            }

            int sent = 0, requested = 0;

            // Files we have that they don't → send
            foreach (var (path, ourEntry) in _catalog)
            {
                if (!remoteFiles.ContainsKey(path))
                {
                    string fullPath = Path.Combine(_localPath, path);
                    if (File.Exists(fullPath))
                    {
                        byte[] content = await File.ReadAllBytesAsync(fullPath);
                        byte[] hash = VelocityShareCrypto.HashChunk(content);
                        string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                        byte[] pkt = Protocol.NdaSignaling.CreateUpdate(_targetPeerId, path, hashHex, content.Length, content);
                        await SendBinaryAsync(pkt);
                        sent++;
                        TotalBytesSent += content.Length;
                    }
                }
            }

            // Files they have that we don't → request
            foreach (var (path, theirEntry) in remoteFiles)
            {
                if (!_catalog.ContainsKey(path))
                {
                    byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, path, "all");
                    await SendBinaryAsync(req);
                    requested++;
                }
                else
                {
                    // Both have it — check hash
                    var ourEntry = _catalog[path];
                    if (ourEntry.Hash != theirEntry.Hash)
                    {
                        // LWW conflict resolution
                        bool weWin = ourEntry.LastModifiedUtc >= theirEntry.LastModifiedUtc;
                        if (weWin)
                        {
                            string fullPath = Path.Combine(_localPath, path);
                            if (File.Exists(fullPath))
                            {
                                byte[] content = await File.ReadAllBytesAsync(fullPath);
                                byte[] hash = VelocityShareCrypto.HashChunk(content);
                                string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                                byte[] pkt = Protocol.NdaSignaling.CreateUpdate(_targetPeerId, path, hashHex, content.Length, content);
                                await SendBinaryAsync(pkt);
                            }
                        }
                        else
                        {
                            byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, path, "all");
                            await SendBinaryAsync(req);
                        }
                    }
                }
            }

            byte[] complete = Protocol.NdaSignaling.CreateSyncManifestComplete(_targetPeerId);
            await SendBinaryAsync(complete);
            OnLog?.Invoke($"[Sync Client] Reconciliation: sent {sent}, requested {requested}");
        }

        private async Task HandleConflictResolutionAsync(Protocol.NdaSignaling.ParsedMessage msg)
        {
            if (_targetPeerId == null) return;
            bool theyWin = msg.Winner == "us"; // They say "us" = they win

            if (theyWin)
            {
                byte[] req = Protocol.NdaSignaling.CreateBlockRequest(_targetPeerId, msg.FilePath, "all");
                await SendBinaryAsync(req);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private async Task SendBinaryAsync(byte[] data)
        {
            if (_webSocket?.State != WebSocketState.Open || _targetPeerId == null) return;
            await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ── Catalog Persistence ─────────────────────────────────────────────

        private void LoadCatalog()
        {
            if (_catalogPath == null || !File.Exists(_catalogPath)) return;
            try
            {
                string json = File.ReadAllText(_catalogPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, FileEntryDto>>(json);
                if (data != null)
                {
                    foreach (var kvp in data)
                        _catalog[kvp.Key] = new FileEntry(kvp.Value.Hash, kvp.Value.Size, kvp.Value.LastModifiedUtc);
                }
            }
            catch { }
        }

        private void SaveCatalog()
        {
            if (_catalogPath == null) return;
            try
            {
                var dto = new Dictionary<string, FileEntryDto>();
                foreach (var kvp in _catalog)
                    dto[kvp.Key] = new FileEntryDto { Hash = kvp.Value.Hash, Size = kvp.Value.Size, LastModifiedUtc = kvp.Value.LastModifiedUtc };
                File.WriteAllText(_catalogPath, JsonSerializer.Serialize(dto));
            }
            catch { }
        }

        private sealed class FileEntryDto
        {
            public string Hash { get; set; } = "";
            public long Size { get; set; }
            public long LastModifiedUtc { get; set; }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
            _throttle?.Dispose();
            _cts?.Dispose();
            _webSocket?.Dispose();
        }
    }
}
