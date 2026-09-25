using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Models;
using BootVideoManager.Core.Sharing;

namespace BootVideoManager.Core.Tests.Sharing;

public sealed class VideoPacksTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Post StarWars = PostFactory.Create("MnZgE", title: "Star Wars", author: "Jaidek");
    private static readonly Post Zelda = PostFactory.Create("Zz9Yy", title: "Zelda", author: "Link");

    private static InstalledVideo Catalog(Post post, bool enabled = true, InstalledVideoStatus status = InstalledVideoStatus.Tracked) =>
        new($"{post.Slug}_{post.Id}.webm", $"/movies/{post.Id}.webm", 1000, status, new ManifestEntry
        {
            FileName = $"{post.Slug}_{post.Id}.webm",
            MoviesDirectory = "/movies",
            Source = InstalledVideoSource.SteamDeckRepo,
            PostId = post.Id,
            Title = post.Title,
            Author = post.Author.Name,
            Sha256 = "00",
        })
        { IsEnabled = enabled };

    private static InstalledVideo LocalImport(string fileName) =>
        new(fileName, $"/movies/{fileName}", 1000, InstalledVideoStatus.Tracked, new ManifestEntry
        {
            FileName = fileName,
            MoviesDirectory = "/movies",
            Source = InstalledVideoSource.LocalImport,
            Title = fileName,
            Sha256 = "00",
        });

    private static InstalledVideo BuiltIn(string fileName, bool enabled) =>
        new(fileName, $"/steamui/movies/{fileName}", 1000, InstalledVideoStatus.BuiltIn, null) { IsEnabled = enabled };

    private static InstalledVideo Untracked(string fileName) =>
        new(fileName, $"/movies/{fileName}", 1000, InstalledVideoStatus.Untracked, null);

    private static InstalledVideo SteamCache(string fileName, bool enabled) =>
        new(fileName, $"/cache/{fileName}", 1000, InstalledVideoStatus.Untracked, null) { IsEnabled = enabled, IsInSteamCache = true };

    [Fact]
    public void Create_KeepsCatalogVideosWithTheirState_AndEnabledStockVideos_AndCountsEnabledUnshareableVideos()
    {
        var export = VideoPacks.Create(
            [
                Catalog(StarWars),
                Catalog(Zelda, enabled: false),
                BuiltIn("deck_startup.webm", enabled: true),
                BuiltIn("oled_startup.webm", enabled: false),
                LocalImport("mine.webm"),
                Untracked("dropped.webm"),
                SteamCache("cached_on.webm", enabled: true),
                SteamCache("cached_off.webm", enabled: false),
            ],
            Now);

        Assert.Equal(VideoPack.FormatName, export.Pack.Format);
        Assert.Equal(Now, export.Pack.CreatedAt);
        Assert.Equal(
            [
                (VideoPackItemKind.Catalog, "MnZgE", true),
                (VideoPackItemKind.Catalog, "Zz9Yy", false),
                (VideoPackItemKind.SteamBuiltIn, "deck_startup.webm", true),
            ],
            export.Pack.Videos.Select(v => (v.Kind, v.Id, v.Enabled)));
        Assert.Equal(3, export.SkippedVideos);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var fileSystem = new MockFileSystem();
        var path = MockUnixSupport.Path(@"C:\Users\me\my-videos.bvmpack");
        var pack = VideoPacks.Create([Catalog(StarWars), BuiltIn("deck_startup.webm", enabled: true)], Now).Pack;

        VideoPacks.Save(fileSystem, path, pack);
        var loaded = VideoPacks.Load(fileSystem, path);

        Assert.Equal(pack.CreatedAt, loaded.CreatedAt);
        Assert.Equal(pack.Videos, loaded.Videos);
        Assert.Contains("\"kind\": \"Catalog\"", fileSystem.File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"format":"something.else","version":1,"videos":[]}""")]
    [InlineData("""{"format":"bootvideomanager.pack","version":0,"videos":[]}""")]
    [InlineData("[1,2,3]")]
    public void Parse_RejectsWhatIsNotAPack(string json) =>
        Assert.Throws<VideoPackException>(() => VideoPacks.Parse(Fixture.Utf8(json)));

    [Fact]
    public void Parse_RefusesNewerVersions_WithAnUpdateHint()
    {
        var ex = Assert.Throws<VideoPackException>(() => VideoPacks.Parse(Fixture.Utf8("""{"format":"bootvideomanager.pack","version":99,"videos":[]}""")));

        Assert.Contains("Boot Video Manager", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DropsInvalidAndDuplicateEntries()
    {
        const string json = """
            {
              "format": "bootvideomanager.pack",
              "version": 1,
              "videos": [
                { "kind": "Catalog", "id": "MnZgE", "title": "Star Wars" },
                { "kind": "Catalog", "id": "MnZgE", "title": "Duplicate" },
                { "kind": "Catalog", "id": "../../evil", "title": "Path" },
                { "kind": "SteamBuiltIn", "id": "..\\..\\evil_startup.webm", "title": "Path" },
                { "kind": "SteamBuiltIn", "id": "deck_startup.webm", "enabled": false },
                null
              ]
            }
            """;

        var pack = VideoPacks.Parse(Fixture.Utf8(json));

        Assert.Equal(
            [(VideoPackItemKind.Catalog, "MnZgE", true), (VideoPackItemKind.SteamBuiltIn, "deck_startup.webm", false)],
            pack.Videos.Select(v => (v.Kind, v.Id, v.Enabled)));
    }

    [Fact]
    public void Load_RefusesOversizedFiles()
    {
        var fileSystem = new MockFileSystem();
        var path = MockUnixSupport.Path(@"C:\big.bvmpack");
        fileSystem.AddFile(path, new MockFileData(new byte[VideoPacks.MaxFileBytes + 1]));

        Assert.Throws<VideoPackException>(() => VideoPacks.Load(fileSystem, path));
    }

    [Fact]
    public void Load_MissingFile_IsAPackException()
    {
        var fileSystem = new MockFileSystem();

        Assert.Throws<VideoPackException>(() => VideoPacks.Load(fileSystem, MockUnixSupport.Path(@"C:\missing.bvmpack")));
    }

    [Fact]
    public void Plan_SplitsIntoDownloads_AlreadyInstalled_AndUnavailable()
    {
        var removed = PostFactory.Create("Gone1", title: "Removed", type: VideoType.Removed);
        var pack = new VideoPack
        {
            Format = VideoPack.FormatName,
            Videos =
            [
                new VideoPackItem { Kind = VideoPackItemKind.Catalog, Id = StarWars.Id, Title = StarWars.Title },
                new VideoPackItem { Kind = VideoPackItemKind.Catalog, Id = Zelda.Id, Title = Zelda.Title, Enabled = false },
                new VideoPackItem { Kind = VideoPackItemKind.Catalog, Id = removed.Id, Title = removed.Title },
                new VideoPackItem { Kind = VideoPackItemKind.Catalog, Id = "Unkwn", Title = "Unknown" },
                new VideoPackItem { Kind = VideoPackItemKind.SteamBuiltIn, Id = "deck_startup.webm" },
                new VideoPackItem { Kind = VideoPackItemKind.SteamBuiltIn, Id = "oled_startup.webm" },
            ],
        };

        var plan = VideoPacks.Plan(
            pack,
            [StarWars, Zelda, removed],
            [Catalog(StarWars, enabled: false), BuiltIn("deck_startup.webm", enabled: false)]);

        Assert.Equal([new PlannedDownload(Zelda, false)], plan.Downloads);
        Assert.Equal(2, plan.AlreadyInstalled);
        Assert.Equal(["Gone1", "Unkwn", "oled_startup.webm"], plan.Unavailable.Select(i => i.Id));
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void Plan_IgnoresUntrackedFilesThatHappenToCarryAPostId()
    {
        var pack = new VideoPack { Format = VideoPack.FormatName, Videos = [new VideoPackItem { Id = StarWars.Id }] };

        var plan = VideoPacks.Plan(pack, [StarWars], [Catalog(StarWars, status: InstalledVideoStatus.Untracked)]);

        Assert.Single(plan.Downloads);
        Assert.Equal(0, plan.AlreadyInstalled);
    }

    [Fact]
    public void Contains_MatchesCatalogAndStockVideos_Only()
    {
        var pack = VideoPacks.Create([Catalog(StarWars), BuiltIn("deck_startup.webm", enabled: true)], Now).Pack;

        Assert.True(VideoPacks.Contains(pack, Catalog(StarWars, enabled: false)));
        Assert.True(VideoPacks.Contains(pack, BuiltIn("deck_startup.webm", enabled: false)));
        Assert.False(VideoPacks.Contains(pack, Catalog(Zelda)));
        Assert.False(VideoPacks.Contains(pack, LocalImport("mine.webm")));
    }
}
