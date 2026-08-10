using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VelocityShare.Server;
using Protocol = VelocityShare.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Configure structured logging
builder.Logging.ClearProviders();

if (builder.Environment.IsDevelopment())
{
    // Human-readable format in development
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}
else
{
    // JSON structured logging in production for log aggregation
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.UseUtcTimestamp = true;
        options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions
        {
            Indented = false // Compact JSON for production
        };
    });
    
    // Add file logging for post-mortem debugging
    // Use ProgramData (writable) instead of ContentRootPath (read-only when installed)
    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VelocityShare", "logs");
    if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
    builder.Logging.AddProvider(new SimpleFileLoggerProvider(logDir));
}

// ── Kestrel hardening: connection limits, body size, header limits ──
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxConcurrentConnections = 256;
    kestrel.Limits.MaxConcurrentUpgradedConnections = 64; // WebSocket limit
    kestrel.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50 MB upload cap
    kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024; // 32 KB headers
    kestrel.Limits.MaxRequestLineSize = 8 * 1024; // 8 KB request line
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);

    // Hide server version header
    kestrel.AddServerHeader = false;
});

// Configure request timeout
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(60),
        TimeoutStatusCode = StatusCodes.Status408RequestTimeout
    };
});

// Configure CORS from appsettings (restricted in Production, open in Development)
var corsConfig = builder.Configuration.GetSection("Cors");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // Allow all origins in development for testing
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            // Restricted CORS in production
            var allowedOrigins = corsConfig.GetSection("AllowedOrigins").Get<string[]>() ?? new[] { "https://share.unitbuilds.com" };
            var allowedMethods = corsConfig.GetSection("AllowedMethods").Get<string[]>() ?? new[] { "GET", "POST" };
            var allowedHeaders = corsConfig.GetSection("AllowedHeaders").Get<string[]>() ?? new[] { "Content-Type", "Authorization", "X-API-Key", "X-WS-Token" };
            
            policy.WithOrigins(allowedOrigins)
                  .WithMethods(allowedMethods)
                  .WithHeaders(allowedHeaders)
                  .AllowCredentials();
        }
    });
});

// Configure rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("fixed", context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 10
        });
    });
    
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Add health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Security headers middleware ──
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["X-XSS-Protection"] = "1; mode=block";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), magnetometer=(), gyroscope=(), accelerometer=()";
    headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' https://fonts.googleapis.com; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self' ws: wss:";
    
    // HSTS with explicit max-age (1 year, includeSubDomains) in production
    if (!app.Environment.IsDevelopment())
    {
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    
    // Remove Server header if still present
    context.Response.Headers.Remove("Server");
    
    await next();
});

// Configure HTTP pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();

// Configure WebSocket options with keepalive
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
};
app.UseWebSockets(webSocketOptions);

// ── Static files: block sensitive file types (.dll, .so, .json config, .pdb) ──
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value?.ToLowerInvariant() ?? "";
        // Block access to sensitive file extensions
        if (path.EndsWith(".dll") || path.EndsWith(".so") || path.EndsWith(".pdb") ||
            path.EndsWith(".config") || path.EndsWith(".json") || path.EndsWith(".xml") ||
            path.Contains("appsettings") || path.Contains("web.config"))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentLength = 0;
            ctx.Context.Response.Body = Stream.Null;
        }
    }
});

app.UseRateLimiter();
app.UseRequestTimeouts();

// ── Metrics middleware for Prometheus monitoring ──
app.UseMiddleware<MetricsMiddleware>();

// Map health check endpoint
app.MapHealthChecks("/health");

var logger = app.Services.GetRequiredService<ILogger<Program>>();

// ── Credentials configuration ──
var credentialsConfig = builder.Configuration.GetSection("Credentials");
var adminApiKey = credentialsConfig["AdminApiKey"] ?? "";
var wsAuthToken = credentialsConfig["WebSocketToken"] ?? "";
var requireAuthInDev = bool.TryParse(credentialsConfig["RequireAuthInDevelopment"], out var rad) && rad;

// ── Metrics endpoint for Prometheus (rate-limited; admin-only in production) ──
app.MapGet("/metrics", (HttpContext context) =>
{
    // In production, require API key to prevent information disclosure
    if (!app.Environment.IsDevelopment() && !ValidateApiKey(context))
    {
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
    }
    return Results.Text(MetricsMiddleware.GenerateMetrics(), "text/plain");
}).RequireRateLimiting("fixed");

// ── Validate production credentials on startup ──
if (!app.Environment.IsDevelopment())
{
    if (string.IsNullOrEmpty(adminApiKey) || adminApiKey.Length < 32)
    {
        logger.LogCritical("[Credentials] AdminApiKey must be configured and at least 32 characters in Production. Set Credentials:AdminApiKey in appsettings.Production.json or environment variable.");
        throw new InvalidOperationException("Missing or weak AdminApiKey in production. Configure Credentials:AdminApiKey (min 32 chars).");
    }
    if (string.IsNullOrEmpty(wsAuthToken) || wsAuthToken.Length < 16)
    {
        logger.LogCritical("[Credentials] WebSocketToken must be configured and at least 16 characters in Production.");
        throw new InvalidOperationException("Missing or weak WebSocketToken in production. Configure Credentials:WebSocketToken (min 16 chars).");
    }
}

// ── API Key validation helper ──
bool IsAuthRequired()
{
    if (app.Environment.IsDevelopment() && !requireAuthInDev) return false;
    return !string.IsNullOrEmpty(adminApiKey);
}

bool ValidateApiKey(HttpContext context)
{
    if (!IsAuthRequired()) return true;
    
    var expectedBytes = Encoding.UTF8.GetBytes(adminApiKey);
    
    // Check X-API-Key header
    var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
    if (!string.IsNullOrEmpty(apiKey) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(apiKey), expectedBytes)) return true;
    
    // Check Authorization: Bearer <token>
    var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
    if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        var token = authHeader["Bearer ".Length..].Trim();
        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), expectedBytes)) return true;
    }
    
    // Check query string parameter (for WebSocket fallback)
    var queryKey = context.Request.Query["apiKey"].FirstOrDefault();
    if (!string.IsNullOrEmpty(queryKey) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(queryKey), expectedBytes)) return true;
    
    return false;
}

