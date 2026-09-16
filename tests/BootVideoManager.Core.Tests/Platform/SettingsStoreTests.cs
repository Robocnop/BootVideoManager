using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Tests.Platform;

public class SettingsStoreTests
{
    private static readonly string Path = MockUnixSupport.Path(@"C:\AppData\BootVideoManager\settings.json");
    private readonly MockFileSystem _fileSystem = new();

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new SettingsStore(_fileSystem, Path);
        var settings = new AppSettings { SteamRootOverride = MockUnixSupport.Path(@"D:\Steam") };

        store.Save(settings);

        Assert.Equal(settings, store.Load());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("null")]
    public void MissingOrCorruptedFile_FallsBackToDefaults(string? content)
    {
        if (content is not null)
        {
            _fileSystem.AddFile(Path, new MockFileData(content));
        }

        Assert.Equal(new AppSettings(), new SettingsStore(_fileSystem, Path).Load());
    }
}
