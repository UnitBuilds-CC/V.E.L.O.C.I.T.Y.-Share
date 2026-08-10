using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace VelocityShare.Server;

/// <summary>
/// Prometheus-compatible metrics middleware for monitoring and alerting.
/// Tracks request counts, durations, and active connections.
/// </summary>
public class MetricsMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly ConcurrentDictionary<string, long> _counters = new();
    private static readonly ConcurrentDictionary<string, double> _histograms = new();
    private static long _activeConnections;
    private static long _activeWebSockets;
    private static long _totalBytesUploaded;
    private static long _totalBytesDownloaded;
    private static long _activeTransfers;

    public MetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "/unknown";
        
        // Skip metrics for the metrics endpoint itself
        if (path == "/metrics")
        {
            await _next(context);
            return;
        }

        Interlocked.Increment(ref _activeConnections);
        
        try
        {
            await _next(context);
            
            var duration = sw.Elapsed.TotalSeconds;
            var statusCode = context.Response.StatusCode.ToString();
            var method = context.Request.Method;
            
            // Increment request counter
            var counterKey = $"{method}_{path}_{statusCode}";
            _counters.AddOrUpdate(counterKey, 1, (_, count) => count + 1);
            
            // Record request duration
            var histogramKey = $"{method}_{path}";
            _histograms.AddOrUpdate(histogramKey, duration, (_, total) => total + duration);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    // Public methods to update metrics from other parts of the application
    public static void RecordWebSocketConnection() => Interlocked.Increment(ref _activeWebSockets);
    public static void RecordWebSocketDisconnection() => Interlocked.Decrement(ref _activeWebSockets);
    public static void RecordUpload(long bytes) => Interlocked.Add(ref _totalBytesUploaded, bytes);
    public static void RecordDownload(long bytes) => Interlocked.Add(ref _totalBytesDownloaded, bytes);
    public static void RecordTransferStart() => Interlocked.Increment(ref _activeTransfers);
    public static void RecordTransferEnd() => Interlocked.Decrement(ref _activeTransfers);

    /// <summary>
    /// Generates Prometheus-compatible metrics output.
    /// </summary>
    public static string GenerateMetrics()
    {
        var sb = new StringBuilder();
        
        // Active connections gauge
        sb.AppendLine("# HELP velocity_active_connections Current number of active HTTP connections");
        sb.AppendLine("# TYPE velocity_active_connections gauge");
        sb.AppendLine($"velocity_active_connections {Interlocked.Read(ref _activeConnections)}");
        sb.AppendLine();
        
        // Active WebSocket connections
        sb.AppendLine("# HELP velocity_active_websockets Current number of active WebSocket connections");
        sb.AppendLine("# TYPE velocity_active_websockets gauge");
        sb.AppendLine($"velocity_active_websockets {Interlocked.Read(ref _activeWebSockets)}");
        sb.AppendLine();
        
        // Active file transfers
        sb.AppendLine("# HELP velocity_active_transfers Current number of active file transfers");
        sb.AppendLine("# TYPE velocity_active_transfers gauge");
        sb.AppendLine($"velocity_active_transfers {Interlocked.Read(ref _activeTransfers)}");
        sb.AppendLine();
        
        // Total bytes uploaded
        sb.AppendLine("# HELP velocity_bytes_uploaded_total Total bytes uploaded");
        sb.AppendLine("# TYPE velocity_bytes_uploaded_total counter");
        sb.AppendLine($"velocity_bytes_uploaded_total {Interlocked.Read(ref _totalBytesUploaded)}");
        sb.AppendLine();
        
        // Total bytes downloaded
        sb.AppendLine("# HELP velocity_bytes_downloaded_total Total bytes downloaded");
        sb.AppendLine("# TYPE velocity_bytes_downloaded_total counter");
        sb.AppendLine($"velocity_bytes_downloaded_total {Interlocked.Read(ref _totalBytesDownloaded)}");
        sb.AppendLine();
        
        // Request counters by endpoint
        sb.AppendLine("# HELP velocity_http_requests_total Total number of HTTP requests");
        sb.AppendLine("# TYPE velocity_http_requests_total counter");
        foreach (var kvp in _counters)
        {
            var parts = kvp.Key.Split('_');
            if (parts.Length >= 3)
            {
                var method = parts[0];
                var status = parts[parts.Length - 1];
                var path = string.Join("_", parts[1..^1]);
                sb.AppendLine($"velocity_http_requests_total{{method=\"{method}\",path=\"{path}\",status=\"{status}\"}} {kvp.Value}");
            }
        }
        sb.AppendLine();
        
        // Request duration summaries
        sb.AppendLine("# HELP velocity_request_duration_seconds Request duration in seconds");
        sb.AppendLine("# TYPE velocity_request_duration_seconds summary");
        foreach (var kvp in _histograms)
        {
            var parts = kvp.Key.Split('_');
            if (parts.Length >= 2)
            {
                var method = parts[0];
                var path = string.Join("_", parts[1..]);
                sb.AppendLine($"velocity_request_duration_seconds_sum{{method=\"{method}\",path=\"{path}\"}} {kvp.Value:F3}");
            }
        }
        
        return sb.ToString();
    }
}
