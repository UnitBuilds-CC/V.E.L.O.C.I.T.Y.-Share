using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VelocityShare.Server;
using Protocol = VelocityShare.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Configure CORS to allow full web dashboard testing
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseWebSockets();
app.UseStaticFiles();

// Thread-safe dictionary to track active peer WebSocket connections
var activePeers = new ConcurrentDictionary<string, WebSocket>();

// In-memory dropsite configuration
string defaultUploadsDir = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "uploads");
if (!Directory.Exists(defaultUploadsDir))
{
    Directory.CreateDirectory(defaultUploadsDir);
}
var dropsiteConfig = new ConcurrentDictionary<string, string>();
dropsiteConfig["type"] = "local_nas";
dropsiteConfig["path"] = defaultUploadsDir;

FileSyncEngine? activeSyncEngine = null;
var activeReceivers = new ConcurrentDictionary<Guid, VctpReceiver>();

string sandboxRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

bool IsPathInsideSandbox(string path, string sandbox)
{
    if (string.IsNullOrEmpty(path) || path.Contains("..")) return false;
    try
    {
        string fullPath = Path.GetFullPath(path);
        string fullSandbox = Path.GetFullPath(sandbox);
        string separator = Path.DirectorySeparatorChar.ToString();
        if (!fullSandbox.EndsWith(separator))
        {
            fullSandbox += separator;
        }
        return fullPath.Equals(Path.GetFullPath(sandbox), StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullSandbox, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

bool IsFileInsideSyncFolder(string file, string syncFolder)
{
    if (string.IsNullOrEmpty(file) || file.Contains("..")) return false;
    try
    {
        string combinedPath = Path.Combine(syncFolder, file);
        string fullPath = Path.GetFullPath(combinedPath);
        string canonicalSyncFolder = Path.GetFullPath(syncFolder);
        string separator = Path.DirectorySeparatorChar.ToString();
        if (!canonicalSyncFolder.EndsWith(separator))
        {
            canonicalSyncFolder += separator;
        }
        return fullPath.StartsWith(canonicalSyncFolder, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

// Start background loop to clean up zombied VctpReceiver instances (inactivity > 60 seconds)
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var cutoff = DateTime.UtcNow.AddSeconds(-60);
            foreach (var kvp in activeReceivers)
            {
                if (kvp.Value.LastActiveTime < cutoff)
                {
                    if (activeReceivers.TryRemove(kvp.Key, out var receiver))
                    {
                        Console.WriteLine($"[Sync Engine] Cleaning up zombied VctpReceiver {kvp.Key} due to 60s inactivity.");
                        receiver.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sync Engine Error] Error in activeReceivers cleanup task: {ex.Message}");
        }
    }
});

// POST /api/share/sync/start: Starts the directory sync engine watching a local folder path
app.MapPost("/api/share/sync/start", async (HttpContext context) =>
{
    string path = context.Request.Query["path"].ToString();
    string targetPeerId = context.Request.Query["targetPeerId"].ToString();

    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(targetPeerId))
    {
        return Results.BadRequest("Missing path or targetPeerId parameters.");
    }

    // Path traversal / sandboxing verification
    if (!IsPathInsideSandbox(path, sandboxRoot))
    {
        return Results.BadRequest("Path must be located inside the sandboxed wwwroot folder and cannot contain directory traversal.");
    }
    
    string fullPath = Path.GetFullPath(path);

    if (!Directory.Exists(fullPath))
    {
        Directory.CreateDirectory(fullPath);
    }

    if (activeSyncEngine != null)
    {
        activeSyncEngine.Stop();
        activeSyncEngine.Dispose();
        activeSyncEngine = null;
    }

    activeSyncEngine = new FileSyncEngine(path, targetPeerId, async (binaryPacket) =>
    {
        // 1. Dispatch directly to target socket if it exists on this server instance (same-server peer connection)
        if (activePeers.TryGetValue(targetPeerId, out var targetSocket) && targetSocket.State == WebSocketState.Open)
        {
            await targetSocket.SendAsync(new ArraySegment<byte>(binaryPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
            Console.WriteLine($"[Sync Engine] Dispatched binary sync payload directly to peer {targetPeerId}");
        }

        // 2. Dispatch to all other active sockets connected to this server instance (local browser tab(s))
        foreach (var peer in activePeers)
        {
            if (peer.Key != targetPeerId && peer.Value.State == WebSocketState.Open)
            {
                await peer.Value.SendAsync(new ArraySegment<byte>(binaryPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
                Console.WriteLine($"[Sync Engine] Dispatched binary sync payload to local peer {peer.Key} for forwarding");
            }
        }
    });

    activeSyncEngine.Start();
    Console.WriteLine($"[Sync Engine] Activated sync loop for path: {path} targeting peer: {targetPeerId}");
    return Results.Ok(new { status = "STARTED", path, targetPeerId });
});

// POST /api/share/sync/stop: Stops the active sync engine
app.MapPost("/api/share/sync/stop", () =>
{
    if (activeSyncEngine != null)
    {
        activeSyncEngine.Stop();
        activeSyncEngine.Dispose();
        activeSyncEngine = null;
        Console.WriteLine("[Sync Engine] Deactivated sync loop.");
    }
    return Results.Ok(new { status = "STOPPED" });
});

// WebSocket Handshake and Signaling Gateway for P2P WebRTC coordination
app.Map("/ws/share", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string peerId = context.Request.Query["peerId"].ToString();
    if (string.IsNullOrEmpty(peerId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Missing peerId parameter.");
        return;
    }

    if (activePeers.TryGetValue(peerId, out var existingSocket) && existingSocket.State == WebSocketState.Open)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsync("Peer ID is already online.");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    activePeers[peerId] = webSocket;
    Console.WriteLine($"[WebSocket] Peer connected: {peerId}. Active peers: {activePeers.Count}");

    // Broadcast updated online peer list
    await BroadcastPeerListAsync(activePeers);

    var buffer = new byte[1024 * 64];
    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                long totalBytesRead = 0;
                const long MaxMessageSize = 10 * 1024 * 1024; // 10MB limit
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    ms.Write(buffer, 0, result.Count);
                    totalBytesRead += result.Count;
                    if (totalBytesRead > MaxMessageSize)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message exceeds size limit", CancellationToken.None);
                        break;
                    }
                }
                while (!result.EndOfMessage);

                if (webSocket.State != WebSocketState.Open || result.MessageType == WebSocketMessageType.Close || totalBytesRead > MaxMessageSize)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(ms, Encoding.UTF8))
                    {
                        string rawMsg = await reader.ReadToEndAsync();
                        try
                        {
                            var msgDoc = JsonDocument.Parse(rawMsg);
                            string msgType = msgDoc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                            
                            if (msgType == "folder_sync_payload" && activeSyncEngine != null)
                            {
                                string innerData = msgDoc.RootElement.GetProperty("data").GetString() ?? "";
                                var innerDoc = JsonDocument.Parse(innerData);
                                string syncType = innerDoc.RootElement.GetProperty("type").GetString() ?? "";
                                
                                if (syncType == "sync_vctp_offer")
                                {
                                    string file = innerDoc.RootElement.GetProperty("file").GetString() ?? "";
                                    string hash = innerDoc.RootElement.GetProperty("hash").GetString() ?? "";
                                    Guid fileId = innerDoc.RootElement.GetProperty("fileId").GetGuid();
                                    byte[] key = Convert.FromBase64String(innerDoc.RootElement.GetProperty("key").GetString() ?? "");
                                    byte[] nonce = Convert.FromBase64String(innerDoc.RootElement.GetProperty("nonce").GetString() ?? "");

                                     string syncFolder = activeSyncEngine.SyncFolderPath;
                                     if (!IsFileInsideSyncFolder(file, syncFolder))
                                     {
                                         Console.WriteLine($"[Sync Engine Error] Path traversal blocked: {file}");
                                         continue;
                                     }
                                     string combinedPath = Path.Combine(syncFolder, file);
                                     string targetDir = Path.GetDirectoryName(Path.GetFullPath(combinedPath)) ?? syncFolder;

                                     var receiver = new VctpReceiver(targetDir, key, nonce, port: 0);
                                    activeReceivers[fileId] = receiver;

                                    receiver.OnTransferComplete += (filePath, fileHash) =>
                                    {
                                        activeSyncEngine.ConfirmRemoteSyncCompleted(file, fileHash);
                                        receiver.Dispose();
                                        activeReceivers.TryRemove(fileId, out _);
                                        Console.WriteLine($"[Sync Engine] VCTP sync receiver complete for {file}");
                                    };
                                    receiver.Start();

                                    string senderPeer = msgDoc.RootElement.GetProperty("sender").GetString() ?? "";
                                    var acceptPayload = JsonSerializer.Serialize(new
                                    {
                                        type = "folder_sync_payload",
                                        sender = "local_sync_engine",
                                        target = senderPeer,
                                        data = JsonSerializer.Serialize(new
                                        {
                                            type = "sync_vctp_accept",
                                            fileId = fileId,
                                            port = receiver.Port
                                        })
                                    });

                                    if (activePeers.TryGetValue(senderPeer, out var senderSocket) && senderSocket.State == WebSocketState.Open)
                                    {
                                        await senderSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(acceptPayload)), WebSocketMessageType.Text, true, CancellationToken.None);
                                        Console.WriteLine($"[Sync Engine] Dispatched sync_vctp_accept back to peer {senderPeer} on port {receiver.Port}");
                                    }
                                }
                                else if (syncType == "sync_vctp_accept")
                                {
                                    Guid fileId = innerDoc.RootElement.GetProperty("fileId").GetGuid();
                                    int port = innerDoc.RootElement.GetProperty("port").GetInt32();

                                    if (activeSyncEngine.ActiveSyncTransfers.TryRemove(fileId, out var senderInfo))
                                    {
                                        var (key, nonce, fullPath, fileHash) = senderInfo;
                                        var remoteEP = new IPEndPoint(IPAddress.Loopback, port);
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                using var vctpSender = new VctpSender(fullPath, fileId, fileHash, remoteEP, key, nonce, targetRateMbps: 1000.0);
                                                await vctpSender.StartAsync();
                                                Console.WriteLine($"[Sync Engine] VCTP sync sender complete for {fullPath}");
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"[Sync Engine] VCTP sync sender failed: {ex.Message}");
                                            }
                                        });
                                    }
                                }
                                else
                                {
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

                                    await activeSyncEngine.ApplyRemoteSyncAsync(syncType, file, hash, contentBytes);
                                    Console.WriteLine($"[Sync Engine] Applied remote {syncType} for file {file}");
                                }
                            }

                            string target = msgDoc.RootElement.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(target) && activePeers.TryGetValue(target, out var targetSocket))
                            {
                                // Forward signaling/sync packet directly to recipient peer
                                if (targetSocket.State == WebSocketState.Open)
                                {
                                    if (msgType == "folder_sync_payload")
                                    {
                                        string senderIp = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
                                        if (senderIp == "::1" || senderIp == "127.0.0.1")
                                        {
                                            senderIp = "127.0.0.1";
                                        }

                                        var node = System.Text.Json.Nodes.JsonNode.Parse(rawMsg);
                                        if (node != null)
                                        {
                                            node["senderIp"] = senderIp;
                                            string modifiedMsg = node.ToJsonString();
                                            byte[] modifiedBytes = Encoding.UTF8.GetBytes(modifiedMsg);
                                            await targetSocket.SendAsync(new ArraySegment<byte>(modifiedBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                                        }
                                    }
                                    else
                                    {
                                        await targetSocket.SendAsync(new ArraySegment<byte>(ms.ToArray()), WebSocketMessageType.Text, true, CancellationToken.None);
                                    }
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // Invalid JSON, ignore packet
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    byte[] rawBytes = ms.ToArray();
                    try
                    {
                        var message = new Protocol.NdaSignaling.ParsedMessage(rawBytes);
                        string target = message.TargetPeerId;

                        if (target == "local_sync_engine" && activeSyncEngine != null)
                        {
                            // Process locally in the sync engine
                            if (message.Action == "delete") // Delete
                            {
                                string file = message.FilePath;
                                await activeSyncEngine.ApplyRemoteSyncAsync("sync_delete", file, "", null);
                                Console.WriteLine($"[Server Local Sync] Applied NDA delete for {file}");
                            }
                            else if (message.Action == "update") // Update
                            {
                                string file = message.FilePath;
                                string hash = message.HashHex;
                                byte[] content = message.Content;

                                await activeSyncEngine.ApplyRemoteSyncAsync("sync_update", file, hash, content);
                                Console.WriteLine($"[Server Local Sync] Applied NDA update for {file}");
                            }
                            else if (message.Action == "offer") // VCTP Offer
                            {
                                string file = message.FilePath;
                                string hash = message.HashHex;
                                Guid fid = message.FileId;
                                byte[] key = message.Key;
                                byte[] nonce = message.Nonce;

                                string syncFolder = activeSyncEngine.SyncFolderPath;
                                if (!IsFileInsideSyncFolder(file, syncFolder))
                                {
                                    Console.WriteLine($"[Sync Engine Error] Path traversal blocked in NDA offer: {file}");
                                    continue;
                                }
                                string combinedPath = Path.Combine(syncFolder, file);
                                string targetDir = Path.GetDirectoryName(Path.GetFullPath(combinedPath)) ?? syncFolder;

                                var receiver = new VctpReceiver(targetDir, key, nonce, port: 0);
                                activeReceivers[fid] = receiver;

                                receiver.OnTransferComplete += (filePath, fileHash) =>
                                {
                                    activeSyncEngine.ConfirmRemoteSyncCompleted(file, fileHash);
                                    receiver.Dispose();
                                    activeReceivers.TryRemove(fid, out _);
                                    Console.WriteLine($"[Sync Engine] VCTP sync receiver complete for {file}");
                                };
                                receiver.Start();

                                // Send accept NDA packet back to peer
                                byte[] acceptPacket = Protocol.NdaSignaling.CreateAccept(peerId, fid, receiver.Port);

                                await webSocket.SendAsync(new ArraySegment<byte>(acceptPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                            else if (message.Action == "accept") // VCTP Accept
                            {
                                Guid fid = message.FileId;
                                int port = message.Port;

                                if (activeSyncEngine.ActiveSyncTransfers.TryRemove(fid, out var senderInfo))
                                {
                                    var (key, nonce, fullPath, fileHash) = senderInfo;
                                    var remoteEP = new IPEndPoint(IPAddress.Loopback, port);
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            using var vctpSender = new VctpSender(fullPath, fid, fileHash, remoteEP, key, nonce, targetRateMbps: 1000.0);
                                            await vctpSender.StartAsync();
                                            Console.WriteLine($"[Sync Engine] VCTP sync sender complete for {fullPath}");
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[Sync Engine] VCTP sync sender failed: {ex.Message}");
                                        }
                                    });
                                }
                            }
                        }
                        else if (activePeers.TryGetValue(target, out var targetSocket) && targetSocket.State == WebSocketState.Open)
                        {
                            // Route/forward to other peer
                            if (message.Action == "accept")
                            {
                                string senderIp = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
                                if (senderIp == "::1" || senderIp == "127.0.0.1")
                                {
                                    senderIp = "127.0.0.1";
                                }
                                byte[] forwardPacket = Protocol.NdaSignaling.CreateAccept(target, message.FileId, message.Port, senderIp);
                                await targetSocket.SendAsync(new ArraySegment<byte>(forwardPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                            else
                            {
                                await targetSocket.SendAsync(new ArraySegment<byte>(rawBytes), WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Server NDA Signaling Error] {ex.Message}");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WebSocket Error] Peer {peerId}: {ex.Message}");
    }
    finally
    {
        activePeers.TryRemove(peerId, out _);
        Console.WriteLine($"[WebSocket] Peer disconnected: {peerId}. Active peers: {activePeers.Count}");
        await BroadcastPeerListAsync(activePeers);
    }
});

// GET /api/share/peers: Lists all currently online handshake peers
app.MapGet("/api/share/peers", () =>
{
    return Results.Ok(activePeers.Keys);
});

// POST /api/share/dumpsite: Configures user-assigned custom dumpsite (NAS, Google Drive Mock, OneDrive Mock)
app.MapPost("/api/share/dumpsite", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    string body = await reader.ReadToEndAsync();
    try
    {
        var doc = JsonDocument.Parse(body);
        string type = doc.RootElement.GetProperty("type").GetString() ?? "local_nas";
        string path = doc.RootElement.GetProperty("path").GetString() ?? "";

        if (string.IsNullOrEmpty(path))
        {
            return Results.BadRequest("Path cannot be empty.");
        }

        // Path traversal / sandboxing verification
        if (!IsPathInsideSandbox(path, sandboxRoot))
        {
            return Results.BadRequest("Path must be located inside the sandboxed wwwroot folder and cannot contain directory traversal.");
        }
        
        string fullPath = Path.GetFullPath(path);

        if (type == "local_nas" && !Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }

        dropsiteConfig["type"] = type;
        dropsiteConfig["path"] = fullPath;
        Console.WriteLine($"[Dropsite Config] Updated to type: {type}, path: {path}");
        return Results.Ok(dropsiteConfig);
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Invalid dropsite configuration payload: {ex.Message}");
    }
});

app.MapGet("/api/share/dumpsite", () =>
{
    return Results.Ok(dropsiteConfig);
});

// POST /api/share/upload: Server-buffered file upload fallback (when peer is offline)
app.MapPost("/api/share/upload", async (HttpContext context) =>
{
    if (!context.Request.HasFormContentType)
    {
        return Results.BadRequest("Invalid form content type.");
    }

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    string fileId = form["fileId"].ToString();
    string chunkIndexStr = form["chunkIndex"].ToString();
    string checksum = form["checksum"].ToString();
    string encryptKeyHex = form["encryptionKey"].ToString(); // Optional key context

    if (file == null || string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(chunkIndexStr))
    {
        return Results.BadRequest("Missing required fields (file, fileId, chunkIndex).");
    }

    // Strictly sanitize and validate inputs to prevent Path Traversal attacks
    if (!System.Text.RegularExpressions.Regex.IsMatch(fileId, "^[a-zA-Z0-9_-]+$") ||
        !System.Text.RegularExpressions.Regex.IsMatch(chunkIndexStr, "^\\d+$"))
    {
        return Results.BadRequest("Invalid fileId or chunkIndex formatting. Threat detected.");
    }

    // Save to the configured dropsite
    string targetFolder = Path.Combine(dropsiteConfig["path"], fileId);
    if (!Directory.Exists(targetFolder))
    {
        Directory.CreateDirectory(targetFolder);
    }

    string chunkPath = Path.Combine(targetFolder, $"chunk_{chunkIndexStr}");
    using (var stream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        await file.CopyToAsync(stream);
    }

    // Server-side fast FFI verification check
    byte[] chunkBytes = await File.ReadAllBytesAsync(chunkPath);
    byte[] calculatedHash = VelocityShareCrypto.HashChunk(chunkBytes);
    string calculatedHashHex = Convert.ToHexString(calculatedHash).ToLowerInvariant();

    if (!string.IsNullOrEmpty(checksum) && !calculatedHashHex.Equals(checksum, StringComparison.OrdinalIgnoreCase))
    {
        System.IO.File.Delete(chunkPath);
        return Results.BadRequest($"FFI Integrity verification check failed! Calculated: {calculatedHashHex}, Received: {checksum}");
    }

    Console.WriteLine($"[Upload Fallback] Saved chunk {chunkIndexStr} of file {fileId} successfully. Verified checksum: {calculatedHashHex}");
    return Results.Ok(new { fileId, chunkIndex = chunkIndexStr, checksum = calculatedHashHex });
});

// GET /api/share/download: Server-buffered file download fallback
app.MapGet("/api/share/download", async (HttpContext context) =>
{
    string fileId = context.Request.Query["fileId"].ToString();
    string chunkIndexStr = context.Request.Query["chunkIndex"].ToString();

    if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(chunkIndexStr))
    {
        return Results.BadRequest("Missing required parameters (fileId, chunkIndex).");
    }

    // Strictly sanitize and validate inputs to prevent Path Traversal attacks
    if (!System.Text.RegularExpressions.Regex.IsMatch(fileId, "^[a-zA-Z0-9_-]+$") ||
        !System.Text.RegularExpressions.Regex.IsMatch(chunkIndexStr, "^\\d+$"))
    {
        return Results.BadRequest("Invalid fileId or chunkIndex formatting. Threat detected.");
    }

    string targetFolder = Path.Combine(dropsiteConfig["path"], fileId);
    string chunkPath = Path.Combine(targetFolder, $"chunk_{chunkIndexStr}");

    if (!System.IO.File.Exists(chunkPath))
    {
        return Results.NotFound($"Chunk {chunkIndexStr} of file {fileId} not found.");
    }

    byte[] chunkBytes = await File.ReadAllBytesAsync(chunkPath);
    return Results.File(chunkBytes, "application/octet-stream");
});

// GET /api/share/test: Runs unmanaged Rust FFI crypto self-test
app.MapGet("/api/share/test", () =>
{
    try
    {
        // Test 1: SHA-256 Hashing FFI
        byte[] testBlock = Encoding.UTF8.GetBytes("velocity_share_handshake_payload");
        byte[] hash = VelocityShareCrypto.HashChunk(testBlock);
        string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
        string expectedHash = "2e5b5654af78be75bb24cbc6bba699e0d9ed7f5049cbc3136ce55e6c2848d5fc";
        
        if (!hashHex.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem($"SHA256 FFI verification failed! Expected: {expectedHash}, Got: {hashHex}");
        }

        // Test 2: ChaCha20-Poly1305 Encryption FFI
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        byte[] plaintext = Encoding.UTF8.GetBytes("secure_enterprise_payload_data");
        
        var (ciphertext, tag) = VelocityShareCrypto.EncryptBlock(plaintext, key, nonce);
        byte[] decrypted = VelocityShareCrypto.DecryptBlock(ciphertext, key, nonce, tag);
        string decryptedStr = Encoding.UTF8.GetString(decrypted);

        if (!decryptedStr.Equals("secure_enterprise_payload_data"))
        {
            return Results.Problem($"ChaCha20 FFI encryption/decryption loop failed! Decrypted: '{decryptedStr}'");
        }

        return Results.Ok(new { status = "PASS", sha256 = hashHex, decrypted = decryptedStr });
    }
    catch (Exception ex)
    {
        return Results.Problem($"FFI diagnostic execution crashed: {ex.Message}\n{ex.StackTrace}");
    }
});

// GET /api/share/test/benchmark: Runs relative performance comparison benchmark for V.E.L.O.C.I.T.Y. Share crypto
app.MapGet("/api/share/test/benchmark", () =>
{
    try
    {
        byte[] block = new byte[64 * 1024]; // 64KB chunk size
        Random.Shared.NextBytes(block);
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(nonce);

        const int iterations = 10000;
        double totalMb = (double)iterations * block.Length / (1024.0 * 1024.0);

        // 1. SHA-256 Hashing Benchmarks
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            byte[] hash = VelocityShareCrypto.HashChunk(block);
        }
        sw.Stop();
        double rustHashMs = sw.Elapsed.TotalMilliseconds;
        double rustHashSpeed = totalMb / (rustHashMs / 1000.0);

        sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(block);
        }
        sw.Stop();
        double netHashMs = sw.Elapsed.TotalMilliseconds;
        double netHashSpeed = totalMb / (netHashMs / 1000.0);

        // 2. ChaCha20-Poly1305 Encrypt/Decrypt Loop Benchmarks
        sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var (cipher, tag) = VelocityShareCrypto.EncryptBlock(block, key, nonce);
            byte[] decrypted = VelocityShareCrypto.DecryptBlock(cipher, key, nonce, tag);
        }
        sw.Stop();
        double rustCipherMs = sw.Elapsed.TotalMilliseconds;
        double rustCipherSpeed = totalMb / (rustCipherMs / 1000.0);

        sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(key);
            byte[] cipher = new byte[block.Length];
            byte[] tag = new byte[16];
            byte[] decrypted = new byte[block.Length];
            chacha.Encrypt(nonce, block, cipher, tag);
            chacha.Decrypt(nonce, cipher, tag, decrypted);
        }
        sw.Stop();
        double netCipherMs = sw.Elapsed.TotalMilliseconds;
        double netCipherSpeed = totalMb / (netCipherMs / 1000.0);

        return Results.Ok(new
        {
            message = "V.E.L.O.C.I.T.Y. Share Cryptographic Engine Benchmarks (625 MB processed per phase)",
            sha256 = new
            {
                rust_ffi = new { time_ms = rustHashMs, speed_mbps = rustHashSpeed },
                net_native = new { time_ms = netHashMs, speed_mbps = netHashSpeed },
                relative_ratio = netHashMs / rustHashMs
            },
            chacha20_poly1305 = new
            {
                rust_ffi = new { time_ms = rustCipherMs, speed_mbps = rustCipherSpeed },
                net_native = new { time_ms = netCipherMs, speed_mbps = netCipherSpeed },
                relative_ratio = netCipherMs / rustCipherMs
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Benchmark failed: {ex.Message}");
    }
});

// GET /api/share/test/vctp: Performs automated custom transport (V.C.T.P.) integrity, speed, and interruptibility tests
app.MapGet("/api/share/test/vctp", async () =>
{
    string testFolder = Path.Combine(Directory.GetCurrentDirectory(), "vctp_test_run");
    if (Directory.Exists(testFolder))
    {
        try { Directory.Delete(testFolder, true); } catch { }
    }
    Directory.CreateDirectory(testFolder);

    string srcPath = Path.Combine(testFolder, "source.bin");
    string destFolder = Path.Combine(testFolder, "destination");
    Directory.CreateDirectory(destFolder);
    string destPath = Path.Combine(destFolder, "source.bin");

    try
    {
        // 1. Generate 50MB of random source data
        const int fileSize = 50 * 1024 * 1024; // 50MB
        byte[] srcData = new byte[fileSize];
        Random.Shared.NextBytes(srcData);
        await File.WriteAllBytesAsync(srcPath, srcData);

        // 2. Compute expected SHA-256 hash
        byte[] srcHash = VelocityShareCrypto.HashChunk(srcData);
        string srcHashHex = Convert.ToHexString(srcHash).ToLowerInvariant();

        // 3. Setup encryption keys (32 bytes) and nonce (12 bytes)
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(nonce);

        Guid fileId = Guid.NewGuid();
        var logs = new List<string>();
        
        // 4. Start VctpReceiver
        using var receiver = new VctpReceiver(destFolder, key, nonce, port: 0);
        receiver.OnLog += (log) => { logs.Add($"[Receiver] {log}"); Console.WriteLine($"[Receiver] {log}"); };
        receiver.Start();

        var remoteEP = new IPEndPoint(IPAddress.Loopback, receiver.Port);

        // 5. Test Interruption and Resumability!
        logs.Add("--- Beginning Phase 1: Transfer with Interruption ---");
        Console.WriteLine("--- Beginning Phase 1: Transfer with Interruption ---");
        using (var sender = new VctpSender(srcPath, fileId, srcHashHex, remoteEP, key, nonce, targetRateMbps: 50.0))
        {
            sender.OnLog += (log) => { logs.Add($"[Sender] {log}"); Console.WriteLine($"[Sender] {log}"); };
            
            // Start the sender in the background
            var senderTask = sender.StartAsync();

            // Wait until some blocks are transferred (e.g. 200ms)
            await Task.Delay(200);

            // Cancel/Dispose the sender mid-transfer
            logs.Add("--- FORCE KILLING Sender mid-transfer to simulate power/network cut ---");
            Console.WriteLine("--- FORCE KILLING Sender mid-transfer to simulate power/network cut ---");
        }

        // Verify partial target file and its companion .vctmeta file exist
        string destMetaPath = destPath + ".vctmeta";
        bool metaExists = File.Exists(destMetaPath);
        long partialSize = File.Exists(destPath) ? new FileInfo(destPath).Length : 0;
        logs.Add($"[Verification] Partial destination file size: {partialSize} bytes. Journal meta file exists: {metaExists}");

        // 6. Resume Phase: Start a new VctpSender with the same session parameters
        logs.Add("--- Beginning Phase 2: Resuming Transfer ---");
        
        // Wait briefly for sockets to release
        await Task.Delay(200);

        string finalDestHash = "";
        var tcs = new TaskCompletionSource<bool>();
        receiver.OnTransferComplete += (path, hash) =>
        {
            finalDestHash = hash;
            tcs.TrySetResult(true);
        };

        var resumeSw = System.Diagnostics.Stopwatch.StartNew();
        using (var senderResume = new VctpSender(srcPath, fileId, srcHashHex, remoteEP, key, nonce, targetRateMbps: 2500.0))
        {
            senderResume.OnLog += (log) => { logs.Add($"[Sender Resume] {log}"); Console.WriteLine($"[Sender Resume] {log}"); };
            await senderResume.StartAsync();
            
            // Wait for receiver to signal completion
            await Task.WhenAny(tcs.Task, Task.Delay(10000));
        }
        resumeSw.Stop();

        // 7. Verify Integrity
        bool verified = finalDestHash.Equals(srcHashHex, StringComparison.OrdinalIgnoreCase);
        logs.Add($"[Verification] Final Hash Check: Expected={srcHashHex}, Got={finalDestHash}. MATCH={verified}");

        if (!verified)
        {
            return Results.BadRequest(new
            {
                status = "FAILED",
                message = "VCTP verification failed: hash mismatch or timeout.",
                logs = logs
            });
        }

        double elapsedSec = resumeSw.Elapsed.TotalSeconds;
        double throughputMB = (fileSize / (1024.0 * 1024.0)) / elapsedSec;

        return Results.Ok(new
        {
            status = "PASS",
            message = "V.C.T.P. zero-copy, rate-paced, interruptible file sync test completed successfully.",
            duration_sec = elapsedSec,
            throughput_mbps = throughputMB * 8.0,
            throughput_mbs = throughputMB,
            logs = logs
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"VCTP test execution crashed: {ex.Message}\n{ex.StackTrace}");
    }
    finally
    {
        // Cleanup
        try
        {
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }
        }
        catch { }
    }
});

// GET /api/share/test/vctp/benchmark: Performs 100% in-memory V.C.T.P. speed and performance verification benchmark (250MB)
app.MapGet("/api/share/test/vctp/benchmark", async () =>
{
    try
    {
        const long fileSize = 250 * 1024 * 1024; // 250MB
        
        // 1. Create in-memory source MMF
        using var srcMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(null, fileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
        
        // Populate with random data in-memory and hash it
        byte[] srcData = new byte[65536];
        Random.Shared.NextBytes(srcData);
        
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        
        unsafe
        {
            using (var accessor = srcMmf.CreateViewAccessor(0, fileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite))
            {
                byte* pMmf = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pMmf);
                try
                {
                    for (long offset = 0; offset < fileSize; offset += srcData.Length)
                    {
                        int len = (int)Math.Min(srcData.Length, fileSize - offset);
                        fixed (byte* pSrc = srcData)
                        {
                            Buffer.MemoryCopy(pSrc, pMmf + offset, len, len);
                        }
                        sha.AppendData(srcData, 0, len);
                    }
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        byte[] srcHash = sha.GetHashAndReset();
        string srcHashHex = Convert.ToHexString(srcHash).ToLowerInvariant();

        // 2. Create in-memory destination MMF
        using var destMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(null, fileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);

        // Warm up the ThreadPool
        Parallel.For(0, Environment.ProcessorCount, i => { });

        // Create the views that will be passed directly to VctpReceiver and VctpSender
        using var srcAccessor = srcMmf.CreateViewAccessor(0, fileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        using var destAccessor = destMmf.CreateViewAccessor(0, fileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);

        // Pre-touch / Warm the destination memory pages to allocate physical RAM and avoid page faults during the benchmark!
        unsafe
        {
            byte* pDest = null;
            destAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pDest);
            try
            {
                int warmingWorkers = 6; // Matching the 6 P-cores
                long warmingChunk = fileSize / warmingWorkers;
                Parallel.For(0, warmingWorkers, w =>
                {
                    long start = w * warmingChunk;
                    long end = (w == warmingWorkers - 1) ? fileSize : start + warmingChunk;
                    for (long offset = start; offset < end; offset += 4096)
                    {
                        pDest[offset] = 0;
                    }
                });
            }
            finally
            {
                destAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        // Warm the source pages too to ensure physical allocation and TLB caching
        unsafe
        {
            byte* pSrc = null;
            srcAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pSrc);
            try
            {
                int warmingWorkers = 6; // Matching the 6 P-cores
                long warmingChunk = fileSize / warmingWorkers;
                Parallel.For(0, warmingWorkers, w =>
                {
                    long start = w * warmingChunk;
                    long end = (w == warmingWorkers - 1) ? fileSize : start + warmingChunk;
                    byte sum = 0;
                    for (long offset = start; offset < end; offset += 4096)
                    {
                        sum ^= pSrc[offset];
                    }
                    if (sum == 42) GC.KeepAlive(sum);
                });
            }
            finally
            {
                srcAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        // --- Dynamic Sweep Benchmark ---
        var sweepResults = new List<object>();
        double bestReadSpeed = 0, bestWriteSpeed = 0, bestCopySpeed = 0;
        int bestReadWorkers = 6, bestWriteWorkers = 6, bestCopyWorkers = 6;
        string bestReadAff = "p_cores", bestWriteAff = "p_cores", bestCopyAff = "p_cores";
        string bestReadPart = "dynamic", bestWritePart = "dynamic", bestCopyPart = "dynamic";
        int bestReadUnroll = 8, bestWriteUnroll = 8, bestCopyUnroll = 8;

        unsafe
        {
            byte* pSrc = null;
            byte* pDest = null;
            srcAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pSrc);
            destAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pDest);
            try
            {
                // Local function to pin thread
                IntPtr PinThread(int workerId, string affinityMode, int maxWorkers)
                {
                    int coreIndex = -1;
                    if (affinityMode == "p_cores")
                    {
                        coreIndex = (workerId % 6) * 2;
                    }
                    else if (affinityMode == "physical")
                    {
                        if (workerId < 6) coreIndex = workerId * 2;
                        else coreIndex = 12 + ((workerId - 6) % 4);
                    }
                    else if (affinityMode == "all")
                    {
                        coreIndex = workerId % 16;
                    }
                    
                    if (coreIndex >= 0)
                    {
                        return VelocityShare.Server.ThreadAffinityHelper.PinToCore(coreIndex);
                    }
                    return IntPtr.Zero;
                }

                // Local function to measure isolated read
                double MeasureRead(int workerCount, string affinityMode, string partitioningMode, int unroll)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (partitioningMode == "static")
                    {
                        Task[] tasks = new Task[workerCount];
                        long partSize = fileSize / workerCount;
                        partSize = (partSize / 4096) * 4096;
                        for (int t = 0; t < workerCount; t++)
                        {
                            int workerId = t;
                            long start = workerId * partSize;
                            long length = (workerId == workerCount - 1) ? (fileSize - start) : partSize;
                            tasks[workerId] = Task.Run(() =>
                            {
                                var prevAffinity = PinThread(workerId, affinityMode, workerCount);
                                try
                                {
                                    if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                                    {
                                        var sumVec0 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec1 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec2 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec3 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec4 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec5 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec6 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        var sumVec7 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                        
                                        long step = unroll == 8 ? 256 : 128;
                                        long limit = length - (length % step);
                                        byte* pSrcOffset = pSrc + start;
                                        
                                        if (unroll == 8)
                                        {
                                            for (long offset = 0; offset < limit; offset += step)
                                            {
                                                sumVec0 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                sumVec1 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                sumVec2 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                sumVec3 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                sumVec4 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 128));
                                                sumVec5 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 160));
                                                sumVec6 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 192));
                                                sumVec7 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 224));
                                            }
                                            var sumVec = sumVec0 ^ sumVec1 ^ sumVec2 ^ sumVec3 ^ sumVec4 ^ sumVec5 ^ sumVec6 ^ sumVec7;
                                            long sum = sumVec[0] ^ sumVec[1] ^ sumVec[2] ^ sumVec[3];
                                            for (long offset = limit; offset < length; offset += 8)
                                            {
                                                sum ^= *(long*)(pSrcOffset + offset);
                                            }
                                            if (sum == 0xDEADBEEF) GC.KeepAlive(sum);
                                        }
                                        else
                                        {
                                            for (long offset = 0; offset < limit; offset += step)
                                            {
                                                sumVec0 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                sumVec1 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                sumVec2 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                sumVec3 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                            }
                                            var sumVec = sumVec0 ^ sumVec1 ^ sumVec2 ^ sumVec3;
                                            long sum = sumVec[0] ^ sumVec[1] ^ sumVec[2] ^ sumVec[3];
                                            for (long offset = limit; offset < length; offset += 8)
                                            {
                                                sum ^= *(long*)(pSrcOffset + offset);
                                            }
                                            if (sum == 0xDEADBEEF) GC.KeepAlive(sum);
                                        }
                                    }
                                    else
                                    {
                                        long* pLong = (long*)(pSrc + start);
                                        long count = length / 8;
                                        long sum = 0;
                                        for (long i = 0; i < count; i++)
                                        {
                                            sum ^= pLong[i];
                                        }
                                        if (sum == 0xDEADBEEF) GC.KeepAlive(sum);
                                    }
                                }
                                finally
                                {
                                    VelocityShare.Server.ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                }
                            });
                        }
                        Task.WaitAll(tasks);
                    }
                    else // dynamic
                    {
                        long nextBlockIndex = 0;
                        long blockSize = 1024 * 1024; // 1MB blocks
                        Task[] tasks = new Task[workerCount];
                        for (int t = 0; t < workerCount; t++)
                        {
                            int workerId = t;
                            tasks[workerId] = Task.Run(() =>
                            {
                                var prevAffinity = PinThread(workerId, affinityMode, workerCount);
                                try
                                {
                                    while (true)
                                    {
                                        long blockIdx = Interlocked.Increment(ref nextBlockIndex) - 1;
                                        long start = blockIdx * blockSize;
                                        if (start >= fileSize) break;
                                        long length = Math.Min(blockSize, fileSize - start);
                                        
                                        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                                        {
                                            var sumVec0 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec1 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec2 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec3 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec4 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec5 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec6 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            var sumVec7 = System.Runtime.Intrinsics.Vector256<long>.Zero;
                                            
                                            long step = unroll == 8 ? 256 : 128;
                                            long limit = length - (length % step);
                                            byte* pSrcOffset = pSrc + start;
                                            
                                            if (unroll == 8)
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    sumVec0 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                    sumVec1 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                    sumVec2 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                    sumVec3 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                    sumVec4 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 128));
                                                    sumVec5 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 160));
                                                    sumVec6 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 192));
                                                    sumVec7 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 224));
                                                }
                                                var sumVec = sumVec0 ^ sumVec1 ^ sumVec2 ^ sumVec3 ^ sumVec4 ^ sumVec5 ^ sumVec6 ^ sumVec7;
                                                long sum = sumVec[0] ^ sumVec[1] ^ sumVec[2] ^ sumVec[3];
                                                for (long offset = limit; offset < length; offset += 8)
                                                {
                                                    sum ^= *(long*)(pSrcOffset + offset);
                                                }
                                                if (sum == 0xDEADBEEF) GC.KeepAlive(sum);
                                            }
                                            else
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    sumVec0 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                    sumVec1 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                    sumVec2 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                    sumVec3 ^= System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                }
                                                var sumVec = sumVec0 ^ sumVec1 ^ sumVec2 ^ sumVec3;
                                                long sum = sumVec[0] ^ sumVec[1] ^ sumVec[2] ^ sumVec[3];
                                                for (long offset = limit; offset < length; offset += 8)
                                                {
                                                    sum ^= *(long*)(pSrcOffset + offset);
                                                }
                                                if (sum == 0xDEADBEEF) GC.KeepAlive(sum);
                                            }
                                        }
                                        else
                                        {
                                            long* pLong = (long*)(pSrc + start);
                                            long count = length / 8;
                                            long sum = 0;
                                            for (long i = 0; i < count; i++)
                                            {
                                                sum ^= pLong[i];
                                            }
                                            if (sum == 0xDEADBEEF) GC.KeepAlive(sum);
                                        }
                                    }
                                }
                                finally
                                {
                                    VelocityShare.Server.ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                }
                            });
                        }
                        Task.WaitAll(tasks);
                    }
                    sw.Stop();
                    return sw.Elapsed.TotalSeconds;
                }

                // Local function to measure isolated write
                double MeasureWrite(int workerCount, string affinityMode, string partitioningMode, int unroll)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (partitioningMode == "static")
                    {
                        Task[] tasks = new Task[workerCount];
                        long partSize = fileSize / workerCount;
                        partSize = (partSize / 4096) * 4096;
                        for (int t = 0; t < workerCount; t++)
                        {
                            int workerId = t;
                            long start = workerId * partSize;
                            long length = (workerId == workerCount - 1) ? (fileSize - start) : partSize;
                            tasks[workerId] = Task.Run(() =>
                            {
                                var prevAffinity = PinThread(workerId, affinityMode, workerCount);
                                try
                                {
                                    if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                                    {
                                        var valVec = System.Runtime.Intrinsics.Vector256.Create(0x5555555555555555);
                                        long step = unroll == 8 ? 256 : 128;
                                        long limit = length - (length % step);
                                        byte* pDestOffset = pDest + start;
                                        
                                        if (unroll == 8)
                                        {
                                            for (long offset = 0; offset < limit; offset += step)
                                            {
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 128), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 160), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 192), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 224), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                            }
                                        }
                                        else
                                        {
                                            for (long offset = 0; offset < limit; offset += step)
                                            {
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                            }
                                        }
                                        for (long offset = limit; offset < length; offset += 8)
                                        {
                                            *(long*)(pDestOffset + offset) = 0x5555555555555555;
                                        }
                                    }
                                    else
                                    {
                                        long* pLong = (long*)(pDest + start);
                                        long count = length / 8;
                                        for (long i = 0; i < count; i++)
                                        {
                                            pLong[i] = 0x5555555555555555;
                                        }
                                    }
                                }
                                finally
                                {
                                    VelocityShare.Server.ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                }
                            });
                        }
                        Task.WaitAll(tasks);
                    }
                    else // dynamic
                    {
                        long nextBlockIndex = 0;
                        long blockSize = 1024 * 1024; // 1MB blocks
                        Task[] tasks = new Task[workerCount];
                        for (int t = 0; t < workerCount; t++)
                        {
                            int workerId = t;
                            tasks[workerId] = Task.Run(() =>
                            {
                                var prevAffinity = PinThread(workerId, affinityMode, workerCount);
                                try
                                {
                                    while (true)
                                    {
                                        long blockIdx = Interlocked.Increment(ref nextBlockIndex) - 1;
                                        long start = blockIdx * blockSize;
                                        if (start >= fileSize) break;
                                        long length = Math.Min(blockSize, fileSize - start);
                                        
                                        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                                        {
                                            var valVec = System.Runtime.Intrinsics.Vector256.Create(0x5555555555555555);
                                            long step = unroll == 8 ? 256 : 128;
                                            long limit = length - (length % step);
                                            byte* pDestOffset = pDest + start;
                                            
                                            if (unroll == 8)
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 128), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 160), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 192), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 224), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                }
                                            }
                                            else
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(valVec));
                                                }
                                            }
                                            for (long offset = limit; offset < length; offset += 8)
                                            {
                                                *(long*)(pDestOffset + offset) = 0x5555555555555555;
                                            }
                                        }
                                        else
                                        {
                                            long* pLong = (long*)(pDest + start);
                                            long count = length / 8;
                                            for (long i = 0; i < count; i++)
                                            {
                                                pLong[i] = 0x5555555555555555;
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    VelocityShare.Server.ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                }
                            });
                        }
                        Task.WaitAll(tasks);
                    }
                    sw.Stop();
                    return sw.Elapsed.TotalSeconds;
                }

                // Local function to measure combined copy
                double MeasureCopy(int workerCount, string affinityMode, string partitioningMode, int unroll)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (partitioningMode == "static")
                    {
                        Task[] tasks = new Task[workerCount];
                        long partSize = fileSize / workerCount;
                        partSize = (partSize / 4096) * 4096;
                        for (int t = 0; t < workerCount; t++)
                        {
                            int workerId = t;
                            long start = workerId * partSize;
                            long length = (workerId == workerCount - 1) ? (fileSize - start) : partSize;
                            tasks[workerId] = Task.Run(() =>
                            {
                                var prevAffinity = PinThread(workerId, affinityMode, workerCount);
                                try
                                {
                                    if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && length >= 256)
                                    {
                                        long step = unroll == 8 ? 256 : 128;
                                        long limit = length - (length % step);
                                        byte* pSrcOffset = pSrc + start;
                                        byte* pDestOffset = pDest + start;
                                        
                                        if (unroll == 8)
                                        {
                                            for (long offset = 0; offset < limit; offset += step)
                                            {
                                                var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                var temp4 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 128));
                                                var temp5 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 160));
                                                var temp6 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 192));
                                                var temp7 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 224));
                                                
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 128), System.Runtime.Intrinsics.Vector256.AsDouble(temp4));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 160), System.Runtime.Intrinsics.Vector256.AsDouble(temp5));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 192), System.Runtime.Intrinsics.Vector256.AsDouble(temp6));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 224), System.Runtime.Intrinsics.Vector256.AsDouble(temp7));
                                            }
                                        }
                                        else
                                        {
                                            for (long offset = 0; offset < limit; offset += step)
                                            {
                                                var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                            }
                                        }
                                        if (length > limit)
                                        {
                                            Buffer.MemoryCopy(pSrcOffset + limit, pDestOffset + limit, length - limit, length - limit);
                                        }
                                    }
                                    else
                                    {
                                        Buffer.MemoryCopy(pSrc + start, pDest + start, length, length);
                                    }
                                }
                                finally
                                {
                                    VelocityShare.Server.ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                }
                            });
                        }
                        Task.WaitAll(tasks);
                    }
                    else // dynamic
                    {
                        long nextBlockIndex = 0;
                        long blockSize = 1024 * 1024; // 1MB blocks
                        Task[] tasks = new Task[workerCount];
                        for (int t = 0; t < workerCount; t++)
                        {
                            int workerId = t;
                            tasks[workerId] = Task.Run(() =>
                            {
                                var prevAffinity = PinThread(workerId, affinityMode, workerCount);
                                try
                                {
                                    while (true)
                                    {
                                        long blockIdx = Interlocked.Increment(ref nextBlockIndex) - 1;
                                        long start = blockIdx * blockSize;
                                        if (start >= fileSize) break;
                                        long length = Math.Min(blockSize, fileSize - start);
                                        
                                        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && length >= 256)
                                        {
                                            long step = unroll == 8 ? 256 : 128;
                                            long limit = length - (length % step);
                                            byte* pSrcOffset = pSrc + start;
                                            byte* pDestOffset = pDest + start;
                                            
                                            if (unroll == 8)
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                    var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                    var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                    var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                    var temp4 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 128));
                                                    var temp5 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 160));
                                                    var temp6 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 192));
                                                    var temp7 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 224));
                                                    
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 128), System.Runtime.Intrinsics.Vector256.AsDouble(temp4));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 160), System.Runtime.Intrinsics.Vector256.AsDouble(temp5));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 192), System.Runtime.Intrinsics.Vector256.AsDouble(temp6));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 224), System.Runtime.Intrinsics.Vector256.AsDouble(temp7));
                                                }
                                            }
                                            else
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                    var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                    var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                    var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                    
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                }
                                            }
                                            if (length > limit)
                                            {
                                                Buffer.MemoryCopy(pSrcOffset + limit, pDestOffset + limit, length - limit, length - limit);
                                            }
                                        }
                                        else
                                        {
                                            Buffer.MemoryCopy(pSrc + start, pDest + start, length, length);
                                        }
                                    }
                                }
                                finally
                                {
                                    ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                }
                            });
                        }
                        Task.WaitAll(tasks);
                    }
                    sw.Stop();
                    return sw.Elapsed.TotalSeconds;
                }

                // Run the diagnostic sweep!
                int[] testWorkerCounts = { 2, 4, 6, 8, 10, 12, 16 };
                string[] testAffinityModes = { "no_pinning", "p_cores", "physical", "all" };
                string[] testPartitioningModes = { "static", "dynamic" };
                int[] testUnrolls = { 4, 8 };

                // Let's run a quick dry run to warm the JIT compiler!
                MeasureRead(6, "p_cores", "dynamic", 8);
                MeasureWrite(6, "p_cores", "dynamic", 8);
                MeasureCopy(6, "p_cores", "dynamic", 8);

                foreach (var workers in testWorkerCounts)
                {
                    foreach (var affinity in testAffinityModes)
                    {
                        foreach (var partitioning in testPartitioningModes)
                        {
                            foreach (var unroll in testUnrolls)
                            {
                                double readSec = MeasureRead(workers, affinity, partitioning, unroll);
                                double writeSec = MeasureWrite(workers, affinity, partitioning, unroll);
                                double copySec = MeasureCopy(workers, affinity, partitioning, unroll);

                                double readMBs = (fileSize / (1024.0 * 1024.0)) / readSec;
                                double writeMBs = (fileSize / (1024.0 * 1024.0)) / writeSec;
                                double copyMBs = (fileSize / (1024.0 * 1024.0)) / copySec;

                                double readGbps = (readMBs * 8.0) / 1024.0;
                                double writeGbps = (writeMBs * 8.0) / 1024.0;
                                double copyGbps = (copyMBs * 8.0) / 1024.0;

                                sweepResults.Add(new
                                {
                                    workers,
                                    affinity,
                                    partitioning,
                                    unroll,
                                    read_sec = readSec,
                                    read_gbps = readGbps,
                                    write_sec = writeSec,
                                    write_gbps = writeGbps,
                                    copy_sec = copySec,
                                    copy_gbps = copyGbps
                                });

                                if (readGbps > bestReadSpeed)
                                {
                                    bestReadSpeed = readGbps;
                                    bestReadWorkers = workers;
                                    bestReadAff = affinity;
                                    bestReadPart = partitioning;
                                    bestReadUnroll = unroll;
                                }
                                if (writeGbps > bestWriteSpeed)
                                {
                                    bestWriteSpeed = writeGbps;
                                    bestWriteWorkers = workers;
                                    bestWriteAff = affinity;
                                    bestWritePart = partitioning;
                                    bestWriteUnroll = unroll;
                                }
                                if (copyGbps > bestCopySpeed)
                                {
                                    bestCopySpeed = copyGbps;
                                    bestCopyWorkers = workers;
                                    bestCopyAff = affinity;
                                    bestCopyPart = partitioning;
                                    bestCopyUnroll = unroll;
                                }
                            }
                        }
                    }
                }

                // Apply the absolute optimal settings for subsequent production VctpSender copy loop!
                VctpSender.OptimalWorkerCount = bestCopyWorkers;
                VctpSender.OptimalAffinityMode = bestCopyAff;
                VctpSender.OptimalPartitioningMode = bestCopyPart;
                VctpSender.OptimalUnrollFactor = bestCopyUnroll;

                Console.WriteLine($"[SWEEP] OPTIMAL READ: {bestReadWorkers} workers, {bestReadAff}, {bestReadPart} partitioning, unroll {bestReadUnroll}x => {bestReadSpeed:F2} Gbps");
                Console.WriteLine($"[SWEEP] OPTIMAL WRITE: {bestWriteWorkers} workers, {bestWriteAff}, {bestWritePart} partitioning, unroll {bestWriteUnroll}x => {bestWriteSpeed:F2} Gbps");
                Console.WriteLine($"[SWEEP] OPTIMAL COPY: {bestCopyWorkers} workers, {bestCopyAff}, {bestCopyPart} partitioning, unroll {bestCopyUnroll}x => {bestCopySpeed:F2} Gbps");
            }
            finally
            {
                srcAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                destAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        // 3. Setup encryption keys
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(nonce);

        Guid fileId = Guid.NewGuid();
        var logs = new List<string>();

        // 4. Start VctpReceiver
        using var receiver = new VctpReceiver(destAccessor, fileSize, "", key, nonce, port: 0, bypassCrypto: true);
        receiver.OnLog += (log) => { logs.Add($"[Receiver] {log}"); Console.WriteLine($"[Receiver] {log}"); };

        var tcs = new TaskCompletionSource<bool>();
        string finalHash = "";
        receiver.OnTransferComplete += (path, hash) =>
        {
            finalHash = hash;
            tcs.TrySetResult(true);
        };

        // 5. Run VctpSender
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var sender = new VctpSender(srcAccessor, fileSize, fileId, srcHashHex, receiver, key, nonce, targetRateMbps: 100000.0, bypassCrypto: true))
        {
            receiver.LinkSender(sender);
            receiver.Start();

            sender.OnLog += (log) => { logs.Add($"[Sender] {log}"); Console.WriteLine($"[Sender] {log}"); };
            await sender.StartAsync();
            
            // Wait for completion with timeout
            await Task.WhenAny(tcs.Task, Task.Delay(15000));
        }
        sw.Stop();

        // 6. Verify in-memory destination data matches source
        bool memCheckPassed = true;
        unsafe
        {
            byte* pSrc = null;
            byte* pDest = null;
            srcAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pSrc);
            destAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pDest);
            try
            {
                long* pSrcLong = (long*)pSrc;
                long* pDestLong = (long*)pDest;
                long longCount = fileSize / 8;
                for (long i = 0; i < longCount; i++)
                {
                    if (pSrcLong[i] != pDestLong[i])
                    {
                        memCheckPassed = false;
                        break;
                    }
                }
            }
            finally
            {
                srcAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                destAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        if (!memCheckPassed || !tcs.Task.IsCompleted || tcs.Task.Result == false)
        {
            return Results.BadRequest(new
            {
                status = "FAILED",
                message = "In-memory sync verification check failed.",
                memCheckPassed,
                transferCompleted = tcs.Task.IsCompleted,
                logs
            });
        }

        double durationSec = sw.Elapsed.TotalSeconds;
        double throughputMB = (fileSize / (1024.0 * 1024.0)) / durationSec;
        double throughputGbps = (throughputMB * 8.0) / 1024.0;

        return Results.Ok(new
        {
            status = "PASS",
            message = "VCTP 100% In-Memory Sync Benchmark completed successfully.",
            payload_size_mb = fileSize / (1024.0 * 1024.0),
            duration_sec = durationSec,
            throughput_mbs = throughputMB,
            throughput_gbps = throughputGbps,
            optimal_copy = new
            {
                workers = bestCopyWorkers,
                affinity = bestCopyAff,
                partitioning = bestCopyPart,
                throughput_gbps = bestCopySpeed
            },
            optimal_read = new
            {
                workers = bestReadWorkers,
                affinity = bestReadAff,
                partitioning = bestReadPart,
                throughput_gbps = bestReadSpeed
            },
            optimal_write = new
            {
                workers = bestWriteWorkers,
                affinity = bestWriteAff,
                partitioning = bestWritePart,
                throughput_gbps = bestWriteSpeed
            },
            sweep_results = sweepResults,
            comparisons = new
            {
                standard_sftp_https = new { typical_max_speed_mbs = 250.0, speedup_x = throughputMB / 250.0 },
                aspera_fasp_wan = new { typical_max_speed_mbs = 75.0, speedup_x = throughputMB / 75.0 },
                webrtc_sctp_browser = new { typical_max_speed_mbs = 37.5, speedup_x = throughputMB / 37.5 }
            },
            logs
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Benchmark run failed: {ex.Message}\n{ex.StackTrace}");
    }
});

app.MapGet("/", async (HttpContext context) =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/index.html");
});

app.Run();

// Helper method to broadcast currently connected peer lists to all active sockets using NDA binary
static async Task BroadcastPeerListAsync(ConcurrentDictionary<string, WebSocket> activePeers)
{
    byte[] payload = Protocol.NdaSignaling.CreatePeerList(activePeers.Keys);

    foreach (var peer in activePeers)
    {
        if (peer.Value.State == WebSocketState.Open)
        {
            try
            {
                await peer.Value.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch
            {
                // Ignore send errors during disconnect broadcasts
            }
        }
    }
}
