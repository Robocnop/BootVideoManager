using BootVideoManager.Core.Platform;

namespace BootVideoManager.App.Services;

/// <summary>Application-wide access to the diagnostic log; a no-op until <see cref="Initialize"/> is called.</summary>
public static class AppLog
{
    private static FileLog? _log;

    public static string? Directory => _log?.Directory;

    public static void Initialize(FileLog log)
    {
        _log = log;
        log.DeleteOlderThan(TimeSpan.FromDays(14));
    }

    public static void Info(string message) => _log?.Info(message);

    public static void Warn(string message, Exception? exception = null) => _log?.Warn(message, exception);

    public static void Error(string message, Exception? exception = null)
    {
        _log?.Error(message, exception);
        if (_log is null)
        {
            Console.Error.WriteLine($"{message}{Environment.NewLine}{exception}");
        }
    }
}
