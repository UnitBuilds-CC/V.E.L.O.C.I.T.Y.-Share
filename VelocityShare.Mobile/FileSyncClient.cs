using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Mobile
{
    public class FileSyncClient : IDisposable
    {
        private string? _localPath;
        private string? _targetPeerId;
        private ClientWebSocket? _webSocket;
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, string> _catalog = new();
        private readonly ConcurrentQueue<string> _pendingChanges = new();
        private System.Threading.Timer? _debounceTimer;
        private CancellationTokenSource? _cts;
        private readonly string _metadataFileName = ".velocity_sync_metadata.json";
        private string? _metadataPath;
        
        public event Action<string>? OnLog;
        public event Action<string>? OnStatusChanged;

        public async Task StartAsync(string localPath, string serverUrl, string myPeerId, string targetPeerId)
        {
            _localPath = localPath;
            _targetPeerId = targetPeerId;
            _metadataPath = Path.Combine(_localPath, _metadataFileName);
            _cts = new CancellationTokenSource();

            if (!Directory.Exists(_localPath))
            {
                Directory.CreateDirectory(_localPath);
            }

            LoadCatalog();

            // Initialize WebSocket client
            _webSocket = new ClientWebSocket();
            Uri serverUri = new Uri($"{serverUrl.Replace("http", "ws")}/ws/share?peerId={myPeerId}");
            
            OnLog?.Invoke($"[Sync Client] Connecting to signaling server: {serverUri}");
            OnStatusChanged?.Invoke("CONNECTING");

            await _webSocket.ConnectAsync(serverUri, _cts.Token);
            OnLog?.Invoke("[Sync Client] WebSocket connected successfully.");
            OnStatusChanged?.Invoke("ACTIVE");

            // Start WebSocket receiving loop
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

            // Setup local file system watcher
            _watcher = new FileSystemWatcher(_localPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Created += OnFileSystemEvent;
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            _debounceTimer = new System.Threading.Timer(ProcessDebouncedChanges, null, Timeout.Infinite, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;

            OnLog?.Invoke($"[Sync Client] Started watching directory: {_localPath}");
        }

        public async Task StopAsync()
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Sync stopped by user", CancellationToken.None);
                }
                _webSocket.Dispose();
                _webSocket = null;
            }

            OnStatusChanged?.Invoke("INACTIVE");
            OnLog?.Invoke("[Sync Client] Sync stopped.");
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.Equals(_metadataPath, StringComparison.OrdinalIgnoreCase)) return;

            _pendingChanges.Enqueue(e.FullPath);
            _debounceTimer?.Change(500, Timeout.Infinite); // Debounce 500ms
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
            if (_localPath == null || _targetPeerId == null) return;

            string relativePath = Path.GetRelativePath(_localPath, fullPath);

            if (!File.Exists(fullPath))
            {
                // File deleted
                if (_catalog.TryRemove(relativePath, out _))
                {
                    SaveCatalog();
                    await SendSyncPayloadAsync(JsonSerializer.Serialize(new
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

                _catalog.TryGetValue(relativePath, out string? oldHash);
                if (hashHex != oldHash)
                {
                    _catalog[relativePath] = hashHex;
                    SaveCatalog();

                    await SendSyncPayloadAsync(JsonSerializer.Serialize(new
                    {
                        type = "sync_update",
                        file = relativePath,
                        hash = hashHex,
                        size = fileBytes.Length,
                        content = Convert.ToBase64String(fileBytes)
                    }));
                }
            }
            catch (IOException)
            {
                // Lock retry or skip temporary OS files
            }
        }

        private async Task SendSyncPayloadAsync(string syncEventJson)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open || _targetPeerId == null) return;

            var envelope = JsonSerializer.Serialize(new
            {
                type = "folder_sync_payload",
                sender = "local_sync_engine",
                target = _targetPeerId,
                data = syncEventJson
            });

            byte[] bytes = Encoding.UTF8.GetBytes(envelope);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            OnLog?.Invoke($"[Sync Client] Dispatched update for peer {_targetPeerId}");
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[1024 * 64];
            try
            {
                while (_webSocket != null && _webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string rawMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await HandleIncomingMessageAsync(rawMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Sync Client Receive Error] {ex.Message}");
            }
        }

        private async Task HandleIncomingMessageAsync(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                string type = doc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                
                if (type == "folder_sync_payload" && _localPath != null)
                {
                    string innerData = doc.RootElement.GetProperty("data").GetString() ?? "";
                    var innerDoc = JsonDocument.Parse(innerData);
                    string syncType = innerDoc.RootElement.GetProperty("type").GetString() ?? "";
                    string file = innerDoc.RootElement.GetProperty("file").GetString() ?? "";
                    string hash = innerDoc.RootElement.TryGetProperty("hash", out var hashProp) ? hashProp.GetString() ?? "" : "";
                    
                    byte[]? contentBytes = null;
                    if (innerDoc.RootElement.TryGetProperty("content", out var contentProp))
                    {
                        string base64Content = contentProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(base64Content))
                        {
                            contentBytes = Convert.FromBase64String(base64Content);
                        }
                    }

                    string fullPath = Path.Combine(_localPath, file);
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (syncType == "sync_delete")
                    {
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        _catalog.TryRemove(file, out _);
                    }
                    else if (syncType == "sync_update" && contentBytes != null)
                    {
                        // Temporarily stop watcher while writing to avoid feedback loop
                        if (_watcher != null) _watcher.EnableRaisingEvents = false;
                        try
                        {
                            await File.WriteAllBytesAsync(fullPath, contentBytes);
                            _catalog[file] = hash;
                        }
                        finally
                        {
                            if (_watcher != null) _watcher.EnableRaisingEvents = true;
                        }
                    }
                    SaveCatalog();
                    OnLog?.Invoke($"[Sync Client] Applied remote {syncType} for file: {file}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Sync Client Message Error] {ex.Message}");
            }
        }

        private void LoadCatalog()
        {
            if (_metadataPath == null || !File.Exists(_metadataPath)) return;
            try
            {
                string json = File.ReadAllText(_metadataPath);
                var data = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(json);
                if (data != null)
                {
                    foreach (var kvp in data) _catalog[kvp.Key] = kvp.Value;
                }
            }
            catch { }
        }

        private void SaveCatalog()
        {
            if (_metadataPath == null) return;
            try
            {
                string json = JsonSerializer.Serialize(_catalog);
                File.WriteAllText(_metadataPath, json);
            }
            catch { }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
            _cts?.Dispose();
            _webSocket?.Dispose();
        }
    }
}
