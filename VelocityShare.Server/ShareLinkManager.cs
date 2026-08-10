using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace VelocityShare.Server;

/// <summary>
/// Manages time-limited, optionally password-protected share links.
/// Share links allow anyone with the URL (and password) to download a file.
/// Includes brute-force protection: 5 failed password attempts locks the link for 15 minutes.
/// </summary>
public class ShareLinkManager
{
    private readonly ConcurrentDictionary<string, ShareLink> _links = new();
    private readonly ConcurrentDictionary<string, DownloadToken> _downloadTokens = new();

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DownloadTokenExpiry = TimeSpan.FromMinutes(2);

    public record ShareLink(
        string Id,
        string FileId,
        string FileName,
        long FileSize,
        string? PasswordHash,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        int MaxDownloads,
        int DownloadCount,
        int FailedAttempts = 0,
        DateTime? LockedUntil = null
    );

    private record DownloadToken(string ShareLinkId, DateTime ExpiresAt);

    /// <summary>
    /// Creates a new share link for a file.
    /// </summary>
    public ShareLink CreateLink(string fileId, string fileName, long fileSize, TimeSpan expiry, string? password = null, int maxDownloads = 100)
    {
        // Generate crypto-secure share ID (12 chars, URL-safe)
        var idBytes = new byte[9];
        RandomNumberGenerator.Fill(idBytes);
        var id = Convert.ToBase64String(idBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

        string? passwordHash = null;
        if (!string.IsNullOrEmpty(password))
        {
            // Use Rust FFI PBKDF2 for zero-allocation password hashing
            var salt = RandomNumberGenerator.GetBytes(16);
            var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
            var hash = VelocityShareCrypto.Pbkdf2Derive(passwordBytes, salt, 100_000, 32);
            // Store as: salt(16 bytes) + hash(32 bytes) = base64
            var combined = new byte[48];
            Buffer.BlockCopy(salt, 0, combined, 0, 16);
            Buffer.BlockCopy(hash, 0, combined, 16, 32);
            passwordHash = Convert.ToBase64String(combined);
        }

        var link = new ShareLink(id, fileId, fileName, fileSize, passwordHash, DateTime.UtcNow, DateTime.UtcNow + expiry, maxDownloads, 0);
        _links[id] = link;
        return link;
    }

    /// <summary>
    /// Validates a share link. Returns null if invalid, expired, locked out, or password mismatch.
    /// Tracks failed password attempts and locks the link after too many failures.
    /// </summary>
    public ShareLink? ValidateLink(string id, string? password = null)
    {
        if (!_links.TryGetValue(id, out var link))
            return null;

        if (DateTime.UtcNow > link.ExpiresAt)
        {
            _links.TryRemove(id, out _);
            return null;
        }

        if (link.DownloadCount >= link.MaxDownloads)
        {
            _links.TryRemove(id, out _);
            return null;
        }

        // Check brute-force lockout
        if (link.LockedUntil.HasValue && DateTime.UtcNow < link.LockedUntil.Value)
            return null; // Link is temporarily locked due to too many failed attempts

        // If locked out period has passed, reset the failed counter
        if (link.LockedUntil.HasValue && DateTime.UtcNow >= link.LockedUntil.Value)
        {
            link = link with { FailedAttempts = 0, LockedUntil = null };
            _links[id] = link;
        }

        // Verify password if protected
        if (link.PasswordHash != null)
        {
            if (string.IsNullOrEmpty(password))
                return null; // Password required but not provided

            var combined = Convert.FromBase64String(link.PasswordHash);
            var salt = new byte[16];
            Buffer.BlockCopy(combined, 0, salt, 0, 16);

            var testHash = VelocityShareCrypto.Pbkdf2Derive(System.Text.Encoding.UTF8.GetBytes(password), salt, 100_000, 32);
            var storedHash = new byte[32];
            Buffer.BlockCopy(combined, 16, storedHash, 0, 32);

            if (!CryptographicOperations.FixedTimeEquals(testHash, storedHash))
            {
                // Record failed attempt
                var updated = link with { FailedAttempts = link.FailedAttempts + 1 };
                if (updated.FailedAttempts >= MaxFailedAttempts)
                {
                    updated = updated with { LockedUntil = DateTime.UtcNow + LockoutDuration };
                }
                _links[id] = updated;
                return null; // Wrong password
            }

            // Successful password verification resets failed counter
            if (link.FailedAttempts > 0)
            {
                _links[id] = link with { FailedAttempts = 0, LockedUntil = null };
            }
        }

        return link;
    }

    /// <summary>
    /// Generates a short-lived one-time download token for a validated share link.
    /// Used to avoid passing passwords in query strings.
    /// </summary>
    public string IssueDownloadToken(string shareLinkId)
    {
        var tokenBytes = new byte[16];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        _downloadTokens[token] = new DownloadToken(shareLinkId, DateTime.UtcNow + DownloadTokenExpiry);
        return token;
    }

    /// <summary>
    /// Validates and consumes a one-time download token.
    /// Returns the share link ID if valid, null otherwise.
    /// </summary>
    public string? ConsumeDownloadToken(string token)
    {
        if (!_downloadTokens.TryRemove(token, out var dt))
            return null;
        if (DateTime.UtcNow > dt.ExpiresAt)
            return null;
        return dt.ShareLinkId;
    }

    /// <summary>
    /// Increments download count atomically. Removes link if max downloads reached.
    /// Uses optimistic concurrency loop to prevent lost updates under contention.
    /// </summary>
    public void RecordDownload(string id)
    {
        while (_links.TryGetValue(id, out var link))
        {
            var updated = link with { DownloadCount = link.DownloadCount + 1 };
            // Atomic CAS: only succeeds if no other thread updated between our read and write
            if (_links.TryUpdate(id, updated, link))
            {
                if (updated.DownloadCount >= updated.MaxDownloads)
                {
                    _links.TryRemove(id, out _);
                }
                return;
            }
            // CAS failed — another thread updated; retry with fresh value
        }
    }

    /// <summary>
    /// Cleans up expired links and stale download tokens. Call periodically.
    /// </summary>
    public int CleanupExpired()
    {
        int removed = 0;
        foreach (var kvp in _links)
        {
            if (DateTime.UtcNow > kvp.Value.ExpiresAt || kvp.Value.DownloadCount >= kvp.Value.MaxDownloads)
            {
                if (_links.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }
        // Clean up expired download tokens
        foreach (var kvp in _downloadTokens)
        {
            if (DateTime.UtcNow > kvp.Value.ExpiresAt)
            {
                _downloadTokens.TryRemove(kvp.Key, out _);
            }
        }
        return removed;
    }

    /// <summary>
    /// Gets the number of active share links.
    /// </summary>
    public int ActiveLinkCount => _links.Count;
}
