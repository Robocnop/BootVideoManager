using System.IO.Abstractions;
using System.Text;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Steam;

/// <summary>
/// Steam's "Startup movie" choice, stored per device in <c>&lt;Steam&gt;/config/config.vdf</c> under
/// <c>InstallConfigStore › Customization › StartupMovie</c>. With <c>Shuffle</c> on, Steam picks a random video
/// among the files of <c>uioverrides/movies</c> at each start: exactly the "enabled videos" of this app.
/// With it off, Steam keeps playing the single selected video (its default one unless the user picked another).
/// </summary>
public static class StartupMovieSettings
{
    public const string BackupSuffix = ".bvm-backup";

    private static readonly string[] ShufflePath = ["InstallConfigStore", "Customization", "StartupMovie", "Shuffle"];

    public static string ConfigPath(IFileSystem fileSystem, string steamRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return fileSystem.Path.Combine(steamRoot, "config", "config.vdf");
    }

    /// <summary>
    /// <c>true</c>/<c>false</c> when the setting could be read (a missing key means off, Steam's default);
    /// <c>null</c> when <c>config.vdf</c> is missing or unreadable, in which case nothing should be changed.
    /// </summary>
    public static bool? IsShuffleEnabled(IFileSystem fileSystem, string steamRoot)
    {
        try
        {
            var path = ConfigPath(fileSystem, steamRoot);
            if (!fileSystem.File.Exists(path))
            {
                return null;
            }

            var (text, _) = Read(fileSystem, path);
            return KeyValuesText.TryGetValue(text, ShufflePath, out var value) && value.Trim() == "1";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns the shuffle on. Steam must not be running: it keeps this file in memory and rewrites it when it exits.
    /// The previous file is copied next to it (<c>config.vdf.bvm-backup</c>) and the new one is written atomically.
    /// </summary>
    /// <exception cref="IOException">Missing or locked file, disk error.</exception>
    /// <exception cref="UnauthorizedAccessException">No write access to the Steam folder.</exception>
    /// <exception cref="InvalidDataException">The file does not look like Steam's config.</exception>
    public static void EnableShuffle(IFileSystem fileSystem, string steamRoot)
    {
        var path = ConfigPath(fileSystem, steamRoot);
        if (!fileSystem.File.Exists(path))
        {
            throw new FileNotFoundException("Steam's config.vdf was not found.", path);
        }

        var (text, hasBom) = Read(fileSystem, path);
        var updated = KeyValuesText.SetValue(text, ShufflePath, "1");
        if (string.Equals(updated, text, StringComparison.Ordinal))
        {
            return;
        }

        fileSystem.File.Copy(path, path + BackupSuffix, overwrite: true);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
        JsonFile.WriteAtomically(fileSystem, path, [.. encoding.GetPreamble(), .. encoding.GetBytes(updated)]);
    }

    private static (string Text, bool HasBom) Read(IFileSystem fileSystem, string path)
    {
        var bytes = fileSystem.File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        return (Encoding.UTF8.GetString(hasBom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes), hasBom);
    }
}
