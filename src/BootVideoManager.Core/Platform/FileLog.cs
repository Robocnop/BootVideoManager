using System.Globalization;
using System.IO.Abstractions;
using System.Text;

namespace BootVideoManager.Core.Platform;

/// <summary>
/// Plain-text diagnostic log, one file per day, so a user can attach it to a bug report. Logging never throws:
/// a full disk or a locked file must not break the application.
/// </summary>
public sealed class FileLog
{
    private const string FilePrefix = "bootvideomanager-";
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    public FileLog(IFileSystem fileSystem, string directory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fileSystem = fileSystem;
        Directory = directory;
        _timeProvider = timeProvider;
    }

    public string Directory { get; }

    /// <summary>Path of today's log file.</summary>
    public string CurrentFilePath =>
        _fileSystem.Path.Combine(Directory, $"{FilePrefix}{_timeProvider.GetLocalNow():yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Deletes log files older than <paramref name="age"/>.</summary>
    public void DeleteOlderThan(TimeSpan age)
    {
        try
        {
            if (!_fileSystem.Directory.Exists(Directory))
            {
                return;
            }

            var limit = _timeProvider.GetUtcNow().UtcDateTime - age;
            foreach (var file in _fileSystem.DirectoryInfo.New(Directory).EnumerateFiles(FilePrefix + "*.log"))
            {
                if (file.LastWriteTimeUtc < limit)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaning is opportunistic.
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(_timeProvider.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .AppendLine(message);
        if (exception is not null)
        {
            line.AppendLine(exception.ToString());
        }

        lock (_gate)
        {
            try
            {
                _fileSystem.Directory.CreateDirectory(Directory);
                _fileSystem.File.AppendAllText(CurrentFilePath, line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a log line is better than failing the operation being logged.
            }
        }
    }
}
