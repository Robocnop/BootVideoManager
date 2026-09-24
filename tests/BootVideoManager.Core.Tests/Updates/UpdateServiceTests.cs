using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Updates;

namespace BootVideoManager.Core.Tests.Updates;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly string DownloadDirectory = MockUnixSupport.Path(@"C:\Cache\updates");
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes(new string('x', 5_000));
    private const string InstallerName = "BootVideoManager-1.2.0-win-x64-setup.exe";

    private readonly MockFileSystem _fileSystem = new();
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    private readonly StubHttpHandler _http;

    public UpdateServiceTests() => _http = new StubHttpHandler((request, _) => _respond(request));

    public void Dispose() => _http.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UpdateService CreateService(string currentVersion = "1.0.0") =>
        new(new HttpClient(_http), new UpdateOptions { UserAgent = "Tests" }, _fileSystem, DownloadDirectory, Version.Parse(currentVersion), "win-x64");

    private static string ReleaseJson(string tag, bool prerelease = false, bool draft = false, long? installerSize = null) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/Robocnop/SteamBigStartup_launcher/releases/tag/{{tag}}",
          "body": "Notes",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "BootVideoManager-1.2.0-linux-x64.tar.gz", "browser_download_url": "https://example.test/linux.tar.gz", "size": 10 },
            { "name": "BootVideoManager-1.2.0-win-arm64-setup.exe", "browser_download_url": "https://example.test/arm64.exe", "size": 10 },
            { "name": "{{InstallerName}}", "browser_download_url": "https://example.test/setup.exe", "size": {{installerSize ?? Installer.Length}} },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt", "size": 100 }
          ]
        }
        """;

    private void ServeRelease(string json, string? checksums = null, byte[]? installer = null) =>
        _respond = request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.EndsWith("/releases/latest", StringComparison.Ordinal) => StubHttpHandler.Json(Encoding.UTF8.GetBytes(json)),
            "https://example.test/SHA256SUMS.txt" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(checksums ?? $"{Convert.ToHexStringLower(SHA256.HashData(Installer))}  {InstallerName}\n"),
            },
            "https://example.test/setup.exe" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer ?? Installer) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    [Fact]
    public async Task Check_NewerRelease_IsOfferedWithTheInstallerForThisArchitecture()
    {
        ServeRelease(ReleaseJson("v1.2.0"));

        var update = await CreateService().CheckAsync(Ct);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 0), update.Version);
        Assert.Equal("1.2.0", update.VersionText);
        Assert.Equal(InstallerName, update.Installer?.Name);
        Assert.Equal("SHA256SUMS.txt", update.Checksums?.Name);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(new Uri("https://api.github.com/repos/Robocnop/SteamBigStartup_launcher/releases/latest"), request.Uri);
        Assert.Equal("Tests", request.UserAgent);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    public async Task Check_SameOrOlderRelease_OffersNothing(string current)
    {
        ServeRelease(ReleaseJson("v1.2.0"));

        Assert.Null(await CreateService(current).CheckAsync(Ct));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Check_PrereleaseOrDraft_IsIgnored(bool prerelease, bool draft)
    {
        ServeRelease(ReleaseJson("v9.0.0", prerelease, draft));

        Assert.Null(await CreateService().CheckAsync(Ct));
    }

    [Fact]
    public async Task Check_NoReleaseYet_OffersNothing()
    {
        Assert.Null(await CreateService().CheckAsync(Ct));
    }

    [Fact]
    public async Task Check_GitHubUnreachable_IsReported()
    {
        _respond = _ => throw new HttpRequestException("offline");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => CreateService().CheckAsync(Ct));

        Assert.Equal(UpdateErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Check_UnreadableAnswer_IsReported()
    {
        ServeRelease("<html>maintenance</html>");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => CreateService().CheckAsync(Ct));

        Assert.Equal(UpdateErrorKind.InvalidResponse, ex.Kind);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.0.0-beta.1", "2.0.0")]
    [InlineData("v1.0.0+build.5", "1.0.0")]
    public void TryParseVersion_AcceptsCommonTagFormats(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.TryParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void TryParseVersion_RejectsNonVersions(string? tag)
    {
        Assert.Null(UpdateService.TryParseVersion(tag));
    }

    [Fact]
    public async Task Download_VerifiedInstaller_IsSavedAndReturned()
    {
        ServeRelease(ReleaseJson("v1.2.0"));
        var service = CreateService();
        var update = (await service.CheckAsync(Ct))!;

        var path = await service.DownloadInstallerAsync(update, cancellationToken: Ct);

        Assert.Equal(_fileSystem.Path.Combine(DownloadDirectory, InstallerName), path);
        Assert.Equal(Installer, _fileSystem.File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Download_ChecksumMismatch_IsRejectedAndDeleted()
    {
        ServeRelease(ReleaseJson("v1.2.0"), installer: Encoding.UTF8.GetBytes(new string('y', Installer.Length)));
        var service = CreateService();
        var update = (await service.CheckAsync(Ct))!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadInstallerAsync(update, cancellationToken: Ct));

        Assert.Equal(UpdateErrorKind.Verification, ex.Kind);
        Assert.False(_fileSystem.File.Exists(_fileSystem.Path.Combine(DownloadDirectory, InstallerName)));
    }

    [Fact]
    public async Task Download_SizeMismatch_IsRejected()
    {
        ServeRelease(ReleaseJson("v1.2.0", installerSize: Installer.Length + 10));
        var service = CreateService();
        var update = (await service.CheckAsync(Ct))!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadInstallerAsync(update, cancellationToken: Ct));

        Assert.Equal(UpdateErrorKind.Verification, ex.Kind);
    }

    [Fact]
    public async Task Download_ChecksumMissingFromTheList_IsRejectedBeforeDownloading()
    {
        ServeRelease(ReleaseJson("v1.2.0"), checksums: "0000  another-file.exe\n");
        var service = CreateService();
        var update = (await service.CheckAsync(Ct))!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadInstallerAsync(update, cancellationToken: Ct));

        Assert.Equal(UpdateErrorKind.Verification, ex.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Uri == new Uri("https://example.test/setup.exe"));
    }

    [Fact]
    public async Task Download_ReleaseWithoutInstaller_IsRejected()
    {
        var update = new AvailableUpdate(new Version(2, 0, 0), "v2.0.0", new Uri("https://example.test/"), string.Empty, null, null);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => CreateService().DownloadInstallerAsync(update, cancellationToken: Ct));

        Assert.Equal(UpdateErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Download_ReportsProgress()
    {
        ServeRelease(ReleaseJson("v1.2.0"));
        var service = CreateService();
        var update = (await service.CheckAsync(Ct))!;
        var reports = new List<DownloadProgress>();

        await service.DownloadInstallerAsync(update, new SyncProgress(reports.Add), Ct);

        Assert.Equal(new DownloadProgress(Installer.Length, Installer.Length), reports[^1]);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
