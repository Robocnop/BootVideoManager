namespace BootVideoManager.Core.Platform;

/// <summary>Where the application keeps its own files (never inside the Steam folder).</summary>
/// <param name="ConfigDirectory">Settings and install manifest (must survive cache cleaning).</param>
/// <param name="CacheDirectory">Re-downloadable data: catalog and thumbnails.</param>
public sealed record AppPaths(string ConfigDirectory, string CacheDirectory)
{
    public const string AppFolderName = "BootVideoManager";

    public string ManifestPath => Path.Combine(ConfigDirectory, "manifest.json");

    public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public string CatalogCacheDirectory => Path.Combine(CacheDirectory, "catalog");

    public string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbnails");

    /// <summary>
    /// Windows: <c>%APPDATA%\BootVideoManager</c> and <c>%LOCALAPPDATA%\BootVideoManager\cache</c>.
    /// Linux: XDG base directories (<c>~/.config</c>, <c>~/.cache</c>), which Flatpak redirects into the sandbox.
    /// </summary>
    public static AppPaths ForCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return new AppPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "cache"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = NonEmpty(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var cache = NonEmpty(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")) ?? Path.Combine(home, ".cache");
        return new AppPaths(Path.Combine(config, AppFolderName), Path.Combine(cache, AppFolderName));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
