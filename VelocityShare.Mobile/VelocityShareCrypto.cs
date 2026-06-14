using System;
using System.Runtime.InteropServices;

namespace VelocityShare.Mobile
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

        public static byte[] DecryptBlock(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes");
            if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes");
            if (tag.Length != 16) throw new ArgumentException("Tag must be 16 bytes");

            byte[] plaintext = ciphertext.ToArray();

            fixed (byte* pPlain = plaintext, pKey = key, pNonce = nonce, pTag = tag)
            {
                int res = decrypt_block_chacha(pKey, pNonce, pPlain, (nuint)plaintext.Length, pTag);
                if (res != 0) throw new InvalidOperationException($"ChaCha20 FFI decryption failed with code {res}");
            }

            return plaintext;
        }
    }
}
