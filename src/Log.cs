using System;
using System.IO;
using System.Text;

namespace OmniTube;

/// <summary>Plain text log in %APPDATA%\OmniTube\omnitube.log (overwritten on each start).</summary>
internal static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter _writer;

    public static void Init(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            _writer = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", message + ": " + ex);

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            try
            {
                _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}");
            }
            catch
            {
                // logging must never crash the app
            }
        }
    }
}