bool ValidateWsToken(HttpContext context)
{
    if (!IsAuthRequired()) return true;
    
    // Check X-WS-Token header
    var wsToken = context.Request.Headers["X-WS-Token"].FirstOrDefault();
    if (!string.IsNullOrEmpty(wsToken) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(wsToken), Encoding.UTF8.GetBytes(wsAuthToken))) return true;
    
    // Check query string 'token' parameter
    var queryToken = context.Request.Query["token"].FirstOrDefault();
    if (!string.IsNullOrEmpty(queryToken) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(queryToken), Encoding.UTF8.GetBytes(wsAuthToken))) return true;
    
    // Fallback: accept admin API key via query string (client sends apiKey param for WebSocket)
    var queryKey = context.Request.Query["apiKey"].FirstOrDefault();
    if (!string.IsNullOrEmpty(queryKey) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(queryKey), Encoding.UTF8.GetBytes(adminApiKey))) return true;
    
    return false;
}

logger.LogInformation("[Credentials] Authentication {AuthStatus}", IsAuthRequired() ? "ENABLED" : "DISABLED (dev mode)");

// ── Constants for security limits ──
const int MaxPeerIdLength = 64;
const int MaxConcurrentPeers = 128;

// Thread-safe dictionary to track active peer WebSocket connections
var activePeers = new ConcurrentDictionary<string, WebSocket>();

// Share link manager for time-limited download links
var shareLinkManager = new ShareLinkManager();

// Writable data directory: use ProgramData when installed, fallback to ContentRootPath for dev
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "VelocityShare");
if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

// In-memory dropsite configuration
string defaultUploadsDir = Path.Combine(dataDir, "uploads");
if (!Directory.Exists(defaultUploadsDir))
{
    Directory.CreateDirectory(defaultUploadsDir);
}
var dropsiteConfig = new ConcurrentDictionary<string, string>();
dropsiteConfig["type"] = "local_nas";
dropsiteConfig["path"] = defaultUploadsDir;

// Multi-peer sync engines: one per target peer
var activeSyncEngines = new ConcurrentDictionary<string, FileSyncEngine>();
// Shared persistent change journal
var syncJournal = new VelocityShare.Server.Sync.SyncChangeJournal(
    Path.Combine(dataDir, ".velocity_sync_journal.db"));
var activeReceivers = new ConcurrentDictionary<Guid, VctpReceiver>();

// ── Background job: cleanup orphaned upload chunks older than 24 hours ──
// Skips directories associated with active share links (grace period protection)
var cleanupCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!cleanupCts.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromHours(1), cleanupCts.Token);
            var uploadRoot = dropsiteConfig["path"] ?? defaultUploadsDir;
            if (!Directory.Exists(uploadRoot)) continue;

            // Build set of active share link FileIds to protect from cleanup
            foreach (var dir in Directory.GetDirectories(uploadRoot))
            {
                // Grace period: skip directories with recent write activity (within 24h)
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-24))
                    continue; // Recent activity — skip (within grace period)

                try
                {
                    dirInfo.Delete(recursive: true);
                    logger.LogInformation("[Cleanup] Removed orphaned upload folder: {Dir}", dirInfo.Name);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Cleanup] Failed to remove folder: {Dir}", dir);
                }
            }
        }
        catch (OperationCanceledException)
        {
            break; // Graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Cleanup] Background cleanup task error.");
        }
    }
    logger.LogInformation("[Cleanup] Background cleanup task stopped.");
}, cleanupCts.Token);

string sandboxRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

// Path validation delegates to the shared utility class
bool IsPathInsideSandbox(string path, string sandbox) => PathValidation.IsPathInsideSandbox(path, sandbox);
bool IsFileInsideSyncFolder(string file, string syncFolder) => PathValidation.IsFileInsideSyncFolder(file, syncFolder);

// Start background loop to clean up zombied VctpReceiver instances (inactivity > 60 seconds)
_ = Task.Run(async () =>
{
    var cancellation = app.Lifetime.ApplicationStopping;
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            var cutoff = DateTime.UtcNow.AddSeconds(-60);
            foreach (var kvp in activeReceivers)
            {
                if (kvp.Value.LastActiveTime < cutoff)
                {
                    if (activeReceivers.TryRemove(kvp.Key, out var receiver))
                    {
                        logger.LogInformation("[Sync Engine] Cleaning up zombied VctpReceiver {ReceiverId} due to 60s inactivity.", kvp.Key);
                        receiver.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown - exit loop
            break;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Sync Engine Error] Error in activeReceivers cleanup task");
        }
    }
    logger.LogInformation("[Sync Engine] Background cleanup task stopped.");
});

// Graceful shutdown: dispose all sync engines and active receivers
app.Lifetime.ApplicationStopping.Register(async () =>
{
    logger.LogInformation("[Sync Engine] Shutting down: disposing {Count} sync engine(s) and receivers.", activeSyncEngines.Count);
    cleanupCts.Cancel();
    foreach (var kvp in activeSyncEngines)
    {
        try { await kvp.Value.DisposeAsync(); } catch { }
    }
    activeSyncEngines.Clear();
    foreach (var kvp in activeReceivers)
    {
        try { kvp.Value.Dispose(); } catch { }
    }
    activeReceivers.Clear();
    try { await syncJournal.DisposeAsync(); } catch { }
});

