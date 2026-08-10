using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using VelocityShare.Server;

namespace VelocityShare.Benchmarks;

/// <summary>
/// SHA-256 hashing: Rust FFI (zero-allocation) vs .NET managed implementation.
/// Measures throughput at various chunk sizes relevant to file transfer.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[HtmlExporter]
public class Sha256Benchmarks
{
    private byte[] _data64 = null!;
    private byte[] _data1K = null!;
    private byte[] _data4K = null!;
    private byte[] _data64K = null!;
    private byte[] _data1M = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = RandomNumberGenerator.Create();
        _data64 = new byte[64]; rng.GetBytes(_data64);
        _data1K = new byte[1024]; rng.GetBytes(_data1K);
        _data4K = new byte[4096]; rng.GetBytes(_data4K);
        _data64K = new byte[65536]; rng.GetBytes(_data64K);
        _data1M = new byte[1_048_576]; rng.GetBytes(_data1M);
    }

    // ── Rust FFI (zero-allocation path) ──

    [Benchmark(Baseline = true, Description = "Rust FFI SHA-256 (64B)")]
    public byte[] RustFfi_64B() => VelocityShareCrypto.HashChunk(_data64);

    [Benchmark(Description = "Rust FFI SHA-256 (1KB)")]
    public byte[] RustFfi_1K() => VelocityShareCrypto.HashChunk(_data1K);

    [Benchmark(Description = "Rust FFI SHA-256 (4KB)")]
    public byte[] RustFfi_4K() => VelocityShareCrypto.HashChunk(_data4K);

    [Benchmark(Description = "Rust FFI SHA-256 (64KB)")]
    public byte[] RustFfi_64K() => VelocityShareCrypto.HashChunk(_data64K);

    [Benchmark(Description = "Rust FFI SHA-256 (1MB)")]
    public byte[] RustFfi_1M() => VelocityShareCrypto.HashChunk(_data1M);

    // ── .NET managed SHA-256 (baseline comparison) ──

    [Benchmark(Description = ".NET SHA256.HashData (64B)")]
    public byte[] Managed_64B() => SHA256.HashData(_data64);

    [Benchmark(Description = ".NET SHA256.HashData (1KB)")]
    public byte[] Managed_1K() => SHA256.HashData(_data1K);

    [Benchmark(Description = ".NET SHA256.HashData (4KB)")]
    public byte[] Managed_4K() => SHA256.HashData(_data4K);

    [Benchmark(Description = ".NET SHA256.HashData (64KB)")]
    public byte[] Managed_64K() => SHA256.HashData(_data64K);

    [Benchmark(Description = ".NET SHA256.HashData (1MB)")]
    public byte[] Managed_1M() => SHA256.HashData(_data1M);

    // ── Span-based zero-alloc path (.NET) ──

    [Benchmark(Description = ".NET SHA256 Span (64KB, zero-alloc)")]
    public int ManagedSpan_64K()
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_data64K, hash);
        return hash[0]; // prevent optimization
    }

    // ── Rust FFI Span-based zero-alloc path ──

    [Benchmark(Description = "Rust FFI SHA-256 Span (64KB, zero-alloc)")]
    public int RustFfiSpan_64K()
    {
        Span<byte> hash = stackalloc byte[32];
        VelocityShareCrypto.HashChunk(_data64K, hash);
        return hash[0];
    }
}
