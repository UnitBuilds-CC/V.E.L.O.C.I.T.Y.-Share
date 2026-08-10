using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelocityShare.Server;
using VelocityShare.Server.Sync;
using VelocityShare.Protocol;

namespace VelocityShare.Tests;

// ── Mock HTTP handler for cloud storage retry testing ──

/// <summary>
/// Mock HttpMessageHandler that returns configurable responses for testing retry logic.
/// </summary>
internal class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _requests = new();

    public IReadOnlyList<HttpRequestMessage> ReceivedRequests => _requests;
    public int CallCount => _requests.Count;

    public void EnqueueResponse(HttpStatusCode statusCode, string content = "")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8),
        };
        _responses.Enqueue(response);
    }

    public void EnqueueSuccess(string content = "ok")
        => EnqueueResponse(HttpStatusCode.OK, content);

    public void EnqueueServerError()
        => EnqueueResponse(HttpStatusCode.InternalServerError);

    public void EnqueueTooManyRequests()
        => EnqueueResponse((HttpStatusCode)429);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        if (_responses.Count == 0)
            throw new InvalidOperationException($"No more mock responses queued. Call #{_requests.Count}");
        return Task.FromResult(_responses.Dequeue());
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Cloud Storage Retry Tests
// ═══════════════════════════════════════════════════════════════════════════

public class S3RetryTests
{
    private static S3SyncStorageProvider CreateProvider(MockHttpHandler handler)
    {
        var client = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri("https://test-bucket.s3.us-east-1.amazonaws.com/")
        };
        return new S3SyncStorageProvider("test-bucket", "us-east-1", "AKID", "secret", "", client);
    }

    [Fact]
    public async Task ReadFile_SucceedsOnFirstAttempt()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueSuccess("file-content");
        var provider = CreateProvider(handler);

        var result = await provider.ReadFileAsync("test.txt");

        Assert.Equal("file-content", Encoding.UTF8.GetString(result));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ReadFile_RetriesOn500_ThenSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();   // attempt 0: 500
        handler.EnqueueServerError();   // attempt 1: 500
        handler.EnqueueSuccess("data"); // attempt 2: 200 OK
        var provider = CreateProvider(handler);

        var result = await provider.ReadFileAsync("test.txt");

        Assert.Equal("data", Encoding.UTF8.GetString(result));
        Assert.Equal(3, handler.CallCount); // 2 retries + 1 success
    }

    [Fact]
    public async Task ReadFile_RetriesOn429_ThenSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueTooManyRequests(); // attempt 0: 429
        handler.EnqueueSuccess("data");   // attempt 1: 200 OK
        var provider = CreateProvider(handler);

        var result = await provider.ReadFileAsync("test.txt");

        Assert.Equal("data", Encoding.UTF8.GetString(result));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ReadFile_ExhaustsRetries_Throws()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError(); // attempt 0
        handler.EnqueueServerError(); // attempt 1
        handler.EnqueueServerError(); // attempt 2
        handler.EnqueueServerError(); // attempt 3 (final)
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ReadFileAsync("test.txt"));
        Assert.Equal(4, handler.CallCount); // MaxRetries(3) + 1
    }

    [Fact]
    public async Task WriteFile_RetriesOn500_ThenSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();   // attempt 0: 500
        handler.EnqueueSuccess();       // attempt 1: 200 OK
        var provider = CreateProvider(handler);

        await provider.WriteFileAsync("test.txt", "content"u8.ToArray());

        Assert.Equal(2, handler.CallCount);
        // Verify the write request used PUT
        Assert.All(handler.ReceivedRequests, r => Assert.Equal(HttpMethod.Put, r.Method));
    }

    [Fact]
    public async Task FileExists_ReturnsTrueOn200()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueSuccess();
        var provider = CreateProvider(handler);

        Assert.True(await provider.FileExistsAsync("test.txt"));
    }

    [Fact]
    public async Task FileExists_ReturnsFalseOn404()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        Assert.False(await provider.FileExistsAsync("test.txt"));
    }

    [Fact]
    public async Task FileExists_RetriesOn500_ThenReturnsFalse404()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();
        handler.EnqueueResponse(HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        Assert.False(await provider.FileExistsAsync("test.txt"));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task DeleteFile_RetriesOn500()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();
        handler.EnqueueSuccess();
        var provider = CreateProvider(handler);

        await provider.DeleteFileAsync("test.txt");

        Assert.Equal(2, handler.CallCount);
        Assert.All(handler.ReceivedRequests, r => Assert.Equal(HttpMethod.Delete, r.Method));
    }

    [Fact]
    public async Task ReadFile_DoesNotRetryOn404()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        // 404 is not a transient error — should not retry
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ReadFileAsync("test.txt"));
        Assert.Equal(1, handler.CallCount); // No retries for 4xx
    }
}