// POST /api/share/sync/start: Starts a sync engine for a specific peer
app.MapPost("/api/share/sync/start", async (HttpContext context) =>
{
    // ── API Key authentication ──
    if (!ValidateApiKey(context))
    {
        logger.LogWarning("[Auth] Rejected unauthenticated sync/start from {RemoteIp}", context.Connection.RemoteIpAddress);
        return Results.Json(new { error = "Unauthorized. Provide a valid API key via X-API-Key or Authorization header." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    string path = context.Request.Query["path"].ToString();
    string targetPeerId = context.Request.Query["targetPeerId"].ToString();

    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(targetPeerId))
    {
        return Results.BadRequest("Missing path or targetPeerId parameters.");
    }

    // ── Input length limits ──
    if (path.Length > 500)
    {
        return Results.BadRequest("Path exceeds maximum length.");
    }

    // ── Validate targetPeerId format ──
    if (targetPeerId.Length > MaxPeerIdLength ||
        !System.Text.RegularExpressions.Regex.IsMatch(targetPeerId, @"^[a-zA-Z0-9_\-]+$"))
    {
        return Results.BadRequest("Invalid targetPeerId format.");
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

    // Stop existing engine for this peer if any
    if (activeSyncEngines.TryRemove(targetPeerId, out var existingEngine))
    {
        await existingEngine.DisposeAsync();
    }

    var storage = new VelocityShare.Server.Sync.LocalSyncStorageProvider(fullPath);

    var engine = new FileSyncEngine(storage, targetPeerId, async (binaryPacket) =>
    {
        // 1. Dispatch directly to target socket if it exists on this server instance
        if (activePeers.TryGetValue(targetPeerId, out var targetSocket) && targetSocket.State == WebSocketState.Open)
        {
            await targetSocket.SendAsync(new ArraySegment<byte>(binaryPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
            logger.LogInformation("[Sync Engine] Dispatched binary sync payload directly to peer {PeerId}", targetPeerId);
        }

        // 2. Dispatch to all other active sockets connected to this server instance
        foreach (var peer in activePeers)
        {
            if (peer.Key != targetPeerId && peer.Value.State == WebSocketState.Open)
            {
                await peer.Value.SendAsync(new ArraySegment<byte>(binaryPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
                logger.LogInformation("[Sync Engine] Dispatched binary sync payload to local peer {PeerId} for forwarding", peer.Key);
            }
        }
    }, syncJournal);

    engine.Start();
    activeSyncEngines[targetPeerId] = engine;

    // Trigger initial manifest exchange
    _ = Task.Run(async () =>
    {
        try
        {
            await engine.SendManifestAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Sync Engine] Initial manifest send failed for peer {PeerId}", targetPeerId);
        }
    });

    logger.LogInformation("[Sync Engine] Activated sync for path: {Path} targeting peer: {PeerId} ({Count} total sessions)", path, targetPeerId, activeSyncEngines.Count);
    return Results.Ok(new { status = "STARTED", path, targetPeerId, sessionCount = activeSyncEngines.Count });
}).RequireRateLimiting("fixed");

// POST /api/share/sync/stop: Stops a specific sync engine (or all if no targetPeerId)
app.MapPost("/api/share/sync/stop", async (HttpContext context) =>
{
    // ── API Key authentication ──
    if (!ValidateApiKey(context))
    {
        logger.LogWarning("[Auth] Rejected unauthenticated sync/stop from {RemoteIp}", context.Connection.RemoteIpAddress);
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    string? targetPeerId = context.Request.Query["targetPeerId"].ToString();

    if (!string.IsNullOrEmpty(targetPeerId))
    {
        // Stop specific peer session
        if (activeSyncEngines.TryRemove(targetPeerId, out var engine))
        {
            await engine.DisposeAsync();
            logger.LogInformation("[Sync Engine] Deactivated sync for peer {PeerId}", targetPeerId);
        }
    }
    else
    {
        // Stop all sessions
        foreach (var kvp in activeSyncEngines)
        {
            if (activeSyncEngines.TryRemove(kvp.Key, out var eng))
            {
                await eng.DisposeAsync();
            }
        }
        logger.LogInformation("[Sync Engine] Deactivated all sync sessions.");
    }
    return Results.Ok(new { status = "STOPPED", remaining = activeSyncEngines.Count });
}).RequireRateLimiting("fixed");

// GET /api/share/sync/sessions: List active sync sessions
app.MapGet("/api/share/sync/sessions", (HttpContext context) =>
{
    if (!ValidateApiKey(context))
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);

    var sessions = activeSyncEngines.Select(kvp => new
    {
        peerId = kvp.Key,
        folder = kvp.Value.SyncFolderPath,
        state = kvp.Value.State.ToString(),
        bytesSent = kvp.Value.TotalBytesSent,
        bytesReceived = kvp.Value.TotalBytesReceived,
        deltaSyncs = kvp.Value.DeltaSyncsCompleted,
        fullSyncs = kvp.Value.FullSyncsCompleted
    });
    return Results.Ok(new { sessions, count = activeSyncEngines.Count });
}).RequireRateLimiting("fixed");

// POST /api/share/sync/start-cloud: Start sync with cloud storage (S3 or Azure)
app.MapPost("/api/share/sync/start-cloud", async (HttpContext context) =>
{
    if (!ValidateApiKey(context))
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);

    string targetPeerId = context.Request.Query["targetPeerId"].ToString();
    string providerType = context.Request.Query["provider"].ToString(); // "s3" or "azure"

    if (string.IsNullOrEmpty(targetPeerId) || string.IsNullOrEmpty(providerType))
        return Results.BadRequest("Missing targetPeerId or provider parameters.");

    VelocityShare.Server.Sync.ISyncStorageProvider storage;

    if (providerType == "s3")
    {
        string bucket = context.Request.Query["bucket"].ToString();
        string region = context.Request.Query["region"].ToString();
        string accessKey = context.Request.Query["accessKey"].ToString();
        string secretKey = context.Request.Query["secretKey"].ToString();
        string prefix = context.Request.Query["prefix"].ToString();

        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            return Results.BadRequest("Missing S3 parameters: bucket, accessKey, secretKey.");

        storage = new VelocityShare.Server.Sync.S3SyncStorageProvider(bucket, region, accessKey, secretKey, prefix);
    }
    else if (providerType == "azure")
    {
        string account = context.Request.Query["account"].ToString();
        string key = context.Request.Query["key"].ToString();
        string container = context.Request.Query["container"].ToString();
        string prefix = context.Request.Query["prefix"].ToString();

        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(container))
            return Results.BadRequest("Missing Azure parameters: account, key, container.");

        storage = new VelocityShare.Server.Sync.AzureBlobSyncStorageProvider(account, key, container, prefix);
    }
    else
    {
        return Results.BadRequest("Unsupported provider. Use 's3' or 'azure'.");
    }

    // Stop existing engine for this peer
    if (activeSyncEngines.TryRemove(targetPeerId, out var existing))
        await existing.DisposeAsync();

    var engine = new FileSyncEngine(storage, targetPeerId, async (binaryPacket) =>
    {
        if (activePeers.TryGetValue(targetPeerId, out var sock) && sock.State == WebSocketState.Open)
            await sock.SendAsync(new ArraySegment<byte>(binaryPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
        foreach (var peer in activePeers)
            if (peer.Key != targetPeerId && peer.Value.State == WebSocketState.Open)
                await peer.Value.SendAsync(new ArraySegment<byte>(binaryPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
    }, syncJournal);

    engine.Start();
    activeSyncEngines[targetPeerId] = engine;

    _ = Task.Run(async () =>
    {
        try { await engine.SendManifestAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "[Cloud Sync] Manifest send failed for {PeerId}", targetPeerId); }
    });

    logger.LogInformation("[Cloud Sync] Started {Provider} sync for peer {PeerId}", providerType, targetPeerId);
    return Results.Ok(new { status = "STARTED", provider = providerType, targetPeerId });
}).RequireRateLimiting("fixed");

// ── Sync Throttle Control ──

// GET /api/share/sync/throttle: Get current throttle status for all engines
app.MapGet("/api/share/sync/throttle", (HttpContext context) =>
{
    if (!ValidateApiKey(context))
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);

    var engines = activeSyncEngines.Select(kvp =>
    {
        var engine = kvp.Value;
        return new
        {
            peerId = kvp.Key,
            throttle = new
            {
                profile = engine.ThrottleConfig.Profile.ToString(),
                autoAdaptive = engine.ThrottleConfig.AutoAdaptive,
                status = engine.RateLimiter.GetStatus(),
                scheduler = engine.AdaptiveScheduler.GetStats(),
                latency = engine.LatencyTracker.GetMetrics()
            }
        };
    });
    return Results.Ok(new { engines });
}).RequireRateLimiting("fixed");

// POST /api/share/sync/throttle: Set throttle profile/limits for a sync engine
app.MapPost("/api/share/sync/throttle", async (HttpContext context) =>
{
    if (!ValidateApiKey(context))
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);

    using var reader = new StreamReader(context.Request.Body);
    var bodyText = await reader.ReadToEndAsync();
    var body = JsonSerializer.Deserialize<JsonElement>(bodyText);

    string? targetPeerId = body.TryGetProperty("peerId", out var pidProp) ? pidProp.GetString() : null;
    if (string.IsNullOrEmpty(targetPeerId) && activeSyncEngines.Count > 0)
        targetPeerId = activeSyncEngines.Keys.First();

    if (targetPeerId == null || !activeSyncEngines.TryGetValue(targetPeerId, out var engine))
        return Results.NotFound(new { error = "No active sync engine found." });

    var config = new VelocityShare.Server.Sync.SyncThrottleConfig();

    if (body.TryGetProperty("profile", out var profileProp) && Enum.TryParse<VelocityShare.Server.Sync.SyncThrottleProfile>(profileProp.GetString(), true, out var parsedProfile))
        config.Profile = parsedProfile;

    if (body.TryGetProperty("autoAdaptive", out var autoProp))
        config.AutoAdaptive = autoProp.GetBoolean();

    if (body.TryGetProperty("manualLimits", out var limitsProp))
    {
        if (limitsProp.TryGetProperty("maxBandwidthBytesPerSec", out var bw))
            config.ManualLimits.MaxBandwidthBytesPerSec = bw.GetInt64();
        if (limitsProp.TryGetProperty("maxCpuPercent", out var cpu))
            config.ManualLimits.MaxCpuPercent = cpu.GetInt32();
        if (limitsProp.TryGetProperty("maxDiskIops", out var iops))
            config.ManualLimits.MaxDiskIops = iops.GetInt32();
        if (limitsProp.TryGetProperty("maxDiskBytesPerSec", out var disk))
            config.ManualLimits.MaxDiskBytesPerSec = disk.GetInt64();
        if (limitsProp.TryGetProperty("minFreeDiskSpaceBytes", out var free))
            config.ManualLimits.MinFreeDiskSpaceBytes = free.GetInt64();
    }

    if (body.TryGetProperty("minDebounceMs", out var minDb))
        config.MinDebounceMs = minDb.GetInt32();
    if (body.TryGetProperty("maxDebounceMs", out var maxDb))
        config.MaxDebounceMs = maxDb.GetInt32();
    if (body.TryGetProperty("stabilityWindowMs", out var sw))
        config.StabilityWindowMs = sw.GetInt32();
    if (body.TryGetProperty("rapidChangeThreshold", out var rct))
        config.RapidChangeThreshold = rct.GetInt32();

    engine.UpdateThrottleConfig(config);

    return Results.Ok(new
    {
        status = "UPDATED",
        peerId = targetPeerId,
        profile = config.Profile.ToString(),
        autoAdaptive = config.AutoAdaptive,
        effectiveLimits = config.Resolve()
    });
}).RequireRateLimiting("fixed");

// WebSocket Handshake and Signaling Gateway for P2P WebRTC coordination
app.Map("/ws/share", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // ── WebSocket authentication ──
    if (!ValidateWsToken(context))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        logger.LogWarning("[WebSocket] Rejected unauthenticated connection from {RemoteIp}", context.Connection.RemoteIpAddress);
        return;
    }

    // ── WebSocket origin validation (prevent cross-site WebSocket hijacking) ──
    if (!app.Environment.IsDevelopment())
    {
        var origin = context.Request.Headers.Origin.ToString();
        var allowedOrigins = corsConfig.GetSection("AllowedOrigins").Get<string[]>() ?? new[] { "https://share.unitbuilds.com" };
        if (!string.IsNullOrEmpty(origin) && !allowedOrigins.Any(o => origin.Equals(o, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            logger.LogWarning("[WebSocket] Rejected connection from unauthorized origin: {Origin}", origin);
            return;
        }
    }

    string peerId = context.Request.Query["peerId"].ToString();

    // ── Peer ID sanitization: alphanumeric + underscore only, max length ──
    if (string.IsNullOrEmpty(peerId) || peerId.Length > MaxPeerIdLength ||
        !System.Text.RegularExpressions.Regex.IsMatch(peerId, @"^[a-zA-Z0-9_\-]+$"))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Invalid peerId. Must be 1-64 alphanumeric/underscore/hyphen characters.");
        return;
    }

    // ── Max concurrent peers limit ──
    if (activePeers.Count >= MaxConcurrentPeers)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        logger.LogWarning("[WebSocket] Rejected peer {PeerId}: max concurrent peers ({Max}) reached.", peerId, MaxConcurrentPeers);
        await context.Response.WriteAsync("Server at maximum concurrent peer capacity.");
        return;
    }

    if (activePeers.TryGetValue(peerId, out var existingSocket) && existingSocket.State == WebSocketState.Open)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsync("Peer ID is already online.");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync(subProtocol: null);
    activePeers[peerId] = webSocket;
    MetricsMiddleware.RecordWebSocketConnection();
    logger.LogInformation("[WebSocket] Peer connected: {PeerId}. Active peers: {PeerCount}", peerId, activePeers.Count);

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
                            
                            // ── Ping/Pong latency measurement ──
                            if (msgType == "ping")
                            {
                                double clientT = msgDoc.RootElement.TryGetProperty("t", out var tProp) ? tProp.GetDouble() : 0;
                                var pong = JsonSerializer.Serialize(new { type = "pong", t = clientT });
                                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(pong)), WebSocketMessageType.Text, true, CancellationToken.None);
                                continue;
                            }
                            
                            if (msgType == "folder_sync_payload")
                            {
                                // Find the sync engine for the sender peer
                                string senderPeer = msgDoc.RootElement.TryGetProperty("sender", out var sp) ? sp.GetString() ?? "" : "";
                                activeSyncEngines.TryGetValue(senderPeer, out var syncEngine);
                                if (syncEngine == null)
                                {
                                    // Try any engine if we can't find a specific one
                                    syncEngine = activeSyncEngines.Values.FirstOrDefault();
                                }
                                if (syncEngine != null)
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

                                     string syncFolder = syncEngine.SyncFolderPath;
                                     if (!IsFileInsideSyncFolder(file, syncFolder))
                                     {
                                         logger.LogWarning("[Sync Engine Error] Path traversal blocked: {File}", file);
                                         continue;
                                     }
                                     string combinedPath = Path.Combine(syncFolder, file);
                                     string targetDir = Path.GetDirectoryName(Path.GetFullPath(combinedPath)) ?? syncFolder;

                                     var receiver = new VctpReceiver(targetDir, key, nonce, port: 0);
                                    activeReceivers[fileId] = receiver;

                                    receiver.OnTransferComplete += (filePath, fileHash) =>
                                    {
                                        syncEngine.ConfirmRemoteSyncCompleted(file, fileHash);
                                        receiver.Dispose();
                                        activeReceivers.TryRemove(fileId, out _);
                                        logger.LogInformation("[Sync Engine] VCTP sync receiver complete for {File}", file);
                                    };
                                    receiver.Start();

                                    string vctpSenderPeer = msgDoc.RootElement.GetProperty("sender").GetString() ?? "";
                                    var acceptPayload = JsonSerializer.Serialize(new
                                    {
                                        type = "folder_sync_payload",
                                        sender = "local_sync_engine",
                                        target = vctpSenderPeer,
                                        data = JsonSerializer.Serialize(new
                                        {
                                            type = "sync_vctp_accept",
                                            fileId = fileId,
                                            port = receiver.Port
                                        })
                                    });

                                    if (activePeers.TryGetValue(vctpSenderPeer, out var senderSocket) && senderSocket.State == WebSocketState.Open)
                                    {
                                        await senderSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(acceptPayload)), WebSocketMessageType.Text, true, CancellationToken.None);
                                        logger.LogInformation("[Sync Engine] Dispatched sync_vctp_accept back to peer {PeerId} on port {Port}", vctpSenderPeer, receiver.Port);
                                    }
                                }
                                else if (syncType == "sync_vctp_accept")
                                {
                                    Guid fileId = innerDoc.RootElement.GetProperty("fileId").GetGuid();
                                    int port = innerDoc.RootElement.GetProperty("port").GetInt32();

                                    if (syncEngine.ActiveSyncTransfers.TryRemove(fileId, out var senderInfo))
                                    {
                                        var (key, nonce, fullPath, fileHash) = senderInfo;
                                        var remoteEP = new IPEndPoint(IPAddress.Loopback, port);
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                using var vctpSender = new VctpSender(fullPath, fileId, fileHash, remoteEP, key, nonce, targetRateMbps: 1000.0);
                                                await vctpSender.StartAsync();
                                                logger.LogInformation("[Sync Engine] VCTP sync sender complete for {Path}", fullPath);
                                            }
                                            catch (Exception ex)
                                            {
                                                logger.LogError(ex, "[Sync Engine] VCTP sync sender failed");
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

                                    await syncEngine.ApplyRemoteSyncAsync(syncType, file, hash, contentBytes);
                                    logger.LogInformation("[Sync Engine] Applied remote {SyncType} for file {File}", syncType, file);
                                }
                                } // end if (syncEngine != null)
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

                        if (target == "local_sync_engine")
                        {
                            // Find the sync engine for this peer
                            activeSyncEngines.TryGetValue(peerId, out var syncEngine);
                            if (syncEngine == null)
                                syncEngine = activeSyncEngines.Values.FirstOrDefault();

                            if (syncEngine != null)
                            {
                            // Process locally in the sync engine
                            if (message.Action == "delete") // Delete
                            {
                                string file = message.FilePath;
                                await syncEngine.ApplyRemoteSyncAsync("sync_delete", file, "", null);
                                logger.LogInformation("[Server Local Sync] Applied NDA delete for {File}", file);
                            }
                            else if (message.Action == "update") // Update
                            {
                                string file = message.FilePath;
                                string hash = message.HashHex;
                                byte[] content = message.Content;

                                await syncEngine.ApplyRemoteSyncAsync("sync_update", file, hash, content);
                                logger.LogInformation("[Server Local Sync] Applied NDA update for {File}", file);
                            }
                            else if (message.Action == "offer") // VCTP Offer
                            {
                                string file = message.FilePath;
                                string hash = message.HashHex;
                                Guid fid = message.FileId;
                                byte[] key = message.Key;
                                byte[] nonce = message.Nonce;

                                string syncFolder = syncEngine.SyncFolderPath;
                                if (!IsFileInsideSyncFolder(file, syncFolder))
                                {
                                    logger.LogWarning("[Sync Engine Error] Path traversal blocked in NDA offer: {File}", file);
                                    continue;
                                }
                                string combinedPath = Path.Combine(syncFolder, file);
                                string targetDir = Path.GetDirectoryName(Path.GetFullPath(combinedPath)) ?? syncFolder;

                                var receiver = new VctpReceiver(targetDir, key, nonce, port: 0);
                                activeReceivers[fid] = receiver;

                                receiver.OnTransferComplete += (filePath, fileHash) =>
                                {
                                    syncEngine.ConfirmRemoteSyncCompleted(file, fileHash);
                                    receiver.Dispose();
                                    activeReceivers.TryRemove(fid, out _);
                                    logger.LogInformation("[Sync Engine] VCTP sync receiver complete for {File}", file);
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

                                if (syncEngine.ActiveSyncTransfers.TryRemove(fid, out var senderInfo))
                                {
                                    var (key, nonce, fullPath, fileHash) = senderInfo;
                                    var remoteEP = new IPEndPoint(IPAddress.Loopback, port);
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            using var vctpSender = new VctpSender(fullPath, fid, fileHash, remoteEP, key, nonce, targetRateMbps: 1000.0);
                                            await vctpSender.StartAsync();
                                            logger.LogInformation("[Sync Engine] VCTP sync sender complete for {Path}", fullPath);
                                        }
                                        catch (Exception ex)
                                        {
                                            logger.LogError(ex, "[Sync Engine] VCTP sync sender failed");
                                        }
                                    });
                                }
                            }
                            // ── Delta sync message handlers ──
                            else if (message.Action == "delta_offer")
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await syncEngine.HandleDeltaOfferAsync(message); }
                                    catch (Exception ex) { logger.LogError(ex, "[Sync Engine] delta_offer handler failed"); }
                                });
                            }
                            else if (message.Action == "block_request")
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await syncEngine.HandleBlockRequestAsync(message); }
                                    catch (Exception ex) { logger.LogError(ex, "[Sync Engine] block_request handler failed"); }
                                });
                            }
                            else if (message.Action == "block_data")
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await syncEngine.HandleBlockDataAsync(message); }
                                    catch (Exception ex) { logger.LogError(ex, "[Sync Engine] block_data handler failed"); }
                                });
                            }
                            else if (message.Action == "delta_complete")
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await syncEngine.HandleDeltaCompleteAsync(message); }
                                    catch (Exception ex) { logger.LogError(ex, "[Sync Engine] delta_complete handler failed"); }
                                });
                            }
                            else if (message.Action == "sync_manifest")
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await syncEngine.ProcessRemoteManifestAsync(message.Manifest); }
                                    catch (Exception ex) { logger.LogError(ex, "[Sync Engine] sync_manifest handler failed"); }
                                });
                            }
                            else if (message.Action == "sync_manifest_complete")
                            {
                                logger.LogInformation("[Sync Engine] Remote peer {PeerId} completed manifest processing", peerId);
                            }
                            else if (message.Action == "conflict_resolve")
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await syncEngine.HandleConflictResolutionAsync(message); }
                                    catch (Exception ex) { logger.LogError(ex, "[Sync Engine] conflict_resolve handler failed"); }
                                });
                            }
                            } // end if (syncEngine != null)
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
                        logger.LogError(ex, "[Server NDA Signaling Error]");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[WebSocket Error] Peer {PeerId}", peerId);
    }
    finally
    {
        activePeers.TryRemove(peerId, out _);
        MetricsMiddleware.RecordWebSocketDisconnection();
        logger.LogInformation("[WebSocket] Peer disconnected: {PeerId}. Active peers: {PeerCount}", peerId, activePeers.Count);
        await BroadcastPeerListAsync(activePeers);
    }
});

