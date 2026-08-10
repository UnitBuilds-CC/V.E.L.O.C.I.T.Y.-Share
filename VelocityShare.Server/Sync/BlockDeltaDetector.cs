using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Detects changed blocks between two versions of a file.
/// Uses fixed-size blocks (default 64KB) and SHA-256 hashing via Rust FFI.
/// </summary>
public sealed class BlockDeltaDetector
{
    public const int DefaultBlockSize = 64 * 1024; // 64KB blocks

    private readonly int _blockSize;

    public BlockDeltaDetector(int blockSize = DefaultBlockSize)
    {
        _blockSize = blockSize;
    }

    /// <summary>
    /// Represents a block that needs to be transferred.
    /// </summary>
    public sealed record BlockDelta(int BlockIndex, long Offset, int Length, byte[] Hash);

    /// <summary>
    /// Represents the full diff between local and remote file versions.
    /// </summary>
    public sealed class FileDiff
    {
        public string RelativePath { get; init; } = "";
        public long LocalSize { get; init; }
        public long RemoteSize { get; init; }
        public List<BlockDelta> ChangedBlocks { get; init; } = new();
        public bool IsFullReplace { get; init; }
        public int TotalBlocks { get; init; }
        public int ChangedBlockCount => ChangedBlocks.Count;
        public long BytesToTransfer { get; init; }
    }

    /// <summary>
    /// Compute block hashes for a file using the storage provider.
    /// </summary>
    public async Task<Dictionary<int, byte[]>> ComputeBlockHashesAsync(
        ISyncStorageProvider storage, string relativePath, long fileSize, CancellationToken ct = default)
    {
        var hashes = new Dictionary<int, byte[]>();
        if (fileSize <= 0) return hashes;

        int totalBlocks = (int)((fileSize + _blockSize - 1) / _blockSize);
        for (int i = 0; i < totalBlocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = (long)i * _blockSize;
            int length = (int)Math.Min(_blockSize, fileSize - offset);
            byte[] block = await storage.ReadFileBlockAsync(relativePath, offset, length, ct);
            byte[] hash = VelocityShareCrypto.HashChunk(block);
            hashes[i] = hash;
        }
        return hashes;
    }

    /// <summary>
    /// Compare local and remote block hashes, return list of changed blocks.
    /// </summary>
    public FileDiff ComputeDiff(
        string relativePath,
        long localSize,
        long remoteSize,
        Dictionary<int, byte[]> localHashes,
        Dictionary<int, byte[]> remoteHashes)
    {
        int localBlocks = localSize > 0 ? (int)((localSize + _blockSize - 1) / _blockSize) : 0;
        int remoteBlocks = remoteSize > 0 ? (int)((remoteSize + _blockSize - 1) / _blockSize) : 0;
        int maxBlocks = Math.Max(localBlocks, remoteBlocks);

        // If sizes differ significantly (>2x), just do full replace
        if (localSize > 0 && remoteSize > 0 &&
            (localSize > remoteSize * 2 || remoteSize > localSize * 2))
        {
            return new FileDiff
            {
                RelativePath = relativePath,
                LocalSize = localSize,
                RemoteSize = remoteSize,
                IsFullReplace = true,
                TotalBlocks = localBlocks,
                ChangedBlocks = new List<BlockDelta>(),
                BytesToTransfer = localSize
            };
        }

        var changed = new List<BlockDelta>();
        long bytesToTransfer = 0;

        for (int i = 0; i < maxBlocks; i++)
        {
            bool localHas = localHashes.TryGetValue(i, out var localHash);
            bool remoteHas = remoteHashes.TryGetValue(i, out var remoteHash);

            if (!localHas || !remoteHas || !HashesEqual(localHash!, remoteHash!))
            {
                // Block is new, deleted, or changed
                if (i < localBlocks)
                {
                    long offset = (long)i * _blockSize;
                    int length = (int)Math.Min(_blockSize, localSize - offset);
                    changed.Add(new BlockDelta(i, offset, length, localHashes[i]));
                    bytesToTransfer += length;
                }
            }
        }

        bool fullReplace = changed.Count == maxBlocks && maxBlocks > 0;

        return new FileDiff
        {
            RelativePath = relativePath,
            LocalSize = localSize,
            RemoteSize = remoteSize,
            IsFullReplace = fullReplace,
            TotalBlocks = localBlocks,
            ChangedBlocks = changed,
            BytesToTransfer = bytesToTransfer
        };
    }

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
