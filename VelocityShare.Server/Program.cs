using System;
using System.Collections.Concurrent;
using System.IO;
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
                    string target = msgDoc.RootElement.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? "" : "";
                    
                    if (!string.IsNullOrEmpty(target) && activePeers.TryGetValue(target, out var targetSocket))
                    {
                        // Forward signaling packet directly to recipient peer
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