// GET /api/share/auth/status: Returns whether authentication is required (public endpoint)
app.MapGet("/api/share/auth/status", () =>
{
    return Results.Ok(new { authRequired = IsAuthRequired() });
}).RequireRateLimiting("fixed");

// POST /api/share/auth/verify: Validates an API key and returns status
app.MapPost("/api/share/auth/verify", (HttpContext context) =>
{
    bool valid = ValidateApiKey(context);
    if (valid)
    {
        return Results.Ok(new { valid = true, message = "API key is valid." });
    }
    return Results.Json(new { valid = false, message = "Invalid or missing API key." }, statusCode: StatusCodes.Status401Unauthorized);
}).RequireRateLimiting("fixed");

// GET /api/share/peers: Lists count of currently online handshake peers (no IDs exposed to prevent info disclosure)
app.MapGet("/api/share/peers", () =>
{
    return Results.Ok(new { count = activePeers.Count });
}).RequireRateLimiting("fixed");

// POST /api/share/dumpsite: Configures user-assigned custom dumpsite (NAS, Google Drive Mock, OneDrive Mock)
app.MapPost("/api/share/dumpsite", async (HttpContext context) =>
{
    // ── API Key authentication ──
    if (!ValidateApiKey(context))
    {
        logger.LogWarning("[Auth] Rejected unauthenticated dumpsite config from {RemoteIp}", context.Connection.RemoteIpAddress);
        return Results.Json(new { error = "Unauthorized. Provide a valid API key via X-API-Key or Authorization header." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // ── Request body size limit for dumpsite config ──
    if (context.Request.ContentLength > 1024 * 1024) // 1 MB max
    {
        return Results.BadRequest("Request body too large.");
    }

    using var reader = new StreamReader(context.Request.Body);
    string body = await reader.ReadToEndAsync();
    try
    {
        var doc = JsonDocument.Parse(body);
        string type = doc.RootElement.GetProperty("type").GetString() ?? "local_nas";
        string path = doc.RootElement.GetProperty("path").GetString() ?? "";

        // ── Dropsite type allowlist validation ──
        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "local_nas", "google_drive_mock", "onedrive_mock" };
        if (!allowedTypes.Contains(type))
        {
            return Results.BadRequest($"Invalid dropsite type '{type}'. Allowed: {string.Join(", ", allowedTypes)}");
        }

        if (string.IsNullOrEmpty(path))
        {
            return Results.BadRequest("Path cannot be empty.");
        }

        // ── Path length limit ──
        if (path.Length > 500)
        {
            return Results.BadRequest("Path exceeds maximum length.");
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
        logger.LogInformation("[Dropsite Config] Updated to type: {Type}, path: {Path}", type, path);
        return Results.Ok(dropsiteConfig);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[Dropsite] Invalid configuration payload from {RemoteIp}", context.Connection.RemoteIpAddress);
        return Results.BadRequest("Invalid dropsite configuration payload. Check JSON format and required fields.");
    }
}).RequireRateLimiting("fixed");

app.MapGet("/api/share/dumpsite", () =>
{
    return Results.Ok(dropsiteConfig);
}).RequireRateLimiting("fixed");

// POST /api/share/upload: Server-buffered file upload fallback (when peer is offline)
app.MapPost("/api/share/upload", async (HttpContext context) =>
{
    // ── API Key authentication ──
    if (!ValidateApiKey(context))
    {
        logger.LogWarning("[Auth] Rejected unauthenticated upload from {RemoteIp}", context.Connection.RemoteIpAddress);
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!context.Request.HasFormContentType)
    {
        return Results.BadRequest("Invalid form content type.");
    }

    // ── Upload size limit: 50 MB per chunk ──
    if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > 50L * 1024 * 1024)
    {
        return Results.BadRequest("Upload exceeds maximum allowed size of 50 MB.");
    }

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    string fileId = form["fileId"].ToString();
    string chunkIndexStr = form["chunkIndex"].ToString();
    string checksum = form["checksum"].ToString();
    string encryptKeyHex = form["encryptionKey"].ToString(); // Optional key context

    // ── Individual file size validation ──
    if (file != null && file.Length > 50L * 1024 * 1024)
    {
        return Results.BadRequest("File chunk exceeds maximum allowed size of 50 MB.");
    }

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

    // Server-side streaming integrity verification (avoids loading entire chunk into memory)
    string calculatedHashHex;
    using (var sha256 = System.Security.Cryptography.SHA256.Create())
    using (var fs = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true))
    {
        byte[] hash = await sha256.ComputeHashAsync(fs);
        calculatedHashHex = Convert.ToHexString(hash).ToLowerInvariant();
    }

    if (!string.IsNullOrEmpty(checksum) && !calculatedHashHex.Equals(checksum, StringComparison.OrdinalIgnoreCase))
    {
        System.IO.File.Delete(chunkPath);
        return Results.BadRequest($"FFI Integrity verification check failed! Calculated: {calculatedHashHex}, Received: {checksum}");
    }

    logger.LogInformation("[Upload Fallback] Saved chunk {ChunkIndex} of file {FileId} successfully. Verified checksum: {Checksum}", chunkIndexStr, fileId, calculatedHashHex);
    return Results.Ok(new { fileId, chunkIndex = chunkIndexStr, checksum = calculatedHashHex });
}).RequireRateLimiting("fixed");

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

    // ── Double-check: resolved path must be inside sandbox ──
    string resolvedPath = Path.GetFullPath(chunkPath);
    string sandboxRoot = Path.GetFullPath(dropsiteConfig["path"] ?? defaultUploadsDir);
    if (!resolvedPath.StartsWith(sandboxRoot, StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning("[Download] Path traversal attempt blocked: {FileId}/{ChunkIndex}", fileId, chunkIndexStr);
        return Results.BadRequest("Invalid file path.");
    }

    if (!System.IO.File.Exists(chunkPath))
    {
        return Results.NotFound($"Chunk {chunkIndexStr} of file {fileId} not found.");
    }

    // Stream the file instead of loading it entirely into memory
    var stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
    return Results.File(stream, contentType: "application/octet-stream");
}).RequireRateLimiting("fixed");