public class AzureRetryTests
{
    private static AzureBlobSyncStorageProvider CreateProvider(MockHttpHandler handler)
    {
        var client = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri("https://testaccount.blob.core.windows.net/")
        };
        return new AzureBlobSyncStorageProvider("testaccount", Convert.ToBase64String(Encoding.UTF8.GetBytes("testkey12345678901234567890123456789012")), "testcontainer", "", client);
    }

    [Fact]
    public async Task ReadFile_SucceedsOnFirstAttempt()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueSuccess("azure-content");
        var provider = CreateProvider(handler);

        var result = await provider.ReadFileAsync("test.txt");

        Assert.Equal("azure-content", Encoding.UTF8.GetString(result));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ReadFile_RetriesOn500_ThenSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();
        handler.EnqueueSuccess("data");
        var provider = CreateProvider(handler);

        var result = await provider.ReadFileAsync("test.txt");

        Assert.Equal("data", Encoding.UTF8.GetString(result));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task WriteFile_RetriesOn429_ThenSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueTooManyRequests();
        handler.EnqueueSuccess();
        var provider = CreateProvider(handler);

        await provider.WriteFileAsync("test.txt", "content"u8.ToArray());

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task WriteFile_IncludesBlobTypeHeader()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueSuccess();
        var provider = CreateProvider(handler);

        await provider.WriteFileAsync("test.txt", "data"u8.ToArray());

        var request = handler.ReceivedRequests[0];
        Assert.True(request.Headers.Contains("x-ms-blob-type"));
    }

    [Fact]
    public async Task ReadFile_ExhaustsRetries_Throws()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();
        handler.EnqueueServerError();
        handler.EnqueueServerError();
        handler.EnqueueServerError();
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ReadFileAsync("test.txt"));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task FileExists_RetriesOnServerError()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueServerError();
        handler.EnqueueSuccess(); // HEAD 200 = exists
        var provider = CreateProvider(handler);

        Assert.True(await provider.FileExistsAsync("blob.txt"));
        Assert.Equal(2, handler.CallCount);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Certificate Validation Tests
// ═══════════════════════════════════════════════════════════════════════════

