using System.IO.Abstractions;
using System.Text.RegularExpressions;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Install;

/// <summary>
/// Stock animations shipped in <c>&lt;Steam&gt;/steamui/movies</c>, and the folders derived from a movies folder.
/// Steam plays (and shuffles) what sits in <c>config/uioverrides/movies</c> and in its startup movie cache, so a
/// stock intro is enabled by copying it into the movies folder, and any video is disabled by moving it to a
/// <c>_disabled</c> sibling folder Steam does not read.
/// </summary>
public static partial class BuiltInVideos
{
    /// <summary>Prefix of the copies placed in the movies folder, so they are recognisable in Steam's list.</summary>
    public const string CopyPrefix = "steam_default_";

    /// <summary>Appended to the movies folder name: <c>uioverrides/movies</c> → <c>uioverrides/movies_disabled</c>.</summary>
    public const string DisabledSuffix = "_disabled";

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigpicture_startup"] = "Big Picture",
        ["deck_startup"] = "Steam Deck",
        ["oled_startup"] = "Steam Deck OLED",
        ["steam_os_startup"] = "SteamOS",
        ["startup_machine"] = "Steam Machine",
        ["steamframe_startup_450x450"] = "Steam Frame",
        ["deck-suspend-animation"] = "Steam Deck",
        ["oled-suspend-animation"] = "Steam Deck OLED",
        ["steam_os_suspend"] = "SteamOS",
    };

    /// <summary>
    /// Startup and suspend animations; controller tutorials and the "from throbber" transition variants are not
    /// selectable movies and are ignored.
    /// </summary>
    public static bool IsSelectable(string fileName) =>
        VideoFileNames.IsSafeFileName(fileName)
        && !fileName.Contains("throbber", StringComparison.OrdinalIgnoreCase)
        && TypeOf(fileName) != VideoType.Unknown;

    public static VideoType TypeOf(string fileName) =>
        fileName.Contains("startup", StringComparison.OrdinalIgnoreCase) ? VideoType.BootVideo
        : fileName.Contains("suspend", StringComparison.OrdinalIgnoreCase) ? VideoType.SuspendVideo
        : VideoType.Unknown;

    public static string TitleOf(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return $"{Titles.GetValueOrDefault(name, name)} (Steam)";
    }

    /// <summary>Name of the copy of <paramref name="builtInFileName"/> in the movies folder.</summary>
    public static string CopyFileName(string builtInFileName) => CopyPrefix + builtInFileName;

    /// <summary><c>&lt;Steam&gt;/steamui/movies</c> for a <c>&lt;Steam&gt;/config/uioverrides/movies</c> folder; <c>null</c> for any other layout.</summary>
    public static string? DirectoryFor(IFileSystem fileSystem, string moviesDirectory) =>
        ConfigDirectoryFor(fileSystem, moviesDirectory) is { } config && fileSystem.Path.GetDirectoryName(config) is { } root
            ? fileSystem.Path.Combine(root, "steamui", "movies")
            : null;

    /// <summary>
    /// <c>&lt;Steam&gt;/config/communityitemscache/startupmovies</c>: where Steam caches Points Shop startup movies.
    /// Steam's shuffle also plays any other <c>.webm</c> placed there, so those files are listed and can be disabled.
    /// </summary>
    public static string? SteamCacheDirectoryFor(IFileSystem fileSystem, string moviesDirectory) =>
        ConfigDirectoryFor(fileSystem, moviesDirectory) is { } config
            ? fileSystem.Path.Combine(config, "communityitemscache", "startupmovies")
            : null;

    /// <summary>
    /// Points Shop items are cached as <c>{communityitemid}_{sha1}.webm</c>; Steam downloads them again if they are
    /// moved, so they are left to Steam.
    /// </summary>
    public static bool IsSteamShopItemFileName(string fileName) => ShopItemName().IsMatch(fileName);

    [GeneratedRegex(@"^\d+_[0-9a-f]{40}\.webm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShopItemName();

    private static string? ConfigDirectoryFor(IFileSystem fileSystem, string moviesDirectory)
    {
        var uiOverrides = fileSystem.Path.GetDirectoryName(moviesDirectory);
        var config = uiOverrides is null ? null : fileSystem.Path.GetDirectoryName(uiOverrides);

        return config is not null
            && string.Equals(fileSystem.Path.GetFileName(uiOverrides), "uioverrides", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fileSystem.Path.GetFileName(config), "config", StringComparison.OrdinalIgnoreCase)
            && fileSystem.Path.GetDirectoryName(config) is not null
                ? config
                : null;
    }

    /// <summary>Where disabled videos are kept: next to the movies folder, outside what Steam scans.</summary>
    public static string DisabledDirectoryFor(string moviesDirectory) => moviesDirectory + DisabledSuffix;
}
