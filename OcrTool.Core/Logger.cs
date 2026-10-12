using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OcrTool.Core;

public static class Logger
{
    private static readonly string _logPath;
    private static readonly object _lock = new();
    private static bool _sessionStarted;

    static Logger()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDir = Path.Combine(localAppData, "OcrTool");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "OcrTool.log");
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";

        lock (_lock)
        {
            try
            {
                if (!_sessionStarted)
                {
                    var sessionHeader = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} === SESSION START ===";
                    File.AppendAllText(_logPath, sessionHeader + Environment.NewLine, Encoding.UTF8);
                    _sessionStarted = true;
                }
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                var fallback = $"[Logger failed: {ex.Message}] {line}";
                Debug.WriteLine(fallback);
                Console.Error.WriteLine(fallback);
            }
        }
    }

    public static string LogPath => _logPath;
}