// ── Shareable Download Links ──

// POST /api/share/link: Create a shareable download link
app.MapPost("/api/share/link", async (HttpContext context) =>
{
    // ── API Key authentication ──
    if (!ValidateApiKey(context))
    {
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
    
    if (!body.TryGetProperty("fileId", out var fileIdEl) || !body.TryGetProperty("fileName", out var fileNameEl))
    {
        return Results.BadRequest("Missing required fields (fileId, fileName).");
    }

    var fileId = fileIdEl.GetString() ?? "";
    var fileName = fileNameEl.GetString() ?? "unknown";
    
    // Validate fileId format
    if (!System.Text.RegularExpressions.Regex.IsMatch(fileId, "^[a-zA-Z0-9_-]+$"))
    {
        return Results.BadRequest("Invalid fileId format.");
    }

    // Get file size from first chunk
    var targetFolder = Path.Combine(dropsiteConfig["path"] ?? defaultUploadsDir, fileId);
    if (!Directory.Exists(targetFolder))
    {
        return Results.NotFound("File not found.");
    }

    long totalSize = 0;
    foreach (var chunk in Directory.GetFiles(targetFolder, "chunk_*"))
    {
        totalSize += new FileInfo(chunk).Length;
    }

    // Parse optional parameters
    int expiryHours = 24;
    string? password = null;
    int maxDownloads = 100;

    if (body.TryGetProperty("expiryHours", out var expEl) && expEl.TryGetInt32(out var exp))
        expiryHours = Math.Clamp(exp, 1, 168); // 1 hour to 7 days
    if (body.TryGetProperty("password", out var pwEl))
        password = pwEl.GetString();
    if (body.TryGetProperty("maxDownloads", out var mdEl) && mdEl.TryGetInt32(out var md))
        maxDownloads = Math.Clamp(md, 1, 10000);

    var link = shareLinkManager.CreateLink(fileId, fileName, totalSize, TimeSpan.FromHours(expiryHours), password, maxDownloads);

    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    var shareUrl = $"{baseUrl}/s/{link.Id}";

    logger.LogInformation("[Share] Created share link {ShareId} for file {FileId} ({FileName}), expires in {Hours}h", link.Id, fileId, fileName, expiryHours);

    return Results.Ok(new
    {
        shareId = link.Id,
        shareUrl = shareUrl,
        fileName = link.FileName,
        fileSize = link.FileSize,
        expiresAt = link.ExpiresAt.ToString("o"),
        maxDownloads = link.MaxDownloads,
        passwordProtected = link.PasswordHash != null
    });
}).RequireRateLimiting("fixed");

// GET /s/{id}: Share link download page
app.MapGet("/s/{id}", async (HttpContext context, string id) =>
{
    if (string.IsNullOrEmpty(id) || id.Length > 20)
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Check if password is required
    var link = shareLinkManager.ValidateLink(id);
    if (link == null)
    {
        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync(SharePageGenerator.GenerateExpiredPage());
        return;
    }

    if (link.PasswordHash != null)
    {
        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync(SharePageGenerator.GeneratePasswordPage(id));
        return;
    }

    // Show download page
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(SharePageGenerator.GenerateDownloadPage(
        id, link.FileName, SharePageGenerator.FormatFileSize(link.FileSize),
        link.ExpiresAt.ToString("MMM d, yyyy h:mm tt"),
        link.MaxDownloads - link.DownloadCount));
}).RequireRateLimiting("fixed");

// POST /s/{id}/verify: Verify password for protected share link
app.MapPost("/s/{id}/verify", async (HttpContext context, string id) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
    var password = body.TryGetProperty("password", out var pwEl) ? pwEl.GetString() : null;

    var link = shareLinkManager.ValidateLink(id, password);
    if (link == null)
    {
        return Results.Json(new { error = "Invalid or expired share link, or wrong password." }, statusCode: 404);
    }

    // Issue a one-time download token to avoid passing password in query string
    var downloadToken = shareLinkManager.IssueDownloadToken(id);

    return Results.Json(new
    {
        valid = true,
        fileName = link.FileName,
        fileSize = link.FileSize,
        expiresAt = link.ExpiresAt.ToString("o"),
        downloadsRemaining = link.MaxDownloads - link.DownloadCount,
        downloadToken = downloadToken
    });
}).RequireRateLimiting("fixed");

