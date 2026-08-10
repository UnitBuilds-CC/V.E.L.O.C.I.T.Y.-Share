using System;
using System.IO;

namespace VelocityShare.Server
{
    /// <summary>
    /// Path validation utilities for sandbox enforcement and traversal prevention.
    /// </summary>
    public static class PathValidation
    {
        /// <summary>
        /// Checks whether the given path resolves to a location inside the sandbox directory.
        /// Rejects empty paths and paths containing ".." traversal sequences.
        /// </summary>
        public static bool IsPathInsideSandbox(string path, string sandbox)
        {
            if (string.IsNullOrEmpty(path) || path.Contains("..")) return false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullSandbox = Path.GetFullPath(sandbox);
                string separator = Path.DirectorySeparatorChar.ToString();
                if (!fullSandbox.EndsWith(separator))
                {
                    fullSandbox += separator;
                }
                return fullPath.Equals(Path.GetFullPath(sandbox), StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullSandbox, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks whether a relative file path stays inside the sync folder root.
        /// Used by the sync engine to prevent path traversal via crafted relative paths.
        /// </summary>
        public static bool IsFileInsideSyncFolder(string relativePath, string syncFolder)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.Contains("..")) return false;
            try
            {
                string combined = Path.GetFullPath(Path.Combine(syncFolder, relativePath));
                string canonical = Path.GetFullPath(syncFolder);
                string separator = Path.DirectorySeparatorChar.ToString();
                if (!canonical.EndsWith(separator)) canonical += separator;
                return combined.StartsWith(canonical, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
