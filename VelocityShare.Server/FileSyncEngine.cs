using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server
{
    public class FileSyncEngine : IDisposable
    {
        private readonly string _syncFolderPath;
        private readonly FileSystemWatcher _watcher;
        private readonly string _metadataPath;
        private readonly ConcurrentDictionary<string, string> _fileCatalog = new();
        private readonly Func<string, Task> _onFileChangedCallback;
        private readonly System.Threading.Timer _debounceTimer;
        private readonly ConcurrentQueue<string> _pendingChanges = new();
        private bool _isApplyingRemoteChange = false;

        public string SyncFolderPath => _syncFolderPath;
        public ConcurrentDictionary<Guid, (byte[] Key, byte[] Nonce, string FullPath, string FileHash)> ActiveSyncTransfers { get; } = new();

        public FileSyncEngine(string syncFolderPath, Func<string, Task> onFileChangedCallback)
        {
            _syncFolderPath = syncFolderPath;
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
                    await _onFileChangedCallback(JsonSerializer.Serialize(new
                    {
                        type = "sync_delete",
                        file = relativePath
                    }));
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
                        Random.Shared.NextBytes(key);
                        Random.Shared.NextBytes(nonce);

                        ActiveSyncTransfers[fileId] = (key, nonce, fullPath, hashHex);

                        await _onFileChangedCallback(JsonSerializer.Serialize(new
                        {
                            type = "sync_vctp_offer",
                            file = relativePath,
                            hash = hashHex,
                            size = fileBytes.Length,
                            fileId = fileId,
                            key = Convert.ToBase64String(key),
                            nonce = Convert.ToBase64String(nonce)
                        }));
                    }
                    else
                    {
                        await _onFileChangedCallback(JsonSerializer.Serialize(new
                        {
                            type = "sync_update",
                            file = relativePath,
                            hash = hashHex,
                            size = fileBytes.Length,
                            content = Convert.ToBase64String(fileBytes)
                        }));
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

        public async Task ApplyRemoteSyncAsync(string type, string relativePath, string hash, byte[]? content)
        {
            _isApplyingRemoteChange = true;
            try
            {
                string fullPath = Path.Combine(_syncFolderPath, relativePath);
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
                else if (type == "sync_update" && content != null)
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