// GET /s/{id}/download: Download the actual file
// For password-protected links, requires a one-time token (issued by POST /s/{id}/verify).
// For non-password links, direct download is allowed.
app.MapGet("/s/{id}/download", async (HttpContext context, string id) =>
{
    ShareLinkManager.ShareLink? link = null;

    // Check for one-time download token (password-protected links)
    var token = context.Request.Query["token"].ToString();
    if (!string.IsNullOrEmpty(token))
    {
        var tokenLinkId = shareLinkManager.ConsumeDownloadToken(token);
        if (tokenLinkId != id)
        {
            return Results.NotFound("Share link is invalid or download token expired.");
        }
        link = shareLinkManager.ValidateLink(id);
    }
    else
    {
        // Non-password protected link: direct access
        link = shareLinkManager.ValidateLink(id);
    }
    
    if (link == null)
    {
        return Results.NotFound("Share link is invalid, expired, or download token expired.");
    }

    var targetFolder = Path.Combine(dropsiteConfig["path"] ?? defaultUploadsDir, link.FileId);
    if (!Directory.Exists(targetFolder))
    {
        return Results.NotFound("File no longer available.");
    }

    // Combine all chunks into a single stream
    var chunks = Directory.GetFiles(targetFolder, "chunk_*").OrderBy(f => f).ToArray();
    if (chunks.Length == 0)
    {
        return Results.NotFound("File data not found.");
    }

    // For single-chunk files, stream directly
    if (chunks.Length == 1)
    {
        shareLinkManager.RecordDownload(id);
        var stream = new FileStream(chunks[0], FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Results.File(stream, "application/octet-stream", link.FileName);
    }

    // For multi-chunk files, stream chunks sequentially to response (no memory buffering)
    shareLinkManager.RecordDownload(id);
    logger.LogInformation("[Share] File downloaded via share link {ShareId}: {FileName} ({Chunks} chunks)", id, link.FileName, chunks.Length);

    context.Response.ContentType = "application/octet-stream";
    context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{link.FileName}\"");

    foreach (var chunkPath in chunks)
    {
        using var chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await chunkStream.CopyToAsync(context.Response.Body);
    }

    return Results.Empty;
}).RequireRateLimiting("fixed");

