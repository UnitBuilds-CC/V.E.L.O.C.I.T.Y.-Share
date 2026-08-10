using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Amazon S3 storage provider. Uses the S3 REST API with AWS Signature V4.
/// Requires: bucket name, region, access key, secret key.
/// Includes retry logic with exponential backoff for transient failures.
/// </summary>
public sealed class S3SyncStorageProvider : ISyncStorageProvider
{
    private readonly string _bucket;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _prefix;
    private readonly HttpClient _http;

    // Retry configuration
    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);

    public string ProviderType => "s3";

    public S3SyncStorageProvider(string bucket, string region, string accessKey, string secretKey, string prefix = "")
    {
        _bucket = bucket;
        _region = region;
        _accessKey = accessKey;
        _secretKey = secretKey;
        _prefix = prefix.TrimStart('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30), BaseAddress = new Uri($"https://{bucket}.s3.{region}.amazonaws.com/") };
    }

    /// <summary>
    /// Internal constructor for testing with a custom HttpClient (e.g., mock handler).
    /// </summary>
    internal S3SyncStorageProvider(string bucket, string region, string accessKey, string secretKey, string prefix, HttpClient httpClient)
    {
        _bucket = bucket;
        _region = region;
        _accessKey = accessKey;
        _secretKey = secretKey;
        _prefix = prefix.TrimStart('/');
        _http = httpClient;
    }

    private string S3Key(string relativePath) =>
        string.IsNullOrEmpty(_prefix) ? relativePath : $"{_prefix}/{relativePath}";

    /// <summary>
    /// Retry wrapper for read/head requests: re-signs and re-sends on transient failures (5xx, 429, network errors).
    /// Uses exponential backoff with jitter. Optional requestCustomizer callback to set headers per attempt.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpMethod method, string key, CancellationToken ct, Action<HttpRequestMessage>? requestCustomizer = null)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = await SignRequestAsync(method, key, ct);
                requestCustomizer?.Invoke(request);
                var response = await _http.SendAsync(request, ct);

                if (attempt < MaxRetries && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests))
                {
                    response.Dispose();
                    await DelayWithJitter(attempt, ct);
                    continue;
                }
                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                await DelayWithJitter(attempt, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                lastException = ex;
                await DelayWithJitter(attempt, ct);
            }
        }
        throw new InvalidOperationException($"S3 request failed after {MaxRetries + 1} attempts", lastException);
    }

    /// <summary>
    /// Retry wrapper for write (PUT) requests. Creates fresh ByteArrayContent per attempt
    /// because HttpRequestMessage.Dispose disposes the content.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryForWriteAsync(HttpMethod method, string key, byte[] data, CancellationToken ct)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = await SignRequestAsync(method, key, ct);
                request.Content = new ByteArrayContent(data);
                var response = await _http.SendAsync(request, ct);

                if (attempt < MaxRetries && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests))
                {
                    response.Dispose();
                    await DelayWithJitter(attempt, ct);
                    continue;
                }
                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                await DelayWithJitter(attempt, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                lastException = ex;
                await DelayWithJitter(attempt, ct);
            }
        }
        throw new InvalidOperationException($"S3 write failed after {MaxRetries + 1} attempts", lastException);
    }

    private static async Task DelayWithJitter(int attempt, CancellationToken ct)
    {
        int baseMs = (int)BaseDelay.TotalMilliseconds * (1 << attempt); // 200, 400, 800ms
        int jitter = Random.Shared.Next(0, baseMs / 2);
        await Task.Delay(baseMs + jitter, ct);
    }

    private async Task<HttpRequestMessage> SignRequestAsync(HttpMethod method, string key, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, key);
        string date = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        string dateStamp = DateTime.UtcNow.ToString("yyyyMMdd");

        request.Headers.TryAddWithoutValidation("x-amz-date", date);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "UNSIGNED-PAYLOAD");

        string stringToSign = $"AWS4-HMAC-SHA256\n{date}\n{dateStamp}/{_region}/s3/aws4_request\n";
        byte[] signingKey = HmacSha256(
            HmacSha256(
                HmacSha256(
                    HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretKey), dateStamp),
                    _region),
                "s3"),
            "aws4_request");

        byte[] signature = HmacSha256(signingKey, stringToSign);

        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={_accessKey}/{dateStamp}/{_region}/s3/aws4_request, SignedHeaders=x-amz-date;x-amz-content-sha256, Signature={Convert.ToHexString(signature).ToLowerInvariant()}");

        return request;
    }

    public async Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Get, S3Key(relativePath), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]> ReadFileBlockAsync(string relativePath, long offset, int length, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Get, S3Key(relativePath), ct,
            r => r.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{offset + length - 1}"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task WriteFileAsync(string relativePath, byte[] content, CancellationToken ct = default)
    {
        using var response = await SendWithRetryForWriteAsync(HttpMethod.Put, S3Key(relativePath), content, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task WriteFileBlockAsync(string relativePath, long offset, byte[] blockData, CancellationToken ct = default)
    {
        // S3 doesn't support partial writes — read-modify-write
        byte[] existing;
        try { existing = await ReadFileAsync(relativePath, ct); }
        catch { existing = Array.Empty<byte>(); }

        long newSize = Math.Max(existing.Length, offset + blockData.Length);
        byte[] merged = new byte[newSize];
        Buffer.BlockCopy(existing, 0, merged, 0, existing.Length);
        Buffer.BlockCopy(blockData, 0, merged, (int)offset, blockData.Length);

        await WriteFileAsync(relativePath, merged, ct);
    }

    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Delete, S3Key(relativePath), ct);
    }

    public async Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(HttpMethod.Head, S3Key(relativePath), ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<long> GetFileSizeAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(HttpMethod.Head, S3Key(relativePath), ct);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    public async Task<DateTimeOffset> GetLastModifiedAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(HttpMethod.Head, S3Key(relativePath), ct);
            return response.Content.Headers.LastModified ?? DateTimeOffset.MinValue;
        }
        catch { return DateTimeOffset.MinValue; }
    }

    public async IAsyncEnumerable<string> EnumerateFilesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        string continuationToken = "";
        do
        {
            string url = $"?list-type=2&prefix={Uri.EscapeDataString(_prefix)}";
            if (!string.IsNullOrEmpty(continuationToken))
                url += $"&continuation-token={Uri.EscapeDataString(continuationToken)}";

            using var response = await SendWithRetryAsync(HttpMethod.Get, url, ct);
            response.EnsureSuccessStatusCode();

            string xml = await response.Content.ReadAsStringAsync(ct);
            foreach (var key in ExtractKeysFromXml(xml, _prefix))
            {
                ct.ThrowIfCancellationRequested();
                yield return key;
            }

            continuationToken = ExtractValueFromXml(xml, "NextContinuationToken") ?? "";
        } while (!string.IsNullOrEmpty(continuationToken));
    }

    public Task EnsureDirectoryAsync(string relativeDirPath, CancellationToken ct = default)
        => Task.CompletedTask; // S3 has no directories

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static IEnumerable<string> ExtractKeysFromXml(string xml, string prefix)
    {
        int idx = 0;
        while ((idx = xml.IndexOf("<Key>", idx, StringComparison.Ordinal)) >= 0)
        {
            int end = xml.IndexOf("</Key>", idx, StringComparison.Ordinal);
            if (end < 0) break;
            string key = xml.Substring(idx + 5, end - idx - 5);
            if (!string.IsNullOrEmpty(prefix) && key.StartsWith(prefix))
                key = key.Substring(prefix.Length).TrimStart('/');
            if (!string.IsNullOrEmpty(key) && !key.EndsWith("/"))
                yield return key;
            idx = end + 6;
        }
    }

    private static string? ExtractValueFromXml(string xml, string tag)
    {
        string open = $"<{tag}>";
        string close = $"</{tag}>";
        int start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        int end = xml.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0) return null;
        return xml.Substring(start, end - start);
    }
}
