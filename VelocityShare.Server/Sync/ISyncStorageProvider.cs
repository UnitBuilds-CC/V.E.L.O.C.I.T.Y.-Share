using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Abstracts file storage for the sync engine.
/// Implementations: local disk, S3, Azure Blob, Google Drive.
/// </summary>
public interface ISyncStorageProvider : IAsyncDisposable
{
    /// <summary>Provider type identifier (e.g., "local", "s3", "azure").</summary>
    string ProviderType { get; }

    /// <summary>Read a file's content as bytes.</summary>
    Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Read a specific block of a file (offset + length).</summary>
    Task<byte[]> ReadFileBlockAsync(string relativePath, long offset, int length, CancellationToken ct = default);

    /// <summary>Write bytes to a file (creates or overwrites).</summary>
    Task WriteFileAsync(string relativePath, byte[] content, CancellationToken ct = default);

    /// <summary>Write a specific block of a file.</summary>
    Task WriteFileBlockAsync(string relativePath, long offset, byte[] blockData, CancellationToken ct = default);

    /// <summary>Delete a file.</summary>
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Check if a file exists.</summary>
    Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Get file size in bytes.</summary>
    Task<long> GetFileSizeAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Get the last-modified timestamp of a file.</summary>
    Task<DateTimeOffset> GetLastModifiedAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Enumerate all files in the storage with their relative paths.</summary>
    IAsyncEnumerable<string> EnumerateFilesAsync(CancellationToken ct = default);

    /// <summary>Ensure a directory path exists.</summary>
    Task EnsureDirectoryAsync(string relativeDirPath, CancellationToken ct = default);
}
