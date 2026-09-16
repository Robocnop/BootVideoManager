using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace BootVideoManager.Core.Tests.Install;

public class ManifestStoreTests
{
    private static readonly string Path = MockUnixSupport.Path(@"C:\AppData\BootVideoManager\manifest.json");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero));

    private static ManifestEntry Entry(string fileName) => new()
    {
        FileName = fileName,
        MoviesDirectory = MockUnixSupport.Path(@"C:\Steam\config\uioverrides\movies"),
        Title = "Title",
        Sha256 = "00ff",
        PostId = "MnZgE",
        Type = VideoType.SuspendVideo,
        PageUri = new Uri("https://steamdeckrepo.com/post/MnZgE/x"),
        SizeBytes = 42,
        LastWriteTimeUtc = new DateTimeOffset(2026, 9, 16, 20, 0, 0, 123, TimeSpan.Zero).AddTicks(4567),
    };

    [Fact]
    public void MissingFile_LoadsEmptyManifest()
    {
        Assert.Empty(new ManifestStore(_fileSystem, Path, _time).Load().Entries);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new ManifestStore(_fileSystem, Path, _time);
        var manifest = new Manifest { Entries = [Entry("a_MnZgE.webm")] };

        store.Save(manifest);
        var loaded = store.Load();

        Assert.Equal(manifest.Entries, loaded.Entries);
        Assert.Contains("\"type\": \"SuspendVideo\"", _fileSystem.File.ReadAllText(Path), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptedFile_IsBackedUp_AndTreatedAsEmpty()
    {
        _fileSystem.AddFile(Path, new MockFileData("{ this is not json"));
        var store = new ManifestStore(_fileSystem, Path, _time);

        var manifest = store.Load();

        Assert.Empty(manifest.Entries);
        Assert.Equal("{ this is not json", _fileSystem.File.ReadAllText(Path + ".corrupt-20260916-213000"));
    }

    [Fact]
    public void TamperedEntries_WithUnsafeFileNames_AreIgnored()
    {
        var store = new ManifestStore(_fileSystem, Path, _time);
        store.Save(new Manifest { Entries = [Entry("ok_MnZgE.webm"), Entry("../../important.webm"), Entry("system.dll")] });

        Assert.Equal(["ok_MnZgE.webm"], store.Load().Entries.Select(e => e.FileName));
    }
}
