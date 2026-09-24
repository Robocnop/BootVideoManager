using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Models;
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

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var store = new SettingsStore(_fileSystem, Path);

        store.Save(new AppSettings { SteamRootOverride = MockUnixSupport.Path(@"D:\Steam") });
        store.Save(new AppSettings());

        Assert.Equal([Path], _fileSystem.AllFiles);
    }

    [Fact]
    public void Update_KeepsTheOtherSettings()
    {
        var store = new SettingsStore(_fileSystem, Path);
        store.Save(new AppSettings { SteamRootOverride = "steam", Language = "en" });

        store.Update(s => s with { Catalog = s.Catalog with { Sort = CatalogSort.MostLiked, Device = DeviceTag.SteamDeck } });

        var loaded = store.Load();
        Assert.Equal("steam", loaded.SteamRootOverride);
        Assert.Equal("en", loaded.Language);
        Assert.Equal(CatalogSort.MostLiked, loaded.Catalog.Sort);
        Assert.Equal(DeviceTag.SteamDeck, loaded.Catalog.Device);
    }

    [Fact]
    public void OlderSettingsFile_GetsDefaultsForNewFields()
    {
        _fileSystem.AddFile(Path, new MockFileData("""{ "steamRootOverride": "D:\\Steam" }"""));

        var loaded = new SettingsStore(_fileSystem, Path).Load();

        Assert.Equal(@"D:\Steam", loaded.SteamRootOverride);
        Assert.True(loaded.CheckForUpdates);
        Assert.Equal(PreviewPreferences.DefaultVolume, loaded.Preview.Volume);
        Assert.Equal(CatalogSort.Trending, loaded.Catalog.Sort);
    }

    [Fact]
    public void Enums_AreStoredByName()
    {
        var store = new SettingsStore(_fileSystem, Path);

        store.Save(new AppSettings { Catalog = new CatalogPreferences { Sort = CatalogSort.Newest, Type = VideoType.SuspendVideo } });

        var json = _fileSystem.File.ReadAllText(Path);
        Assert.Contains("\"Newest\"", json, StringComparison.Ordinal);
        Assert.Contains("\"SuspendVideo\"", json, StringComparison.Ordinal);
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
