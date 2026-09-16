using System.IO.Abstractions;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Caching;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Platform;
using BootVideoManager.Core.Steam;

namespace BootVideoManager.App.Services;

/// <summary>Core services wired together for the desktop application.</summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;

    private AppServices(
        HttpClient http,
        RepoApiOptions apiOptions,
        CatalogService catalog,
        InstallService install,
        ThumbnailCache thumbnails,
        SettingsStore settings,
        SteamLocator steamLocator)
    {
        _http = http;
        ApiOptions = apiOptions;
        Catalog = catalog;
        Install = install;
        Thumbnails = thumbnails;
        Settings = settings;
        SteamLocator = steamLocator;
    }

    public RepoApiOptions ApiOptions { get; }

    public CatalogService Catalog { get; }

    public InstallService Install { get; }

    public ThumbnailCache Thumbnails { get; }

    public SettingsStore Settings { get; }

    public SteamLocator SteamLocator { get; }

    public static AppServices Create()
    {
        var fileSystem = new FileSystem();
        var time = TimeProvider.System;
        var paths = AppPaths.ForCurrentUser();
        var apiOptions = new RepoApiOptions();

        // One HttpClient for the whole app: shared connection pool, polite retries, identifiable User-Agent.
        var http = RepoApiClient.CreateHttpClient(apiOptions);
        var api = new RepoApiClient(http, apiOptions);

        return new AppServices(
            http,
            apiOptions,
            new CatalogService(api, new CatalogCache(fileSystem, paths.CatalogCacheDirectory), time),
            new InstallService(fileSystem, new ManifestStore(fileSystem, paths.ManifestPath, time), http, api, apiOptions, time),
            new ThumbnailCache(fileSystem, paths.ThumbnailCacheDirectory, http, apiOptions, time),
            new SettingsStore(fileSystem, paths.SettingsPath),
            new SteamLocator(
                fileSystem,
                SteamLocatorEnvironment.Current(),
                OperatingSystem.IsWindows() ? new WindowsSteamRegistry() : null));
    }

    public void Dispose()
    {
        Catalog.Dispose();
        Install.Dispose();
        Thumbnails.Dispose();
        _http.Dispose();
    }
}
