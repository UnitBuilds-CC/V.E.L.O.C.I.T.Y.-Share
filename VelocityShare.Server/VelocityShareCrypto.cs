using System;
using System.Runtime.InteropServices;

namespace VelocityShare.Server
{
    public static unsafe class VelocityShareCrypto
    {
        private const string DllName = "velocity_share_ffi";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sha256_hash_chunk(byte* dataPtr, nuint dataLen, byte* hashOutPtr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int encrypt_block_chacha(byte* keyPtr, byte* noncePtr, byte* dataPtr, nuint dataLen, byte* tagOutPtr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int decrypt_block_chacha(byte* keyPtr, byte* noncePtr, byte* dataPtr, nuint dataLen, byte* tagPtr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bulk_hash_chunks(byte* chunksPtr, nuint totalLen, uint chunkCount, byte* hashesOutPtr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int verify_chunk_integrity(byte* dataPtr, nuint dataLen, byte* expectedHashPtr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pbkdf2_derive(byte* passwordPtr, nuint passwordLen, byte* saltPtr, nuint saltLen, uint iterations, byte* derivedKeyPtr, nuint outLen);
        
        public static byte[] HashChunk(ReadOnlySpan<byte> chunk)
        {
            byte[] hash = new byte[32];
            fixed (byte* pChunk = chunk, pHash = hash)
            {
                int res = sha256_hash_chunk(pChunk, (nuint)chunk.Length, pHash);
                if (res != 0) throw new InvalidOperationException($"SHA256 FFI failed with code {res}");
            }
            return hash;
        }

        public static int HashChunk(ReadOnlySpan<byte> chunk, Span<byte> destination)
        {
            if (destination.Length < 32) throw new ArgumentException("Destination span must be at least 32 bytes");
            fixed (byte* pChunk = chunk, pHash = destination)
            {
                return sha256_hash_chunk(pChunk, (nuint)chunk.Length, pHash);
            }
        }

        public static (byte[] Ciphertext, byte[] Tag) EncryptBlock(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes");
            if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes");

            byte[] ciphertext = plaintext.ToArray();
            byte[] tag = new byte[16];

            fixed (byte* pCipher = ciphertext, pKey = key, pNonce = nonce, pTag = tag)
            {
                int res = encrypt_block_chacha(pKey, pNonce, pCipher, (nuint)ciphertext.Length, pTag);
                if (res != 0) throw new InvalidOperationException($"ChaCha20 FFI encryption failed with code {res}");
            }

            return (ciphertext, tag);
        }

        public static int EncryptBlock(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> ciphertextDestination, Span<byte> tagDestination)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes");
            if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes");
            if (ciphertextDestination.Length < plaintext.Length) throw new ArgumentException("Ciphertext destination too small");
            if (tagDestination.Length < 16) throw new ArgumentException("Tag destination too small");

            fixed (byte* pPlain = plaintext)
            fixed (byte* pCipher = ciphertextDestination)
            {
                if (pPlain != pCipher)
                {
                    plaintext.CopyTo(ciphertextDestination);
                }
            }

            fixed (byte* pCipher = ciphertextDestination, pKey = key, pNonce = nonce, pTag = tagDestination)
            {
                return encrypt_block_chacha(pKey, pNonce, pCipher, (nuint)plaintext.Length, pTag);
            }
        }

        public static byte[] DecryptBlock(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes");
            if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes");
            if (tag.Length != 16) throw new ArgumentException("Tag must be 16 bytes");

            byte[] plaintext = ciphertext.ToArray();

            fixed (byte* pPlain = plaintext, pKey = key, pNonce = nonce, pTag = tag)
            {
                int res = decrypt_block_chacha(pKey, pNonce, pPlain, (nuint)ciphertext.Length, pTag);
                if (res != 0) throw new InvalidOperationException($"ChaCha20 FFI decryption failed with code {res}");
            }

            return plaintext;
        }

        public static int DecryptBlock(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag, Span<byte> plaintextDestination)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes");
            if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes");
            if (tag.Length != 16) throw new ArgumentException("Tag must be 16 bytes");
            if (plaintextDestination.Length < ciphertext.Length) throw new ArgumentException("Plaintext destination too small");

            fixed (byte* pCipher = ciphertext)
            fixed (byte* pPlain = plaintextDestination)
            {
                if (pCipher != pPlain)
                {
                    ciphertext.CopyTo(plaintextDestination);
                }
            }

            fixed (byte* pPlain = plaintextDestination, pKey = key, pNonce = nonce, pTag = tag)
            {
                return decrypt_block_chacha(pKey, pNonce, pPlain, (nuint)ciphertext.Length, pTag);
            }
        }

        // ── Bulk hash: hash multiple chunks in a single FFI call ──
        public static byte[][] BulkHashChunks(ReadOnlySpan<byte> chunkBuffer, uint chunkCount)
        {
            var hashes = new byte[chunkCount][];
            var hashBuffer = new byte[chunkCount * 32];

            fixed (byte* pChunks = chunkBuffer)
            fixed (byte* pHashes = hashBuffer)
            {
                int rc = bulk_hash_chunks(pChunks, (nuint)chunkBuffer.Length, chunkCount, pHashes);
                if (rc != 0) throw new InvalidOperationException($"Bulk hash FFI failed with code {rc}");
            }

            for (int i = 0; i < chunkCount; i++)
            {
                hashes[i] = new byte[32];
                System.Buffer.BlockCopy(hashBuffer, i * 32, hashes[i], 0, 32);
            }
            return hashes;
        }

        // ── Verify chunk integrity: hash + compare in one FFI call (constant-time) ──
        public static bool VerifyChunkIntegrity(ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> expectedHash)
        {
            if (expectedHash.Length != 32) throw new ArgumentException("Expected hash must be 32 bytes");

            fixed (byte* pData = chunk)
            fixed (byte* pHash = expectedHash)
            {
                int rc = verify_chunk_integrity(pData, (nuint)chunk.Length, pHash);
                if (rc < 0) throw new InvalidOperationException($"Integrity check FFI failed with code {rc}");
                return rc == 0;
            }
        }

        // ── PBKDF2 key derivation via Rust (zero-allocation in the crypto path) ──
        public static byte[] Pbkdf2Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, uint iterations, int outputLength = 32)
        {
            byte[] derived = new byte[outputLength];

            fixed (byte* pPwd = password)
            fixed (byte* pSalt = salt)
            fixed (byte* pOut = derived)
            {
                int rc = pbkdf2_derive(pPwd, (nuint)password.Length, pSalt, (nuint)salt.Length, iterations, pOut, (nuint)outputLength);
                if (rc != 0) throw new InvalidOperationException($"PBKDF2 FFI failed with code {rc}");
            }

            return derived;
        }
    }
}
