namespace BootVideoManager.Core.Platform;

/// <summary>Where the application keeps its own files (never inside the Steam folder).</summary>
/// <param name="ConfigDirectory">Settings and install manifest (must survive cache cleaning).</param>
/// <param name="CacheDirectory">Re-downloadable data: catalog, thumbnails and downloaded updates.</param>
/// <param name="LogDirectory">Diagnostic logs.</param>
public sealed record AppPaths(string ConfigDirectory, string CacheDirectory, string LogDirectory)
{
    public const string AppFolderName = "BootVideoManager";

    public string ManifestPath => Path.Combine(ConfigDirectory, "manifest.json");

    public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public string CatalogCacheDirectory => Path.Combine(CacheDirectory, "catalog");

    public string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbnails");

    public string UpdateDownloadDirectory => Path.Combine(CacheDirectory, "updates");

    /// <summary>
    /// Windows: <c>%APPDATA%\BootVideoManager</c>, <c>%LOCALAPPDATA%\BootVideoManager\cache</c> and <c>…\logs</c>.
    /// Linux: XDG base directories (<c>~/.config</c>, <c>~/.cache</c>, <c>~/.local/state</c>), which Flatpak redirects
    /// into the sandbox.
    /// </summary>
    public static AppPaths ForCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
            return new AppPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName),
                Path.Combine(local, "cache"),
                Path.Combine(local, "logs"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = NonEmpty(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var cache = NonEmpty(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")) ?? Path.Combine(home, ".cache");
        var state = NonEmpty(Environment.GetEnvironmentVariable("XDG_STATE_HOME")) ?? Path.Combine(home, ".local", "state");
        return new AppPaths(
            Path.Combine(config, AppFolderName),
            Path.Combine(cache, AppFolderName),
            Path.Combine(state, AppFolderName, "logs"));
    }

    /// <summary>Every folder under <paramref name="root"/>: handy for tests and portable setups.</summary>
    public static AppPaths Under(string root) =>
        new(Path.Combine(root, "config"), Path.Combine(root, "cache"), Path.Combine(root, "logs"));

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
