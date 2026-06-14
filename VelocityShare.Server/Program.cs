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

// POST /api/share/sync/start: Starts the directory sync engine watching a local folder path
app.MapPost("/api/share/sync/start", async (HttpContext context) =>
{
    string path = context.Request.Query["path"].ToString();
    string targetPeerId = context.Request.Query["targetPeerId"].ToString();

    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(targetPeerId))
    {
        return Results.BadRequest("Missing path or targetPeerId parameters.");
    }

    if (!Directory.Exists(path))
    {
        Directory.CreateDirectory(path);
    }

    if (activeSyncEngine != null)
    {
        activeSyncEngine.Stop();
        activeSyncEngine.Dispose();
        activeSyncEngine = null;
    }

    activeSyncEngine = new FileSyncEngine(path, async (syncEventJson) =>
    {
        var envelope = JsonSerializer.Serialize(new
        {
            type = "folder_sync_payload",
            sender = "local_sync_engine",
            target = targetPeerId,
            data = syncEventJson
        });
        byte[] payload = Encoding.UTF8.GetBytes(envelope);

        // 1. Dispatch directly to target socket if it exists on this server instance (same-server peer connection)
        if (activePeers.TryGetValue(targetPeerId, out var targetSocket) && targetSocket.State == WebSocketState.Open)
        {
            await targetSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
            Console.WriteLine($"[Sync Engine] Dispatched sync payload directly to peer {targetPeerId}");
        }

        // 2. Dispatch to all other active sockets connected to this server instance (local browser tab(s))
        foreach (var peer in activePeers)
        {
            if (peer.Key != targetPeerId && peer.Value.State == WebSocketState.Open)
            {
                await peer.Value.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
                Console.WriteLine($"[Sync Engine] Dispatched sync payload to local peer {peer.Key} for forwarding");
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
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string rawMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                try
                {
                    var msgDoc = JsonDocument.Parse(rawMsg);
                    string msgType = msgDoc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    
                    if (msgType == "folder_sync_payload" && activeSyncEngine != null)
                    {
                        string innerData = msgDoc.RootElement.GetProperty("data").GetString() ?? "";
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

                        // Apply the remote change locally
                        await activeSyncEngine.ApplyRemoteSyncAsync(syncType, file, hash, contentBytes);
                        Console.WriteLine($"[Sync Engine] Applied remote {syncType} for file {file}");
                    }

                    string target = msgDoc.RootElement.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(target) && activePeers.TryGetValue(target, out var targetSocket))
                    {
                        // Forward signaling/sync packet directly to recipient peer
                        if (targetSocket.State == WebSocketState.Open)
                        {
                            await targetSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Invalid JSON, ignore packet
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

        if (type == "local_nas" && !Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        dropsiteConfig["type"] = type;
        dropsiteConfig["path"] = path;
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
        using (var sender = new VctpSender(srcPath, fileId, srcHashHex, remoteEP, key, nonce, targetRateMbps: 10000.0))
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
        using (var senderResume = new VctpSender(srcPath, fileId, srcHashHex, remoteEP, key, nonce, targetRateMbps: 10000.0))
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

app.MapGet("/", async (HttpContext context) =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/index.html");
});

app.Run();

// Helper method to broadcast currently connected peer lists to all active sockets
static async Task BroadcastPeerListAsync(ConcurrentDictionary<string, WebSocket> activePeers)
{
    var listMsg = JsonSerializer.Serialize(new
    {
        type = "peer_list",
        peers = activePeers.Keys
    });
    byte[] payload = Encoding.UTF8.GetBytes(listMsg);

    foreach (var peer in activePeers)
    {
        if (peer.Value.State == WebSocketState.Open)
        {
            try
            {
                await peer.Value.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // Ignore send errors during disconnect broadcasts
            }
        }
    }
}
