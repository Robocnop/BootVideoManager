using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace BootVideoManager.Core.Tests.Install;

public sealed class InstallServiceTests : IDisposable
{
    private static readonly string MoviesDirectory = MockUnixSupport.Path(@"C:\Steam\config\uioverrides\movies");
    private static readonly string ManifestPath = MockUnixSupport.Path(@"C:\AppData\BootVideoManager\manifest.json");
    private static readonly RepoApiOptions Options = new()
    {
        UserAgent = "BootVideoManager-Tests/1.0",
        Retry = new RetryPolicyOptions { BaseDelay = TimeSpan.Zero },
    };

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero));
    private readonly ManifestStore _manifestStore;
    private readonly StubHttpHandler _http;
    private readonly InstallService _service;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public InstallServiceTests()
    {
        _respond = _ => WebmResponse(WebmBytes(2_000));
        _http = new StubHttpHandler((request, _) => _respond(request));
        _manifestStore = new ManifestStore(_fileSystem, ManifestPath, _time);
        _service = new InstallService(_fileSystem, _manifestStore, new HttpClient(_http), new FakeRepoApiClient(), Options, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Post StarWars = PostFactory.Create("MnZgE", title: "Star Wars", author: "Jaidek");
    private static readonly Post Zelda = PostFactory.Create("Zz9Yy", title: "Zelda", author: "Link");

    public void Dispose() => _service.Dispose();

    private static byte[] WebmBytes(int length, byte fill = 7)
    {
        var bytes = Enumerable.Repeat(fill, length).ToArray();
        bytes[0] = 0x1A;
        bytes[1] = 0x45;
        bytes[2] = 0xDF;
        bytes[3] = 0xA3;
        return bytes;
    }

    private static HttpResponseMessage WebmResponse(byte[] body, long? declaredLength = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/webm");
        content.Headers.ContentLength = declaredLength ?? body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private string MoviePath(string fileName) => _fileSystem.Path.Combine(MoviesDirectory, fileName);

    private string[] FilesInMovies() =>
        _fileSystem.Directory.Exists(MoviesDirectory)
            ? _fileSystem.Directory.GetFiles(MoviesDirectory).Select(path => _fileSystem.Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray()
            : [];

    [Fact]
    public async Task Install_DownloadsIntoMoviesFolder_AndRecordsItInManifest()
    {
        var body = WebmBytes(300_000);
        _respond = _ => WebmResponse(body);
        var progress = new RecordingProgress();

        var video = await _service.InstallAsync(StarWars, MoviesDirectory, progress, Ct);

        Assert.Equal("mnzge_MnZgE.webm", video.FileName);
        Assert.Equal(InstalledVideoStatus.Tracked, video.Status);
        Assert.Equal(body, _fileSystem.File.ReadAllBytes(MoviePath("mnzge_MnZgE.webm")));
        Assert.Equal(["mnzge_MnZgE.webm"], FilesInMovies()); // no .part left behind

        var request = Assert.Single(_http.Requests);
        Assert.Equal(new Uri("https://steamdeckrepo.com/post/download/MnZgE"), request.Uri);
        Assert.Equal(Options.UserAgent, request.UserAgent);

        Assert.Equal(new DownloadProgress(body.Length, body.Length), progress.Reports[^1]);

        var entry = Assert.Single(_manifestStore.Load().Entries);
        Assert.Equal("MnZgE", entry.PostId);
        Assert.Equal("Star Wars", entry.Title);
        Assert.Equal("Jaidek", entry.Author);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), entry.Sha256);
        Assert.Equal(body.Length, entry.SizeBytes);
        Assert.Equal(_time.GetUtcNow(), entry.InstalledAt);
    }

    [Fact]
    public async Task Install_SamePostTwice_DownloadsOnlyOnce()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        var second = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);

        Assert.Equal(InstalledVideoStatus.Tracked, second.Status);
        Assert.Single(_http.Requests);
        Assert.Single(_manifestStore.Load().Entries);
    }

    [Fact]
    public async Task Install_NeverOverwritesAFileTheAppDidNotCreate()
    {
        _fileSystem.AddFile(MoviePath("mnzge_MnZgE.webm"), new MockFileData("user content"));

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct));

        Assert.Equal(InstallErrorKind.FileConflict, ex.Kind);
        Assert.Equal("user content", _fileSystem.File.ReadAllText(MoviePath("mnzge_MnZgE.webm")));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Install_ContentThatIsNotWebm_IsRejectedAndLeavesNothing()
    {
        _respond = _ => WebmResponse("<html>Error page</html>"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct));

        Assert.Equal(InstallErrorKind.InvalidFile, ex.Kind);
        Assert.Empty(FilesInMovies());
        Assert.Empty(_manifestStore.Load().Entries);
    }

    [Fact]
    public async Task Install_TruncatedDownload_IsRejected()
    {
        _respond = _ => WebmResponse(WebmBytes(1_000), declaredLength: 5_000);

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct));

        Assert.Equal(InstallErrorKind.Download, ex.Kind);
        Assert.Empty(FilesInMovies());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Install_HttpError_IsReportedAsDownloadFailure(HttpStatusCode status)
    {
        _respond = _ => new HttpResponseMessage(status);

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct));

        Assert.Equal(InstallErrorKind.Download, ex.Kind);
        Assert.Empty(FilesInMovies());
    }

    [Fact]
    public async Task Install_ConnectionFailure_IsReportedAsDownloadFailure()
    {
        _respond = _ => throw new HttpRequestException("offline");

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct));

        Assert.Equal(InstallErrorKind.Download, ex.Kind);
    }

    [Fact]
    public async Task Install_CancelledMidDownload_LeavesNothing()
    {
        _respond = _ => WebmResponse(WebmBytes(1_000_000));
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress(onReport: _ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.InstallAsync(StarWars, MoviesDirectory, progress, cts.Token));

        Assert.Empty(FilesInMovies());
        Assert.Empty(_manifestStore.Load().Entries);
    }

    [Fact]
    public async Task GetInstalled_ClassifiesFiles_AndForgetsVanishedOnes()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        await _service.InstallAsync(Zelda, MoviesDirectory, cancellationToken: Ct);
        await _service.InstallAsync(PostFactory.Create("Gone1", title: "Gone"), MoviesDirectory, cancellationToken: Ct);

        _fileSystem.File.WriteAllBytes(MoviePath("zz9yy_Zz9Yy.webm"), WebmBytes(3_000, fill: 9)); // edited by the user
        _fileSystem.File.Delete(MoviePath("gone1_Gone1.webm")); // deleted by the user
        _fileSystem.AddFile(MoviePath("my_own_intro.webm"), new MockFileData(WebmBytes(100)));
        _fileSystem.AddFile(MoviePath("readme.txt"), new MockFileData("not a video"));

        var videos = await _service.GetInstalledAsync(MoviesDirectory, Ct);

        Assert.Equal(
            [
                ("my_own_intro.webm", InstalledVideoStatus.Untracked),
                ("mnzge_MnZgE.webm", InstalledVideoStatus.Tracked),
                ("zz9yy_Zz9Yy.webm", InstalledVideoStatus.Modified),
            ],
            videos.Select(v => (v.FileName, v.Status)));
        Assert.Equal(["MnZgE", "Zz9Yy"], _manifestStore.Load().Entries.Select(e => e.PostId).Order());
    }

    [Fact]
    public async Task GetInstalled_TouchedButIdenticalFile_StaysTracked()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.File.SetLastWriteTimeUtc(MoviePath("mnzge_MnZgE.webm"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var video = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));

        Assert.Equal(InstalledVideoStatus.Tracked, video.Status);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), Assert.Single(_manifestStore.Load().Entries).LastWriteTimeUtc);
    }

    [Fact]
    public async Task GetInstalled_MissingFolder_IsEmpty()
    {
        Assert.Empty(await _service.GetInstalledAsync(MoviesDirectory, Ct));
    }

    [Fact]
    public async Task Uninstall_TrackedVideo_IsDeletedAndForgotten()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);

        var outcome = await _service.UninstallAsync(MoviesDirectory, video.FileName, userConfirmed: false, Ct);

        Assert.Equal(UninstallOutcome.Deleted, outcome);
        Assert.Empty(FilesInMovies());
        Assert.Empty(_manifestStore.Load().Entries);
    }

    [Fact]
    public async Task Uninstall_FileAddedByUser_RequiresConfirmation()
    {
        _fileSystem.AddFile(MoviePath("my_own_intro.webm"), new MockFileData(WebmBytes(100)));

        var withoutConfirmation = await _service.UninstallAsync(MoviesDirectory, "my_own_intro.webm", userConfirmed: false, Ct);
        Assert.Equal(UninstallOutcome.RequiresConfirmation, withoutConfirmation);
        Assert.True(_fileSystem.File.Exists(MoviePath("my_own_intro.webm")));

        var confirmed = await _service.UninstallAsync(MoviesDirectory, "my_own_intro.webm", userConfirmed: true, Ct);
        Assert.Equal(UninstallOutcome.Deleted, confirmed);
        Assert.False(_fileSystem.File.Exists(MoviePath("my_own_intro.webm")));
    }

    [Fact]
    public async Task Uninstall_ModifiedVideo_RequiresConfirmation()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.File.WriteAllBytes(video.FullPath, WebmBytes(50));

        var outcome = await _service.UninstallAsync(MoviesDirectory, video.FileName, userConfirmed: false, Ct);

        Assert.Equal(UninstallOutcome.RequiresConfirmation, outcome);
        Assert.True(_fileSystem.File.Exists(video.FullPath));
    }

    [Fact]
    public async Task Uninstall_AlreadyDeletedFile_CleansManifest()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.File.Delete(video.FullPath);

        Assert.Equal(UninstallOutcome.AlreadyGone, await _service.UninstallAsync(MoviesDirectory, video.FileName, false, Ct));
        Assert.Empty(_manifestStore.Load().Entries);
    }

    [Theory]
    [InlineData("../outside.webm")]
    [InlineData("..\\outside.webm")]
    [InlineData("sub/inner.webm")]
    [InlineData("C:\\Windows\\evil.webm")]
    [InlineData("notes.txt")]
    [InlineData(".webm")]
    [InlineData("")]
    public async Task Uninstall_RejectsAnythingButABareWebmName(string fileName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UninstallAsync(MoviesDirectory, fileName, userConfirmed: true, Ct));
    }

    [Fact]
    public async Task UninstallAll_WithoutConfirmation_KeepsFilesTheAppDidNotInstall()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        await _service.InstallAsync(Zelda, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.AddFile(MoviePath("my_own_intro.webm"), new MockFileData(WebmBytes(100)));

        var result = await _service.UninstallAllAsync(MoviesDirectory, includeUnconfirmed: false, Ct);

        Assert.Equal(new UninstallAllResult(2, 1, []), result with { Failed = [] });
        Assert.Empty(result.Failed);
        Assert.Equal(["my_own_intro.webm"], FilesInMovies());
    }

    [Fact]
    public async Task UninstallAll_Confirmed_RemovesEverything()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.AddFile(MoviePath("my_own_intro.webm"), new MockFileData(WebmBytes(100)));

        var result = await _service.UninstallAllAsync(MoviesDirectory, includeUnconfirmed: true, Ct);

        Assert.Equal(2, result.Deleted);
        Assert.Empty(FilesInMovies());
    }

    [Fact]
    public async Task ImportLocalFile_CopiesAndTracksIt()
    {
        var source = MockUnixSupport.Path(@"C:\Users\me\Videos\My Boot Intro.webm");
        var body = WebmBytes(5_000);
        _fileSystem.AddFile(source, new MockFileData(body));

        var video = await _service.ImportLocalFileAsync(source, MoviesDirectory, Ct);

        var expectedName = $"my_boot_intro_local-{Convert.ToHexStringLower(SHA256.HashData(body))[..8]}.webm";
        Assert.Equal(expectedName, video.FileName);
        Assert.Equal(InstalledVideoStatus.Tracked, video.Status);
        Assert.Equal(body, _fileSystem.File.ReadAllBytes(MoviePath(expectedName)));
        Assert.True(_fileSystem.File.Exists(source));
        var entry = Assert.Single(_manifestStore.Load().Entries);
        Assert.Equal(InstalledVideoSource.LocalImport, entry.Source);
        Assert.Equal("My Boot Intro", entry.Title);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task ImportLocalFile_RejectsRenamedNonWebm()
    {
        var source = MockUnixSupport.Path(@"C:\Users\me\Videos\clip.webm");
        _fileSystem.AddFile(source, new MockFileData("definitely an mp4"));

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.ImportLocalFileAsync(source, MoviesDirectory, Ct));

        Assert.Equal(InstallErrorKind.InvalidFile, ex.Kind);
        Assert.Empty(FilesInMovies());
    }

    [Fact]
    public async Task ImportLocalFile_RejectsOtherExtensions()
    {
        var source = MockUnixSupport.Path(@"C:\Users\me\Videos\clip.mp4");
        _fileSystem.AddFile(source, new MockFileData(WebmBytes(100)));

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.ImportLocalFileAsync(source, MoviesDirectory, Ct));

        Assert.Equal(InstallErrorKind.InvalidFile, ex.Kind);
    }

    private static readonly string DisabledDirectory = MockUnixSupport.Path(@"C:\Steam\config\uioverrides\movies_disabled");
    private static readonly string SteamUiMoviesDirectory = MockUnixSupport.Path(@"C:\Steam\steamui\movies");

    private string DisabledPath(string fileName) => _fileSystem.Path.Combine(DisabledDirectory, fileName);

    private byte[] AddStockAnimation(string fileName, int length = 1_500)
    {
        var body = WebmBytes(length, fill: 3);
        _fileSystem.AddFile(_fileSystem.Path.Combine(SteamUiMoviesDirectory, fileName), new MockFileData(body));
        return body;
    }

    [Fact]
    public async Task GetInstalled_ListsSteamStockAnimations_AsBuiltInAndDisabled()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        AddStockAnimation("deck_startup.webm");
        AddStockAnimation("oled-suspend-animation.webm");
        AddStockAnimation("oled-suspend-animation-from-throbber.webm"); // transition, not a selectable movie
        AddStockAnimation("steamdeck_touchscreen.webm"); // controller tutorial

        var videos = await _service.GetInstalledAsync(MoviesDirectory, Ct);

        Assert.Equal(
            [
                ("mnzge_MnZgE.webm", InstalledVideoStatus.Tracked, true),
                ("deck_startup.webm", InstalledVideoStatus.BuiltIn, false),
                ("oled-suspend-animation.webm", InstalledVideoStatus.BuiltIn, false),
            ],
            videos.Select(v => (v.FileName, v.Status, v.IsEnabled)));
        Assert.Equal("Steam Deck (Steam)", videos[1].DisplayTitle);
        Assert.Equal(VideoType.BootVideo, videos[1].Type);
        Assert.Equal(VideoType.SuspendVideo, videos[2].Type);
    }

    [Fact]
    public async Task SetEnabled_BuiltIn_CopiesIntoMoviesFolder_AndDisablingRemovesOnlyTheCopy()
    {
        var body = AddStockAnimation("deck_startup.webm");
        var stock = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));

        await _service.SetEnabledAsync(MoviesDirectory, stock, enabled: true, Ct);

        Assert.Equal(["steam_default_deck_startup.webm"], FilesInMovies());
        Assert.Equal(body, _fileSystem.File.ReadAllBytes(MoviePath("steam_default_deck_startup.webm")));
        var enabled = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct)); // the copy is merged into its stock row
        Assert.Equal((InstalledVideoStatus.BuiltIn, true), (enabled.Status, enabled.IsEnabled));
        Assert.Equal(InstalledVideoSource.SteamBuiltIn, Assert.Single(_manifestStore.Load().Entries).Source);

        await _service.SetEnabledAsync(MoviesDirectory, enabled, enabled: false, Ct);

        Assert.Empty(FilesInMovies());
        Assert.Empty(_manifestStore.Load().Entries);
        Assert.Equal(body, _fileSystem.File.ReadAllBytes(_fileSystem.Path.Combine(SteamUiMoviesDirectory, "deck_startup.webm")));
        Assert.False(Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct)).IsEnabled);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task SetEnabled_InstalledVideo_MovesToDisabledFolderAndBack_WithoutDownloadingAgain()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);

        await _service.SetEnabledAsync(MoviesDirectory, video, enabled: false, Ct);

        Assert.Empty(FilesInMovies());
        Assert.True(_fileSystem.File.Exists(DisabledPath(video.FileName)));
        var disabled = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));
        Assert.Equal((InstalledVideoStatus.Tracked, false), (disabled.Status, disabled.IsEnabled));
        Assert.Single(_manifestStore.Load().Entries); // still installed

        await _service.SetEnabledAsync(MoviesDirectory, disabled, enabled: true, Ct);

        Assert.Equal([video.FileName], FilesInMovies());
        Assert.True(Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct)).IsEnabled);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task SetEnabled_FileAddedByUser_IsMovedNotDeleted()
    {
        _fileSystem.AddFile(MoviePath("my_own_intro.webm"), new MockFileData(WebmBytes(100)));
        var video = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));

        await _service.SetEnabledAsync(MoviesDirectory, video, enabled: false, Ct);

        Assert.True(_fileSystem.File.Exists(DisabledPath("my_own_intro.webm")));
        var disabled = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));
        Assert.Equal((InstalledVideoStatus.Untracked, false), (disabled.Status, disabled.IsEnabled));
    }

    [Fact]
    public async Task SetEnabled_NameTakenInTarget_Throws_AndMovesNothing()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.AddFile(DisabledPath(video.FileName), new MockFileData(WebmBytes(10)));

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.SetEnabledAsync(MoviesDirectory, video, enabled: false, Ct));

        Assert.Equal(InstallErrorKind.FileConflict, ex.Kind);
        Assert.Equal([video.FileName], FilesInMovies());
    }

    [Fact]
    public async Task Install_DisabledVideo_ReEnablesItWithoutDownloading()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        await _service.SetEnabledAsync(MoviesDirectory, video, enabled: false, Ct);

        var again = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);

        Assert.Equal(MoviePath(video.FileName), again.FullPath);
        Assert.Equal([video.FileName], FilesInMovies());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Uninstall_DisabledVideo_IsDeletedAndForgotten()
    {
        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        await _service.SetEnabledAsync(MoviesDirectory, video, enabled: false, Ct);

        Assert.Equal(UninstallOutcome.Deleted, await _service.UninstallAsync(MoviesDirectory, video.FileName, userConfirmed: false, Ct));

        Assert.False(_fileSystem.File.Exists(DisabledPath(video.FileName)));
        Assert.Empty(_manifestStore.Load().Entries);
    }

    [Fact]
    public async Task UninstallAll_DisablesStockAnimations_ButNeverDeletesThem()
    {
        AddStockAnimation("deck_startup.webm");
        AddStockAnimation("oled_startup.webm");
        await _service.SetEnabledAsync(MoviesDirectory, (await _service.GetInstalledAsync(MoviesDirectory, Ct))[0], enabled: true, Ct);

        var result = await _service.UninstallAllAsync(MoviesDirectory, includeUnconfirmed: true, Ct);

        Assert.Equal(1, result.Deleted);
        Assert.Empty(FilesInMovies());
        Assert.Equal(2, _fileSystem.Directory.GetFiles(SteamUiMoviesDirectory).Length);
    }

    private static readonly string SteamCacheDirectory = MockUnixSupport.Path(@"C:\Steam\config\communityitemscache\startupmovies");
    private const string ShopItemFileName = "31845574022_590371b7f10882d09ac59e5d3691b462c0091a6d.webm";

    private string CachePath(string fileName) => _fileSystem.Path.Combine(SteamCacheDirectory, fileName);

    [Fact]
    public async Task GetInstalled_ListsVideosHiddenInSteamStartupMovieCache()
    {
        _fileSystem.AddFile(CachePath("helldivers_2_part_2.webm"), new MockFileData(WebmBytes(100)));
        _fileSystem.AddFile(CachePath(ShopItemFileName), new MockFileData(WebmBytes(100)));

        var videos = await _service.GetInstalledAsync(MoviesDirectory, Ct);

        Assert.All(videos, v => Assert.True(v.IsInSteamCache && v.IsEnabled && v.Status == InstalledVideoStatus.Untracked));
        Assert.Equal([ShopItemFileName, "helldivers_2_part_2.webm"], videos.Select(v => v.FileName).Order(StringComparer.Ordinal));
        Assert.True(videos.Single(v => v.FileName == ShopItemFileName).IsSteamShopItem);
        Assert.False(videos.Single(v => v.FileName == "helldivers_2_part_2.webm").IsSteamShopItem);
    }

    [Fact]
    public async Task SetEnabled_SteamCacheVideo_MovesNextToTheCacheAndBack()
    {
        var body = WebmBytes(100);
        _fileSystem.AddFile(CachePath("helldivers_2_part_2.webm"), new MockFileData(body));
        var video = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));

        await _service.SetEnabledAsync(MoviesDirectory, video, enabled: false, Ct);

        Assert.False(_fileSystem.File.Exists(CachePath("helldivers_2_part_2.webm")));
        Assert.Equal(body, _fileSystem.File.ReadAllBytes(_fileSystem.Path.Combine(SteamCacheDirectory + "_disabled", "helldivers_2_part_2.webm")));
        var disabled = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));
        Assert.True(disabled.IsInSteamCache && !disabled.IsEnabled);
        Assert.Empty(FilesInMovies());

        await _service.SetEnabledAsync(MoviesDirectory, disabled, enabled: true, Ct);

        Assert.True(_fileSystem.File.Exists(CachePath("helldivers_2_part_2.webm")));
    }

    [Fact]
    public async Task SteamShopItem_IsNeverMovedNorDeleted()
    {
        _fileSystem.AddFile(CachePath(ShopItemFileName), new MockFileData(WebmBytes(100)));
        var item = Assert.Single(await _service.GetInstalledAsync(MoviesDirectory, Ct));

        await Assert.ThrowsAsync<InstallException>(() => _service.SetEnabledAsync(MoviesDirectory, item, enabled: false, Ct));
        await Assert.ThrowsAsync<InstallException>(() => _service.UninstallAsync(MoviesDirectory, item, userConfirmed: true, Ct));
        var result = await _service.UninstallAllAsync(MoviesDirectory, includeUnconfirmed: true, Ct);

        Assert.Equal(0, result.Deleted);
        Assert.True(_fileSystem.File.Exists(CachePath(ShopItemFileName)));
    }

    [Fact]
    public async Task Uninstall_SteamCacheVideo_RequiresConfirmation_AndNeverTouchesTheMoviesFolderNamesake()
    {
        await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);
        _fileSystem.AddFile(CachePath("mnzge_MnZgE.webm"), new MockFileData(WebmBytes(100)));
        var cached = (await _service.GetInstalledAsync(MoviesDirectory, Ct)).Single(v => v.IsInSteamCache);

        Assert.Equal(UninstallOutcome.RequiresConfirmation, await _service.UninstallAsync(MoviesDirectory, cached, userConfirmed: false, Ct));
        Assert.Equal(UninstallOutcome.Deleted, await _service.UninstallAsync(MoviesDirectory, cached, userConfirmed: true, Ct));

        Assert.False(_fileSystem.File.Exists(CachePath("mnzge_MnZgE.webm")));
        Assert.Equal(["mnzge_MnZgE.webm"], FilesInMovies());
    }

    [Fact]
    public async Task Install_InterruptedDownload_ResumesWhereItStopped()
    {
        var body = WebmBytes(200_000);
        var ranges = new List<RangeHeaderValue?>();
        _respond = request =>
        {
            ranges.Add(request.Headers.Range);
            return ranges.Count == 1
                ? StreamResponse(new FailingStream(body, failAfter: 70_000), body.Length)
                : PartialResponse(body, from: request.Headers.Range!.Ranges.Single().From!.Value);
        };

        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);

        Assert.Equal(body, _fileSystem.File.ReadAllBytes(video.FullPath));
        Assert.Equal(2, ranges.Count);
        Assert.Null(ranges[0]);
        Assert.Equal(70_000, ranges[1]!.Ranges.Single().From);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), Assert.Single(_manifestStore.Load().Entries).Sha256);
    }

    [Fact]
    public async Task Install_InterruptedDownload_RestartsWhenTheServerIgnoresTheRange()
    {
        var body = WebmBytes(150_000);
        _respond = _ => _http.Requests.Count == 1
            ? StreamResponse(new FailingStream(body, failAfter: 40_000), body.Length)
            : WebmResponse(body);

        var video = await _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct);

        Assert.Equal(body, _fileSystem.File.ReadAllBytes(video.FullPath));
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Install_DownloadThatKeepsFailing_GivesUpAndLeavesNothing()
    {
        var body = WebmBytes(100_000);
        _respond = _ => StreamResponse(new FailingStream(body, failAfter: 10_000), body.Length);

        var ex = await Assert.ThrowsAsync<InstallException>(() => _service.InstallAsync(StarWars, MoviesDirectory, cancellationToken: Ct));

        Assert.Equal(InstallErrorKind.Download, ex.Kind);
        Assert.Equal(4, _http.Requests.Count);
        Assert.Empty(FilesInMovies());
    }

    private static HttpResponseMessage StreamResponse(Stream stream, long length)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage PartialResponse(byte[] body, long from)
    {
        var content = new ByteArrayContent(body[(int)from..]);
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    /// <summary>Yields the first bytes of a body, then fails like a dropped connection.</summary>
    private sealed class FailingStream(byte[] body, int failAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter)
            {
                throw new IOException("Connection reset (simulated).");
            }

            var read = Math.Min(count, failAfter - _position);
            Array.Copy(body, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Synchronous progress sink (the BCL Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class RecordingProgress(Action<DownloadProgress>? onReport = null) : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }
}
