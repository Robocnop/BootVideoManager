using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Platform;
using Microsoft.Extensions.Time.Testing;

namespace BootVideoManager.Core.Tests.Platform;

public class FileLogTests
{
    private static readonly string Directory = MockUnixSupport.Path(@"C:\Local\BootVideoManager\logs");
    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 24, 10, 30, 0, TimeSpan.Zero));

    [Fact]
    public void Entries_AreAppendedToTodaysFile_WithLevelAndException()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var log = new FileLog(_fileSystem, Directory, _time);

        log.Info("Started");
        log.Error("Install failed", new InvalidOperationException("boom"));

        var path = _fileSystem.Path.Combine(Directory, "bootvideomanager-2026-09-24.log");
        Assert.Equal(path, log.CurrentFilePath);
        var text = _fileSystem.File.ReadAllText(path);
        Assert.Contains("2026-09-24 10:30:00.000 [INFO] Started", text, StringComparison.Ordinal);
        Assert.Contains("[ERROR] Install failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteOlderThan_RemovesOnlyOldLogs()
    {
        var log = new FileLog(_fileSystem, Directory, _time);
        var old = _fileSystem.Path.Combine(Directory, "bootvideomanager-2026-08-01.log");
        var recent = _fileSystem.Path.Combine(Directory, "bootvideomanager-2026-09-20.log");
        var unrelated = _fileSystem.Path.Combine(Directory, "notes.txt");
        _fileSystem.AddFile(old, new MockFileData("old") { LastWriteTime = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) });
        _fileSystem.AddFile(recent, new MockFileData("recent") { LastWriteTime = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc) });
        _fileSystem.AddFile(unrelated, new MockFileData("keep") { LastWriteTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        log.DeleteOlderThan(TimeSpan.FromDays(14));

        Assert.False(_fileSystem.File.Exists(old));
        Assert.True(_fileSystem.File.Exists(recent));
        Assert.True(_fileSystem.File.Exists(unrelated));
    }

    [Fact]
    public void Logging_NeverThrows_WhenTheFolderCannotBeWritten()
    {
        _fileSystem.AddFile(Directory, new MockFileData("a file where the folder should be"));
        var log = new FileLog(_fileSystem, Directory, _time);

        log.Error("still fine");
    }
}
