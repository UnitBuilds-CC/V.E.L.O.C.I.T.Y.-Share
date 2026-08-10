using VelocityShare.Server;

namespace VelocityShare.Tests;

public class PathValidationTests
{
    private readonly string _sandboxRoot;

    public PathValidationTests()
    {
        _sandboxRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "velocity_test_sandbox"));
        if (!Directory.Exists(_sandboxRoot))
            Directory.CreateDirectory(_sandboxRoot);
    }

    [Fact]
    public void IsPathInsideSandbox_ValidPath_ReturnsTrue()
    {
        var validPath = Path.Combine(_sandboxRoot, "subdir", "file.txt");
        Assert.True(PathValidation.IsPathInsideSandbox(validPath, _sandboxRoot));
    }

    [Fact]
    public void IsPathInsideSandbox_SandboxRootItself_ReturnsTrue()
    {
        Assert.True(PathValidation.IsPathInsideSandbox(_sandboxRoot, _sandboxRoot));
    }

    [Fact]
    public void IsPathInsideSandbox_PathWithTraversal_ReturnsFalse()
    {
        var evilPath = Path.Combine(_sandboxRoot, "..", "..", "Windows", "System32");
        Assert.False(PathValidation.IsPathInsideSandbox(evilPath, _sandboxRoot));
    }

    [Fact]
    public void IsPathInsideSandbox_OutsidePath_ReturnsFalse()
    {
        Assert.False(PathValidation.IsPathInsideSandbox("/etc/passwd", _sandboxRoot));
    }

    [Fact]
    public void IsPathInsideSandbox_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathValidation.IsPathInsideSandbox("", _sandboxRoot));
    }

    [Fact]
    public void IsPathInsideSandbox_NullPath_ReturnsFalse()
    {
        Assert.False(PathValidation.IsPathInsideSandbox(null!, _sandboxRoot));
    }

    [Fact]
    public void IsFileInsideSyncFolder_ValidRelativePath_ReturnsTrue()
    {
        var syncFolder = Path.Combine(Path.GetTempPath(), "velocity_sync_test");
        Directory.CreateDirectory(syncFolder);
        try
        {
            Assert.True(PathValidation.IsFileInsideSyncFolder("subdir/file.txt", syncFolder));
        }
        finally
        {
            Directory.Delete(syncFolder, true);
        }
    }

    [Fact]
    public void IsFileInsideSyncFolder_TraversalPath_ReturnsFalse()
    {
        var syncFolder = Path.Combine(Path.GetTempPath(), "velocity_sync_test2");
        Directory.CreateDirectory(syncFolder);
        try
        {
            Assert.False(PathValidation.IsFileInsideSyncFolder("../../../etc/passwd", syncFolder));
        }
        finally
        {
            Directory.Delete(syncFolder, true);
        }
    }

    [Fact]
    public void IsFileInsideSyncFolder_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathValidation.IsFileInsideSyncFolder("", "/tmp/sync"));
    }
}
