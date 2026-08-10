using VelocityShare.Server;

namespace VelocityShare.Tests;

public class ShareLinkSecurityTests
{
    // ── Brute-Force Protection Tests ──

    [Fact]
    public void BruteForce_LocksAfter5FailedAttempts()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), password: "correct-password");

        // 5 wrong password attempts should lock the link
        for (int i = 0; i < 5; i++)
        {
            var result = manager.ValidateLink(link.Id, "wrong-password");
            Assert.Null(result);
        }

        // Even the correct password should be rejected while locked
        var lockedResult = manager.ValidateLink(link.Id, "correct-password");
        Assert.Null(lockedResult);
    }

    [Fact]
    public void BruteForce_CorrectPasswordBeforeLockout_Succeeds()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), password: "my-password");

        // 4 wrong attempts (below threshold)
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(manager.ValidateLink(link.Id, "wrong"));
        }

        // Correct password should still work
        var result = manager.ValidateLink(link.Id, "my-password");
        Assert.NotNull(result);
        Assert.Equal(link.Id, result.Id);
    }

    [Fact]
    public void BruteForce_SuccessfulLogin_ResetsFailedCounter()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), password: "correct");

        // 3 wrong attempts
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(manager.ValidateLink(link.Id, "wrong"));
        }

        // Correct password resets counter
        Assert.NotNull(manager.ValidateLink(link.Id, "correct"));

        // Now 5 more wrong attempts should trigger lockout (counter was reset)
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(manager.ValidateLink(link.Id, "wrong"));
        }

        // Should be locked now
        Assert.Null(manager.ValidateLink(link.Id, "correct"));
    }

    // ── Download Token Tests ──

    [Fact]
    public void DownloadToken_IssueAndConsume_Succeeds()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1));

        var token = manager.IssueDownloadToken(link.Id);
        Assert.False(string.IsNullOrEmpty(token));

        var consumedLinkId = manager.ConsumeDownloadToken(token);
        Assert.Equal(link.Id, consumedLinkId);
    }

    [Fact]
    public void DownloadToken_OneTimeUse_SecondConsumeFails()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1));

        var token = manager.IssueDownloadToken(link.Id);

        // First consume succeeds
        Assert.Equal(link.Id, manager.ConsumeDownloadToken(token));

        // Second consume fails (token already consumed)
        Assert.Null(manager.ConsumeDownloadToken(token));
    }

    [Fact]
    public void DownloadToken_InvalidToken_ReturnsNull()
    {
        var manager = new ShareLinkManager();
        Assert.Null(manager.ConsumeDownloadToken("nonexistent-token"));
    }

    [Fact]
    public void DownloadToken_DifferentTokensAreUnique()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1));

        var token1 = manager.IssueDownloadToken(link.Id);
        var token2 = manager.IssueDownloadToken(link.Id);

        Assert.NotEqual(token1, token2);
    }

    // ── Share Link Basic Tests ──

    [Fact]
    public void CreateLink_NoPassword_ValidatesWithoutPassword()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1));

        var result = manager.ValidateLink(link.Id);
        Assert.NotNull(result);
        Assert.Null(result.PasswordHash);
    }

    [Fact]
    public void CreateLink_WithPassword_RequiresPassword()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), password: "secret");

        // Without password should fail
        Assert.Null(manager.ValidateLink(link.Id));

        // With correct password should succeed
        Assert.NotNull(manager.ValidateLink(link.Id, "secret"));
    }

    [Fact]
    public void ExpiredLink_ReturnsNull()
    {
        var manager = new ShareLinkManager();
        // Create a link that expires immediately (negative timespan not allowed, use minimal)
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromTicks(1));

        // Wait a tiny bit for expiry
        Thread.Sleep(10);

        Assert.Null(manager.ValidateLink(link.Id));
    }

    [Fact]
    public void RecordDownload_IncrementsCount()
    {
        var manager = new ShareLinkManager();
        var link = manager.CreateLink("file1", "test.txt", 1024, TimeSpan.FromHours(1), maxDownloads: 3);

        manager.RecordDownload(link.Id);
        manager.RecordDownload(link.Id);

        var result = manager.ValidateLink(link.Id);
        Assert.NotNull(result);
        Assert.Equal(2, result.DownloadCount);
    }
}