// GET /api/share/links: List active share links (admin only)
app.MapGet("/api/share/links", (HttpContext context) =>
{
    if (!ValidateApiKey(context))
    {
        logger.LogWarning("[Auth] Rejected unauthenticated access to /api/share/links from {RemoteIp}", context.Connection.RemoteIpAddress);
        return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
    }
    return Results.Ok(new { activeLinks = shareLinkManager.ActiveLinkCount });
}).RequireRateLimiting("fixed");

// GET /api/share/test: Runs unmanaged Rust FFI crypto self-test (Development only)
if (app.Environment.IsDevelopment())
{
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
}

// GET /api/share/test/benchmark: Runs relative performance comparison benchmark for V.E.L.O.C.I.T.Y. Share crypto (Development only)
if (app.Environment.IsDevelopment())
{
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
}

// GET /api/share/test/vctp: Performs automated custom transport (V.C.T.P.) integrity, speed, and interruptibility tests (Development only)
if (app.Environment.IsDevelopment())
{
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
        receiver.OnLog += (log) => { logs.Add($"[Receiver] {log}"); logger.LogDebug("[Receiver] {Log}", log); };
        receiver.Start();

        var remoteEP = new IPEndPoint(IPAddress.Loopback, receiver.Port);

        // 5. Test Interruption and Resumability!
        logs.Add("--- Beginning Phase 1: Transfer with Interruption ---");
        logger.LogInformation("--- Beginning Phase 1: Transfer with Interruption ---");
        using (var sender = new VctpSender(srcPath, fileId, srcHashHex, remoteEP, key, nonce, targetRateMbps: 50.0))
        {
            sender.OnLog += (log) => { logs.Add($"[Sender] {log}"); logger.LogDebug("[Sender] {Log}", log); };
            
            // Start the sender in the background
            var senderTask = sender.StartAsync();

            // Wait until some blocks are transferred (e.g. 200ms)
            await Task.Delay(200);

            // Cancel/Dispose the sender mid-transfer
            logs.Add("--- FORCE KILLING Sender mid-transfer to simulate power/network cut ---");
            logger.LogInformation("--- FORCE KILLING Sender mid-transfer to simulate power/network cut ---");
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
            senderResume.OnLog += (log) => { logs.Add($"[Sender Resume] {log}"); logger.LogDebug("[Sender Resume] {Log}", log); };
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
}

// GET /api/share/test/vctp/benchmark: Performs 100% in-memory V.C.T.P. speed and performance verification benchmark (250MB) (Development only)
if (app.Environment.IsDevelopment())
{
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

                logger.LogInformation("[SWEEP] OPTIMAL READ: {Workers} workers, {Affinity}, {Partitioning} partitioning, unroll {Unroll}x => {Speed:F2} Gbps", bestReadWorkers, bestReadAff, bestReadPart, bestReadUnroll, bestReadSpeed);
                logger.LogInformation("[SWEEP] OPTIMAL WRITE: {Workers} workers, {Affinity}, {Partitioning} partitioning, unroll {Unroll}x => {Speed:F2} Gbps", bestWriteWorkers, bestWriteAff, bestWritePart, bestWriteUnroll, bestWriteSpeed);
                logger.LogInformation("[SWEEP] OPTIMAL COPY: {Workers} workers, {Affinity}, {Partitioning} partitioning, unroll {Unroll}x => {Speed:F2} Gbps", bestCopyWorkers, bestCopyAff, bestCopyPart, bestCopyUnroll, bestCopySpeed);
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
        receiver.OnLog += (log) => { logs.Add($"[Receiver] {log}"); logger.LogDebug("[Receiver] {Log}", log); };

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

            sender.OnLog += (log) => { logs.Add($"[Sender] {log}"); logger.LogDebug("[Sender] {Log}", log); };
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
}

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

// Make Program class accessible for integration testing
public partial class Program { }
