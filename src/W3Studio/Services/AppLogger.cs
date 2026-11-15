using System;
using System.IO;
using System.Text;

namespace W3Studio.Services;

/// <summary>
/// Thread-safe lightweight diagnostic logger for W3 Studio runtime, MPQ/compiler errors, and tests.
/// </summary>
public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private const long MaxLogBytes = 2 * 1024 * 1024;

    public static string DefaultLogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "W3Studio",
        "logs",
        "w3studio.log");

    public static void Info(string source, string message) => Write("INFO", source, message, null);

    public static void Warn(string source, string message, Exception? ex = null) => Write("WARN", source, message, ex);

    public static void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);

    public static void Write(string level, string source, string message, Exception? ex = null, string? customLogPath = null)
    {
        var path = string.IsNullOrWhiteSpace(customLogPath) ? DefaultLogFilePath : customLogPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            lock (SyncRoot)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    var backup = path + ".1";
                    if (File.Exists(backup))
                        File.Delete(backup);
                    File.Move(path, backup);
                }

                var sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                sb.Append('[').Append(level).Append("] ");
                sb.Append('[').Append(source).Append("] ");
                sb.AppendLine(message);
                if (ex != null)
                    sb.AppendLine(ex.ToString());

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw out of diagnostics logging
        }
    }
}

