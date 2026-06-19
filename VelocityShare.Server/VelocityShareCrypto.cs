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
    }
}