public class CertificateValidationTests
{
    [Fact]
    public void NullCertificate_AlwaysRejected()
    {
        Assert.False(CertificateValidator.Validate("https://example.com", null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Localhost_AcceptedRegardlessOfPolicy()
    {
        // Create a self-signed cert for testing
        using var cert = CreateSelfSignedCert();

        Assert.True(CertificateValidator.Validate("https://localhost:5000", cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(CertificateValidator.Validate("https://localhost", cert, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void LoopbackIP_AcceptedAsLocalhost()
    {
        using var cert = CreateSelfSignedCert();

        Assert.True(CertificateValidator.Validate("https://127.0.0.1:5000", cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        // Note: Uri.Host for IPv6 returns "[::1]" with brackets, but the validator
        // checks the exact string "::1" — this matches how the mobile client uses it
        // (the server URL is typically "http://localhost:5000" or "http://127.0.0.1:5000")
        Assert.True(CertificateValidator.Validate("http://127.0.0.1", cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void NonLoopback_RequiresNoPolicyErrors()
    {
        using var cert = CreateSelfSignedCert();

        // Production server with chain errors — rejected
        Assert.False(CertificateValidator.Validate("https://example.com", cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void NonLoopback_NoErrors_NullChain_Rejected()
    {
        using var cert = CreateSelfSignedCert();

        // No chain provided — rejected in production
        Assert.False(CertificateValidator.Validate("https://example.com", cert, null, SslPolicyErrors.None));
    }

    [Fact]
    public void SubdomainContainingLocalhost_NotBypassed()
    {
        using var cert = CreateSelfSignedCert();

        // "localhost.evil.com" should NOT get the localhost bypass
        Assert.False(CertificateValidator.Validate("https://localhost.evil.com", cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void NullServerUrl_RequiresStrictValidation()
    {
        using var cert = CreateSelfSignedCert();

        // No server URL — can't determine if localhost, so enforce strict policy
        Assert.False(CertificateValidator.Validate(null, cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void IsLoopbackHost_CorrectlyIdentifies()
    {
        Assert.True(CertificateValidator.IsLoopbackHost("localhost"));
        Assert.True(CertificateValidator.IsLoopbackHost("127.0.0.1"));
        Assert.True(CertificateValidator.IsLoopbackHost("::1"));
        Assert.False(CertificateValidator.IsLoopbackHost("example.com"));
        Assert.False(CertificateValidator.IsLoopbackHost("localhost.evil.com"));
        Assert.False(CertificateValidator.IsLoopbackHost("127.0.0.2"));
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var dn = new X500DistinguishedName("CN=test");
        var request = new CertificateRequest(dn, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// NDA Protocol Parsing Tests
// ═══════════════════════════════════════════════════════════════════════════

public class NdaProtocolTests
{
    [Fact]
    public void CreateUpdate_ParsedMessage_Roundtrips()
    {
        byte[] content = "hello world"u8.ToArray();
        var nda = NdaSignaling.CreateUpdate("peer1", "docs/file.txt", "abc123", 1024, content);

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer1", parsed.TargetPeerId);
        Assert.Equal("update", parsed.Action);
        Assert.Equal("docs/file.txt", parsed.FilePath);
        Assert.Equal("abc123", parsed.HashHex);
        Assert.Equal(1024, parsed.FileSize);
        Assert.Equal(content, parsed.Content);
    }

    [Fact]
    public void CreateDelete_ParsedMessage_Roundtrips()
    {
        var nda = NdaSignaling.CreateDelete("peer2", "old/file.txt");

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer2", parsed.TargetPeerId);
        Assert.Equal("delete", parsed.Action);
        Assert.Equal("old/file.txt", parsed.FilePath);
    }

    [Fact]
    public void CreateOffer_ParsedMessage_Roundtrips()
    {
        var fileId = Guid.NewGuid();
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(nonce);

        var nda = NdaSignaling.CreateOffer("peer3", "big.bin", "hash456", 999999, fileId, key, nonce);

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer3", parsed.TargetPeerId);
        Assert.Equal("offer", parsed.Action);
        Assert.Equal("big.bin", parsed.FilePath);
        Assert.Equal("hash456", parsed.HashHex);
        Assert.Equal(999999, parsed.FileSize);
        Assert.Equal(fileId, parsed.FileId);
        Assert.Equal(key, parsed.Key);
        Assert.Equal(nonce, parsed.Nonce);
    }

    [Fact]
    public void CreateAccept_ParsedMessage_Roundtrips()
    {
        var fileId = Guid.NewGuid();
        var nda = NdaSignaling.CreateAccept("peer4", fileId, 8080, "192.168.1.100");

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer4", parsed.TargetPeerId);
        Assert.Equal("accept", parsed.Action);
        Assert.Equal(fileId, parsed.FileId);
        Assert.Equal(8080, parsed.Port);
        Assert.Equal("192.168.1.100", parsed.SenderIp);
    }

    [Fact]
    public void CreateDeltaOffer_ParsedMessage_Roundtrips()
    {
        var nda = NdaSignaling.CreateDeltaOffer("peer5", "changed.bin", "newhash", 4096, 65536, "0:aaa,1:bbb", 1700000000);

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer5", parsed.TargetPeerId);
        Assert.Equal("delta_offer", parsed.Action);
        Assert.Equal("changed.bin", parsed.FilePath);
        Assert.Equal("newhash", parsed.HashHex);
        Assert.Equal(4096, parsed.FileSize);
        Assert.Equal(65536, parsed.BlockSize);
        Assert.Equal("0:aaa,1:bbb", parsed.BlockList);
        Assert.Equal(1700000000, parsed.LastModified);
    }

    [Fact]
    public void CreateBlockData_ParsedMessage_Roundtrips()
    {
        byte[] blockData = new byte[128];
        Random.Shared.NextBytes(blockData);

        var nda = NdaSignaling.CreateBlockData("peer6", "file.dat", 3, 196608, blockData, "blockhash");

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer6", parsed.TargetPeerId);
        Assert.Equal("block_data", parsed.Action);
        Assert.Equal("file.dat", parsed.FilePath);
        Assert.Equal(3, parsed.BlockIndex);
        Assert.Equal(196608, parsed.BlockOffset);
        Assert.Equal("blockhash", parsed.BlockHash);
        Assert.Equal(blockData, parsed.Content);
    }

    [Fact]
    public void CreateConflictResolution_ParsedMessage_Roundtrips()
    {
        var nda = NdaSignaling.CreateConflictResolution("peer7", "conflict.txt", "ourhash", 2048, 1700000001, weWin: true);

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer7", parsed.TargetPeerId);
        Assert.Equal("conflict_resolve", parsed.Action);
        Assert.Equal("conflict.txt", parsed.FilePath);
        Assert.Equal("ourhash", parsed.HashHex);
        Assert.Equal(2048, parsed.FileSize);
        Assert.Equal(1700000001, parsed.LastModified);
        Assert.Equal("us", parsed.Winner);
    }

    [Fact]
    public void CreateSyncManifest_ParsedMessage_Roundtrips()
    {
        string manifest = "a.txt|hash1|100|1000,b.txt|hash2|200|2000";
        var nda = NdaSignaling.CreateSyncManifest("peer8", manifest);

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer8", parsed.TargetPeerId);
        Assert.Equal("sync_manifest", parsed.Action);
        Assert.Equal(manifest, parsed.Manifest);
    }

    [Fact]
    public void CreatePeerList_ParsedMessage_HasAction()
    {
        var nda = NdaSignaling.CreatePeerList(new[] { "peer1", "peer2", "peer3" });

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer_list", parsed.Action);
    }

    [Fact]
    public void CreateUpdate_WithNullContent_ParsedMessage_HasEmptyContent()
    {
        var nda = NdaSignaling.CreateUpdate("peer1", "file.txt", "hash", 100, null!);

        var parsed = new NdaSignaling.ParsedMessage(nda);

        Assert.Equal("peer1", parsed.TargetPeerId);
        Assert.Equal("update", parsed.Action);
        Assert.Empty(parsed.Content);
    }
}
