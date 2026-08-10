using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using VelocityShare.Server;

namespace VelocityShare.Benchmarks;

/// <summary>
/// PBKDF2 key derivation: Rust FFI vs .NET managed.
/// Relevant for share link password hashing (100K iterations).
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[HtmlExporter]
public class Pbkdf2Benchmarks
{
    private byte[] _password = null!;
    private byte[] _salt = null!;
    private string _passwordStr = "share-link-password-test-12345";

    [GlobalSetup]
    public void Setup()
    {
        _password = System.Text.Encoding.UTF8.GetBytes(_passwordStr);
        _salt = RandomNumberGenerator.GetBytes(16);
    }

    // ── Rust FFI PBKDF2 ──

    [Benchmark(Baseline = true, Description = "Rust FFI PBKDF2 (10K iter)")]
    public byte[] RustFfi_10K()
        => VelocityShareCrypto.Pbkdf2Derive(_password, _salt, 10_000, 32);

    [Benchmark(Description = "Rust FFI PBKDF2 (100K iter)")]
    public byte[] RustFfi_100K()
        => VelocityShareCrypto.Pbkdf2Derive(_password, _salt, 100_000, 32);

    [Benchmark(Description = "Rust FFI PBKDF2 (600K iter)")]
    public byte[] RustFfi_600K()
        => VelocityShareCrypto.Pbkdf2Derive(_password, _salt, 600_000, 32);

    // ── .NET Managed PBKDF2 ──

    [Benchmark(Description = ".NET PBKDF2 (10K iter)")]
    public byte[] Managed_10K()
        => Rfc2898DeriveBytes.Pbkdf2(_passwordStr, _salt, 10_000, HashAlgorithmName.SHA256, 32);

    [Benchmark(Description = ".NET PBKDF2 (100K iter)")]
    public byte[] Managed_100K()
        => Rfc2898DeriveBytes.Pbkdf2(_passwordStr, _salt, 100_000, HashAlgorithmName.SHA256, 32);

    [Benchmark(Description = ".NET PBKDF2 (600K iter)")]
    public byte[] Managed_600K()
        => Rfc2898DeriveBytes.Pbkdf2(_passwordStr, _salt, 600_000, HashAlgorithmName.SHA256, 32);
}

/// <summary>
/// Bulk operations: batch hashing, integrity verification.
/// Demonstrates the advantage of single-FFI-call patterns.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[HtmlExporter]
public class BulkOperationBenchmarks
{
    private byte[] _singleChunk = null!;
    private byte[] _expectedHash = null!;
    private byte[] _bulkBuffer = null!;
    private const int ChunkSize = 65536; // 64KB

    [GlobalSetup]
    public void Setup()
    {
        _singleChunk = RandomNumberGenerator.GetBytes(ChunkSize);
        _expectedHash = VelocityShareCrypto.HashChunk(_singleChunk);

        // Build a bulk buffer with 16 length-prefixed 64KB chunks
        int count = 16;
        _bulkBuffer = new byte[count * (4 + ChunkSize)];
        for (int i = 0; i < count; i++)
        {
            int offset = i * (4 + ChunkSize);
            // Length prefix (little-endian)
            _bulkBuffer[offset] = (byte)(ChunkSize & 0xFF);
            _bulkBuffer[offset + 1] = (byte)((ChunkSize >> 8) & 0xFF);
            _bulkBuffer[offset + 2] = (byte)((ChunkSize >> 16) & 0xFF);
            _bulkBuffer[offset + 3] = (byte)((ChunkSize >> 24) & 0xFF);
            // Chunk data
            RandomNumberGenerator.Fill(_bulkBuffer.AsSpan(offset + 4, ChunkSize));
        }
    }

    // ── Bulk hash: single FFI call for all chunks ──

    [Benchmark(Baseline = true, Description = "Rust BulkHash (16x64KB, 1 FFI call)")]
    public byte[][] BulkHash_SingleCall()
        => VelocityShareCrypto.BulkHashChunks(_bulkBuffer, 16);

    // ── Individual hash: 16 separate FFI calls ──

    [Benchmark(Description = "Rust Individual Hash (16x64KB, 16 FFI calls)")]
    public byte[][] IndividualHash_16Calls()
    {
        var hashes = new byte[16][];
        for (int i = 0; i < 16; i++)
        {
            int offset = i * (4 + ChunkSize) + 4;
            hashes[i] = VelocityShareCrypto.HashChunk(_bulkBuffer.AsSpan(offset, ChunkSize));
        }
        return hashes;
    }

    // ── .NET managed individual hash ──

    [Benchmark(Description = ".NET SHA256 (16x64KB, 16 calls)")]
    public byte[][] ManagedHash_16Calls()
    {
        var hashes = new byte[16][];
        for (int i = 0; i < 16; i++)
        {
            int offset = i * (4 + ChunkSize) + 4;
            hashes[i] = SHA256.HashData(_bulkBuffer.AsSpan(offset, ChunkSize));
        }
        return hashes;
    }

    // ── Verify integrity: hash + compare in one FFI call ──

    [Benchmark(Description = "Rust VerifyIntegrity (hash+compare, 1 call)")]
    public bool VerifyIntegrity_SingleCall()
        => VelocityShareCrypto.VerifyChunkIntegrity(_singleChunk, _expectedHash);

    // ── Verify integrity: separate hash + compare ──

    [Benchmark(Description = ".NET VerifyIntegrity (hash + FixedTimeEquals)")]
    public bool VerifyIntegrity_Separate()
    {
        var hash = SHA256.HashData(_singleChunk);
        return CryptographicOperations.FixedTimeEquals(hash, _expectedHash);
    }
}
