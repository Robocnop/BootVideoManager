using System.IO.Abstractions;

namespace BootVideoManager.Core.Platform;

/// <summary>Path comparison rules matching the host file system.</summary>
internal static class PathUtilities
{
    /// <summary>Windows file names are case-insensitive; Linux ones are not.</summary>
    public static StringComparison FileNameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer FileNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Absolute path without trailing separator, so equal folders compare equal.</summary>
    public static string NormalizeDirectory(IFileSystem fileSystem, string path) =>
        fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(path));

    public static bool SameDirectory(IFileSystem fileSystem, string left, string right) =>
        string.Equals(NormalizeDirectory(fileSystem, left), NormalizeDirectory(fileSystem, right), FileNameComparison);
}
