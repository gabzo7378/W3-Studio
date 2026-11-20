using System;
using System.IO;

namespace W3Studio.Services;

/// <summary>
/// Provides crash-safe atomic file writing via a same-directory temporary file,
/// disk flush, optional .bak backup after successful write, and atomic rename.
/// </summary>
public static class AtomicFileHelper
{
    public static void WriteAtomically(
        string targetPath,
        Action<FileStream> writeAction,
        bool createBackup = false)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        if (writeAction == null)
            throw new ArgumentNullException(nameof(writeAction));

        var fullTarget = Path.GetFullPath(targetPath);
        var dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{fullTarget}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var tempFs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                writeAction(tempFs);
                tempFs.Flush(flushToDisk: true);
            }

            if (createBackup && File.Exists(fullTarget))
            {
                var backupPath = fullTarget + ".bak";
                File.Copy(fullTarget, backupPath, overwrite: true);
            }

            File.Move(tempPath, fullTarget, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("AtomicFileHelper", $"Atomic write failed for '{fullTarget}'; preserving existing file.", ex);
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception cleanupEx)
        {
            AppLogger.Warn("AtomicFileHelper", $"Could not delete temporary file '{tempPath}'.", cleanupEx);
        }
    }
}

