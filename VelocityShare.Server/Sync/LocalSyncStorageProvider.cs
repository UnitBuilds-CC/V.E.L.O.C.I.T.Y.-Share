using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server.Sync;

/// <summary>
/// Local filesystem storage provider. Wraps a root directory and
/// maps relative paths to absolute paths within that directory.
/// </summary>
public sealed class LocalSyncStorageProvider : ISyncStorageProvider
{
    private readonly string _rootPath;

    public string ProviderType => "local";

    public LocalSyncStorageProvider(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(_rootPath))
            Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    private string ResolvePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        // Sandbox: ensure the resolved path is within root
        var canonicalRoot = _rootPath.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path escapes sync root: {relativePath}");
        return fullPath;
    }

    public Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default)
        => File.ReadAllBytesAsync(ResolvePath(relativePath), ct);

    public async Task<byte[]> ReadFileBlockAsync(string relativePath, long offset, int length, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativePath);
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
            if (read == 0) break; // EOF
            totalRead += read;
        }
        if (totalRead < length)
            Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    public Task WriteFileAsync(string relativePath, byte[] content, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return File.WriteAllBytesAsync(fullPath, content, ct);
    }

    public async Task WriteFileBlockAsync(string relativePath, long offset, byte[] blockData, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativePath);
        using var fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, true);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(blockData, ct);
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
        => Task.FromResult(File.Exists(ResolvePath(relativePath)));

    public Task<long> GetFileSizeAsync(string relativePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(ResolvePath(relativePath));
        return Task.FromResult(fi.Exists ? fi.Length : -1L);
    }

    public Task<DateTimeOffset> GetLastModifiedAsync(string relativePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(ResolvePath(relativePath));
        return Task.FromResult(fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) : DateTimeOffset.MinValue);
    }

    public async IAsyncEnumerable<string> EnumerateFilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in Directory.EnumerateFiles(_rootPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.System
        }))
        {
            ct.ThrowIfCancellationRequested();
            yield return Path.GetRelativePath(_rootPath, file);
        }
        await Task.CompletedTask;
    }

    public Task EnsureDirectoryAsync(string relativeDirPath, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativeDirPath);
        if (!Directory.Exists(fullPath))
            Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
