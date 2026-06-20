using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Protocol = VelocityShare.Protocol;

namespace VelocityShare.Server
{
    public class FileSyncEngine : IDisposable
    {
        private readonly string _syncFolderPath;
        private readonly FileSystemWatcher _watcher;
        private readonly string _metadataPath;
        private readonly ConcurrentDictionary<string, string> _fileCatalog = new();
        private readonly string _targetPeerId;
        private readonly Func<byte[], Task> _onFileChangedCallback;
        private readonly System.Threading.Timer _debounceTimer;
        private readonly ConcurrentQueue<string> _pendingChanges = new();
        private bool _isApplyingRemoteChange = false;

        public string SyncFolderPath => _syncFolderPath;
        public ConcurrentDictionary<Guid, (byte[] Key, byte[] Nonce, string FullPath, string FileHash)> ActiveSyncTransfers { get; } = new();

        public FileSyncEngine(string syncFolderPath, string targetPeerId, Func<byte[], Task> onFileChangedCallback)
        {
            _syncFolderPath = syncFolderPath;
            _targetPeerId = targetPeerId;
            _onFileChangedCallback = onFileChangedCallback;
            _metadataPath = Path.Combine(_syncFolderPath, ".velocity_sync_metadata.json");

            if (!Directory.Exists(_syncFolderPath))
            {
                Directory.CreateDirectory(_syncFolderPath);
            }

            LoadCatalog();

            _watcher = new FileSystemWatcher(_syncFolderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Created += OnFileSystemEvent;
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            _debounceTimer = new System.Threading.Timer(ProcessDebouncedChanges, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _watcher.EnableRaisingEvents = true;
            Console.WriteLine($"[Sync Engine] Started watching folder: {_syncFolderPath}");
        }

        public void Stop()
        {
            _watcher.EnableRaisingEvents = false;
            Console.WriteLine("[Sync Engine] Stopped watching folder.");
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (_isApplyingRemoteChange) return;

            // Skip metadata file changes
            if (e.FullPath.Equals(_metadataPath, StringComparison.OrdinalIgnoreCase)) return;

            _pendingChanges.Enqueue(e.FullPath);
            _debounceTimer.Change(500, Timeout.Infinite); // Debounce 500ms
        }

        private async void ProcessDebouncedChanges(object? state)
        {
            var processed = new HashSet<string>();
            while (_pendingChanges.TryDequeue(out var path))
            {
                if (processed.Add(path))
                {
                    await HandleFileChangeAsync(path);
                }
            }
        }

        private async Task HandleFileChangeAsync(string fullPath)
        {
            string relativePath = Path.GetRelativePath(_syncFolderPath, fullPath);

            if (!File.Exists(fullPath))
            {
                // File deleted
                if (_fileCatalog.TryRemove(relativePath, out _))
                {
                    SaveCatalog();

                    byte[] packet = Protocol.NdaSignaling.CreateDelete(_targetPeerId ?? "", relativePath);

                    await _onFileChangedCallback(packet);
                }
                return;
            }

            try
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
                byte[] hash = VelocityShareCrypto.HashChunk(fileBytes);
                string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

                _fileCatalog.TryGetValue(relativePath, out string? oldHash);
                if (hashHex != oldHash)
                {
                    _fileCatalog[relativePath] = hashHex;
                    SaveCatalog();

                    if (fileBytes.Length >= 65536)
                    {
                        var fileId = Guid.NewGuid();
                        byte[] key = new byte[32];
                        byte[] nonce = new byte[12];
                        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
                        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

                        ActiveSyncTransfers[fileId] = (key, nonce, fullPath, hashHex);

                        byte[] packet = Protocol.NdaSignaling.CreateOffer(_targetPeerId ?? "", relativePath, hashHex, fileBytes.Length, fileId, key, nonce);

                        await _onFileChangedCallback(packet);
                    }
                    else
                    {
                        byte[] packet = Protocol.NdaSignaling.CreateUpdate(_targetPeerId ?? "", relativePath, hashHex, fileBytes.Length, fileBytes);

                        await _onFileChangedCallback(packet);
                    }
                }
            }
            catch (IOException)
            {
                // File lock retry
            }
        }

        public void ConfirmRemoteSyncCompleted(string relativePath, string hash)
        {
            _fileCatalog[relativePath] = hash;
            SaveCatalog();
        }

        public async Task ApplyRemoteSyncAsync(string type, string relativePath, string hash, ReadOnlyMemory<byte> content)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.Contains(".."))
            {
                Console.WriteLine($"[Sync Engine Error] Path traversal blocked in ApplyRemoteSyncAsync: {relativePath}");
                return;
            }

            try
            {
                string combinedPath = Path.Combine(_syncFolderPath, relativePath);
                string fullPath = Path.GetFullPath(combinedPath);
                string canonicalSyncFolder = Path.GetFullPath(_syncFolderPath);
                string separator = Path.DirectorySeparatorChar.ToString();
                if (!canonicalSyncFolder.EndsWith(separator))
                {
                    canonicalSyncFolder += separator;
                }
                if (!fullPath.StartsWith(canonicalSyncFolder, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Sync Engine Error] Path escapes sync folder: {relativePath}");
                    return;
                }

                _isApplyingRemoteChange = true;
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (type == "sync_delete")
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    _fileCatalog.TryRemove(relativePath, out _);
                }
                else if (type == "sync_update" && !content.IsEmpty)
                {
                    await File.WriteAllBytesAsync(fullPath, content);
                    _fileCatalog[relativePath] = hash;
                }
                SaveCatalog();
            }
            finally
            {
                await Task.Delay(100);
                _isApplyingRemoteChange = false;
            }
        }

        private void LoadCatalog()
        {
            if (File.Exists(_metadataPath))
            {
                try
                {
                    string json = File.ReadAllText(_metadataPath);
                    var data = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(json);
                    if (data != null)
                    {
                        foreach (var kvp in data)
                        {
                            _fileCatalog[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch { }
            }
        }

        private void SaveCatalog()
        {
            try
            {
                string json = JsonSerializer.Serialize(_fileCatalog);
                File.WriteAllText(_metadataPath, json);
            }
            catch { }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _debounceTimer.Dispose();
        }
    }
}
