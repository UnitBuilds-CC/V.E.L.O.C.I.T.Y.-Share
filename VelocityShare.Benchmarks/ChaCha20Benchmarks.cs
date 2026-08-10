using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using VelocityShare.Server;

namespace VelocityShare.Benchmarks;

/// <summary>
/// ChaCha20-Poly1305 AEAD encryption/decryption: Rust FFI vs .NET managed.
/// Tests the critical path for file chunk encryption in transit.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[HtmlExporter]
public class ChaCha20Benchmarks
{
    private byte[] _key = null!;
    private byte[] _nonce = null!;
    private byte[] _plaintext1K = null!;
    private byte[] _plaintext64K = null!;
    private byte[] _plaintext1M = null!;
    private byte[] _ciphertext1K = null!;
    private byte[] _ciphertext64K = null!;
    private byte[] _ciphertext1M = null!;
    private byte[] _tag1K = null!;
    private byte[] _tag64K = null!;
    private byte[] _tag1M = null!;

    [GlobalSetup]
    public void Setup()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        _nonce = RandomNumberGenerator.GetBytes(12);

        _plaintext1K = RandomNumberGenerator.GetBytes(1024);
        _plaintext64K = RandomNumberGenerator.GetBytes(65536);
        _plaintext1M = RandomNumberGenerator.GetBytes(1_048_576);

        // Pre-encrypt for decryption benchmarks
        (_ciphertext1K, _tag1K) = VelocityShareCrypto.EncryptBlock(_plaintext1K, _key, _nonce);
        (_ciphertext64K, _tag64K) = VelocityShareCrypto.EncryptBlock(_plaintext64K, _key, _nonce);
        (_ciphertext1M, _tag1M) = VelocityShareCrypto.EncryptBlock(_plaintext1M, _key, _nonce);
    }

    // ── Rust FFI Encryption ──

    [Benchmark(Baseline = true, Description = "Rust FFI Encrypt (1KB)")]
    public (byte[], byte[]) RustFfi_Encrypt1K()
        => VelocityShareCrypto.EncryptBlock(_plaintext1K, _key, _nonce);

    [Benchmark(Description = "Rust FFI Encrypt (64KB)")]
    public (byte[], byte[]) RustFfi_Encrypt64K()
        => VelocityShareCrypto.EncryptBlock(_plaintext64K, _key, _nonce);

    [Benchmark(Description = "Rust FFI Encrypt (1MB)")]
    public (byte[], byte[]) RustFfi_Encrypt1M()
        => VelocityShareCrypto.EncryptBlock(_plaintext1M, _key, _nonce);

    // ── .NET Managed Encryption ──

    [Benchmark(Description = ".NET ChaCha20Poly1305 Encrypt (1KB)")]
    public (byte[], byte[]) Managed_Encrypt1K()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] ciphertext = new byte[_plaintext1K.Length];
        byte[] tag = new byte[16];
        aead.Encrypt(_nonce, _plaintext1K, ciphertext, tag);
        return (ciphertext, tag);
    }

    [Benchmark(Description = ".NET ChaCha20Poly1305 Encrypt (64KB)")]
    public (byte[], byte[]) Managed_Encrypt64K()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] ciphertext = new byte[_plaintext64K.Length];
        byte[] tag = new byte[16];
        aead.Encrypt(_nonce, _plaintext64K, ciphertext, tag);
        return (ciphertext, tag);
    }

    [Benchmark(Description = ".NET ChaCha20Poly1305 Encrypt (1MB)")]
    public (byte[], byte[]) Managed_Encrypt1M()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] ciphertext = new byte[_plaintext1M.Length];
        byte[] tag = new byte[16];
        aead.Encrypt(_nonce, _plaintext1M, ciphertext, tag);
        return (ciphertext, tag);
    }

    // ── Rust FFI Decryption ──

    [Benchmark(Description = "Rust FFI Decrypt (1KB)")]
    public byte[] RustFfi_Decrypt1K()
        => VelocityShareCrypto.DecryptBlock(_ciphertext1K, _key, _nonce, _tag1K);

    [Benchmark(Description = "Rust FFI Decrypt (64KB)")]
    public byte[] RustFfi_Decrypt64K()
        => VelocityShareCrypto.DecryptBlock(_ciphertext64K, _key, _nonce, _tag64K);

    [Benchmark(Description = "Rust FFI Decrypt (1MB)")]
    public byte[] RustFfi_Decrypt1M()
        => VelocityShareCrypto.DecryptBlock(_ciphertext1M, _key, _nonce, _tag1M);

    // ── .NET Managed Decryption ──

    [Benchmark(Description = ".NET ChaCha20Poly1305 Decrypt (1KB)")]
    public byte[] Managed_Decrypt1K()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] plaintext = new byte[_ciphertext1K.Length];
        aead.Decrypt(_nonce, _ciphertext1K, _tag1K, plaintext);
        return plaintext;
    }

    [Benchmark(Description = ".NET ChaCha20Poly1305 Decrypt (64KB)")]
    public byte[] Managed_Decrypt64K()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] plaintext = new byte[_ciphertext64K.Length];
        aead.Decrypt(_nonce, _ciphertext64K, _tag64K, plaintext);
        return plaintext;
    }

    [Benchmark(Description = ".NET ChaCha20Poly1305 Decrypt (1MB)")]
    public byte[] Managed_Decrypt1M()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] plaintext = new byte[_ciphertext1M.Length];
        aead.Decrypt(_nonce, _ciphertext1M, _tag1M, plaintext);
        return plaintext;
    }

    // ── Roundtrip (encrypt + decrypt) ──

    [Benchmark(Description = "Rust FFI Roundtrip (64KB)")]
    public byte[] RustFfi_Roundtrip64K()
    {
        var (ct, tag) = VelocityShareCrypto.EncryptBlock(_plaintext64K, _key, _nonce);
        return VelocityShareCrypto.DecryptBlock(ct, _key, _nonce, tag);
    }

    [Benchmark(Description = ".NET Roundtrip (64KB)")]
    public byte[] Managed_Roundtrip64K()
    {
        using var aead = new ChaCha20Poly1305(_key);
        byte[] ct = new byte[_plaintext64K.Length];
        byte[] tag = new byte[16];
        aead.Encrypt(_nonce, _plaintext64K, ct, tag);
        byte[] pt = new byte[ct.Length];
        aead.Decrypt(_nonce, ct, tag, pt);
        return pt;
    }
}
