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
/// Azure Blob Storage provider. Uses the Blob REST API with Shared Key auth.
/// Requires: account name, account key, container name.
/// </summary>
public sealed class AzureBlobSyncStorageProvider : ISyncStorageProvider
{
    private readonly string _accountName;
    private readonly string _accountKey;
    private readonly string _container;
    private readonly string _prefix;
    private readonly HttpClient _http;

    // Retry configuration
    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);

    public string ProviderType => "azure";

    public AzureBlobSyncStorageProvider(string accountName, string accountKey, string container, string prefix = "")
    {
        _accountName = accountName;
        _accountKey = accountKey;
        _container = container;
        _prefix = prefix.TrimStart('/');
        _http = new HttpClient { BaseAddress = new Uri($"https://{accountName}.blob.core.windows.net/") };
    }

    /// <summary>
    /// Internal constructor for testing with a custom HttpClient (e.g., mock handler).
    /// </summary>
    internal AzureBlobSyncStorageProvider(string accountName, string accountKey, string container, string prefix, HttpClient httpClient)
    {
        _accountName = accountName;
        _accountKey = accountKey;
        _container = container;
        _prefix = prefix.TrimStart('/');
        _http = httpClient;
    }

    private string BlobName(string relativePath) =>
        string.IsNullOrEmpty(_prefix) ? relativePath : $"{_prefix}/{relativePath}";

    /// <summary>
    /// Retry wrapper for read/head requests with exponential backoff and jitter.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpMethod method, string blobName, CancellationToken ct, Action<HttpRequestMessage>? requestCustomizer = null)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = await CreateSignedRequestAsync(method, blobName, ct);
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
        throw new InvalidOperationException($"Azure Blob request failed after {MaxRetries + 1} attempts", lastException);
    }

    /// <summary>
    /// Retry wrapper for write (PUT) requests. Creates fresh ByteArrayContent per attempt.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryForWriteAsync(HttpMethod method, string blobName, byte[] data, CancellationToken ct)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = await CreateSignedRequestAsync(method, blobName, ct);
                request.Content = new ByteArrayContent(data);
                request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
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
        throw new InvalidOperationException($"Azure Blob write failed after {MaxRetries + 1} attempts", lastException);
    }

    private static async Task DelayWithJitter(int attempt, CancellationToken ct)
    {
        int baseMs = (int)BaseDelay.TotalMilliseconds * (1 << attempt);
        int jitter = Random.Shared.Next(0, baseMs / 2);
        await Task.Delay(baseMs + jitter, ct);
    }

    private async Task<HttpRequestMessage> CreateSignedRequestAsync(HttpMethod method, string blobName, CancellationToken ct)
    {
        string date = DateTime.UtcNow.ToString("R");
        string url = $"{_container}/{blobName}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-ms-date", date);
        request.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");

        // Shared Key authorization
        string stringToSign = $"{method}\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:{date}\nx-ms-version:2023-11-03\n/{_accountName}/{url}";
        using var hmac = new HMACSHA256(Convert.FromBase64String(_accountKey));
        byte[] signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        string authHeader = $"SharedKey {_accountName}:{Convert.ToBase64String(signature)}";

        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        return request;
    }

    public async Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Get, BlobName(relativePath), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]> ReadFileBlockAsync(string relativePath, long offset, int length, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Get, BlobName(relativePath), ct,
            r => r.Headers.TryAddWithoutValidation("x-ms-range", $"bytes={offset}-{offset + length - 1}"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task WriteFileAsync(string relativePath, byte[] content, CancellationToken ct = default)
    {
        using var response = await SendWithRetryForWriteAsync(HttpMethod.Put, BlobName(relativePath), content, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task WriteFileBlockAsync(string relativePath, long offset, byte[] blockData, CancellationToken ct = default)
    {
        // Azure Blob doesn't support partial writes on Block Blobs — read-modify-write
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
        using var response = await SendWithRetryAsync(HttpMethod.Delete, BlobName(relativePath), ct);
    }

    public async Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(HttpMethod.Head, BlobName(relativePath), ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<long> GetFileSizeAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(HttpMethod.Head, BlobName(relativePath), ct);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    public async Task<DateTimeOffset> GetLastModifiedAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(HttpMethod.Head, BlobName(relativePath), ct);
            return response.Content.Headers.LastModified ?? DateTimeOffset.MinValue;
        }
        catch { return DateTimeOffset.MinValue; }
    }

    public async IAsyncEnumerable<string> EnumerateFilesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        string marker = "";
        do
        {
            string url = $"{_container}?restype=container&comp=list";
            if (!string.IsNullOrEmpty(_prefix))
                url += $"&prefix={Uri.EscapeDataString(_prefix)}";
            if (!string.IsNullOrEmpty(marker))
                url += $"&marker={Uri.EscapeDataString(marker)}";

            using var request = await CreateSignedRequestAsync(HttpMethod.Get, $"{_container}?restype=container&comp=list", ct);
            // Rebuild the URL with query params
            request.RequestUri = new Uri(_http.BaseAddress!, url);

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string xml = await response.Content.ReadAsStringAsync(ct);
            foreach (var name in ExtractBlobNames(xml, _prefix))
            {
                ct.ThrowIfCancellationRequested();
                yield return name;
            }

            marker = ExtractValueFromXml(xml, "NextMarker") ?? "";
        } while (!string.IsNullOrEmpty(marker));
    }

    public Task EnsureDirectoryAsync(string relativeDirPath, CancellationToken ct = default)
        => Task.CompletedTask; // Azure Blob has no directories

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> ExtractBlobNames(string xml, string prefix)
    {
        int idx = 0;
        while ((idx = xml.IndexOf("<Name>", idx, StringComparison.Ordinal)) >= 0)
        {
            int end = xml.IndexOf("</Name>", idx, StringComparison.Ordinal);
            if (end < 0) break;
            string name = xml.Substring(idx + 6, end - idx - 6);
            if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix))
                name = name.Substring(prefix.Length).TrimStart('/');
            if (!string.IsNullOrEmpty(name) && !name.EndsWith("/"))
                yield return name;
            idx = end + 7;
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
