using System.Security.Cryptography;
using VelocityShare.Server;

namespace VelocityShare.Tests;

public class CryptoTests
{
    [Fact]
    public void HashChunk_ProducesCorrectLength()
    {
        byte[] data = "Hello, VelocityShare!"u8.ToArray();
        byte[] hash = VelocityShareCrypto.HashChunk(data);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void HashChunk_SameInput_ProducesSameHash()
    {
        byte[] data = "deterministic hash test"u8.ToArray();
        byte[] hash1 = VelocityShareCrypto.HashChunk(data);
        byte[] hash2 = VelocityShareCrypto.HashChunk(data);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashChunk_DifferentInput_ProducesDifferentHash()
    {
        byte[] data1 = "input one"u8.ToArray();
        byte[] data2 = "input two"u8.ToArray();
        byte[] hash1 = VelocityShareCrypto.HashChunk(data1);
        byte[] hash2 = VelocityShareCrypto.HashChunk(data2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashChunk_MatchesManagedSha256()
    {
        byte[] data = "cross-validation test data"u8.ToArray();
        byte[] ffiHash = VelocityShareCrypto.HashChunk(data);
        byte[] managedHash = SHA256.HashData(data);
        Assert.Equal(managedHash, ffiHash);
    }

    [Fact]
    public void EncryptDecrypt_Roundtrip_Succeeds()
    {
        byte[] plaintext = "Secret message for ChaCha20-Poly1305 roundtrip test!"u8.ToArray();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);

        var (ciphertext, tag) = VelocityShareCrypto.EncryptBlock(plaintext, key, nonce);
        byte[] decrypted = VelocityShareCrypto.DecryptBlock(ciphertext, key, nonce, tag);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptBlock_ProducesDifferentCiphertext()
    {
        byte[] plaintext = "encryption test"u8.ToArray();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);

        var (ciphertext, tag) = VelocityShareCrypto.EncryptBlock(plaintext, key, nonce);

        // Ciphertext should differ from plaintext (it's encrypted)
        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(16, tag.Length); // Poly1305 tag is 16 bytes
    }

    [Fact]
    public void DecryptBlock_WrongKey_Throws()
    {
        byte[] plaintext = "authenticated encryption test"u8.ToArray();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);

        var (ciphertext, tag) = VelocityShareCrypto.EncryptBlock(plaintext, key, nonce);

        // Try decrypting with a different key
        byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
        Assert.Throws<InvalidOperationException>(() =>
            VelocityShareCrypto.DecryptBlock(ciphertext, wrongKey, nonce, tag));
    }

    [Fact]
    public void DecryptBlock_TamperedTag_Throws()
    {
        byte[] plaintext = "tamper detection test"u8.ToArray();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);

        var (ciphertext, tag) = VelocityShareCrypto.EncryptBlock(plaintext, key, nonce);

        // Tamper with the tag
        tag[0] ^= 0xFF;
        Assert.Throws<InvalidOperationException>(() =>
            VelocityShareCrypto.DecryptBlock(ciphertext, key, nonce, tag));
    }

    [Fact]
    public void EncryptBlock_InvalidKeyLength_Throws()
    {
        byte[] plaintext = "test"u8.ToArray();
        byte[] badKey = new byte[16]; // Should be 32
        byte[] nonce = RandomNumberGenerator.GetBytes(12);

        Assert.Throws<ArgumentException>(() =>
            VelocityShareCrypto.EncryptBlock(plaintext, badKey, nonce));
    }

    [Fact]
    public void EncryptBlock_InvalidNonceLength_Throws()
    {
        byte[] plaintext = "test"u8.ToArray();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] badNonce = new byte[8]; // Should be 12

        Assert.Throws<ArgumentException>(() =>
            VelocityShareCrypto.EncryptBlock(plaintext, key, badNonce));
    }

    [Fact]
    public void HashChunk_EmptyInput_ThrowsFromFFI()
    {
        // The Rust FFI rejects zero-length input (returns -1)
        byte[] empty = Array.Empty<byte>();
        Assert.Throws<InvalidOperationException>(() => VelocityShareCrypto.HashChunk(empty));
    }

    // ── Bulk Hash Tests ──

    [Fact]
    public void BulkHashChunks_MatchesIndividualHashes()
    {
        byte[] chunk1 = "first chunk data"u8.ToArray();
        byte[] chunk2 = "second chunk data"u8.ToArray();

        // Build length-prefixed buffer
        var buffer = new byte[4 + chunk1.Length + 4 + chunk2.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), chunk1.Length);
        chunk1.CopyTo(buffer.AsSpan(4));
        BitConverter.TryWriteBytes(buffer.AsSpan(4 + chunk1.Length, 4), chunk2.Length);
        chunk2.CopyTo(buffer.AsSpan(4 + chunk1.Length + 4));

        var bulkHashes = VelocityShareCrypto.BulkHashChunks(buffer, 2);
        var hash1 = VelocityShareCrypto.HashChunk(chunk1);
        var hash2 = VelocityShareCrypto.HashChunk(chunk2);

        Assert.Equal(hash1, bulkHashes[0]);
        Assert.Equal(hash2, bulkHashes[1]);
    }

    // ── Verify Chunk Integrity Tests ──

    [Fact]
    public void VerifyChunkIntegrity_CorrectHash_ReturnsTrue()
    {
        byte[] data = "integrity check test data"u8.ToArray();
        byte[] hash = VelocityShareCrypto.HashChunk(data);
        Assert.True(VelocityShareCrypto.VerifyChunkIntegrity(data, hash));
    }

    [Fact]
    public void VerifyChunkIntegrity_WrongHash_ReturnsFalse()
    {
        byte[] data = "integrity check test data"u8.ToArray();
        byte[] hash = VelocityShareCrypto.HashChunk(data);
        hash[0] ^= 0xFF; // corrupt
        Assert.False(VelocityShareCrypto.VerifyChunkIntegrity(data, hash));
    }

    // ── PBKDF2 Tests ──

    [Fact]
    public void Pbkdf2Derive_ProducesCorrectLength()
    {
        byte[] password = "test-password"u8.ToArray();
        byte[] salt = "test-salt-123456"u8.ToArray();
        byte[] derived = VelocityShareCrypto.Pbkdf2Derive(password, salt, 1000, 32);
        Assert.Equal(32, derived.Length);
    }

    [Fact]
    public void Pbkdf2Derive_SameInput_ProducesSameOutput()
    {
        byte[] password = "deterministic-test"u8.ToArray();
        byte[] salt = "fixed-salt-value"u8.ToArray();
        byte[] d1 = VelocityShareCrypto.Pbkdf2Derive(password, salt, 1000, 32);
        byte[] d2 = VelocityShareCrypto.Pbkdf2Derive(password, salt, 1000, 32);
        Assert.Equal(d1, d2);
    }

    [Fact]
    public void Pbkdf2Derive_DifferentPassword_ProducesDifferentOutput()
    {
        byte[] salt = "shared-salt"u8.ToArray();
        byte[] d1 = VelocityShareCrypto.Pbkdf2Derive("password1"u8.ToArray(), salt, 1000, 32);
        byte[] d2 = VelocityShareCrypto.Pbkdf2Derive("password2"u8.ToArray(), salt, 1000, 32);
        Assert.NotEqual(d1, d2);
    }
}
