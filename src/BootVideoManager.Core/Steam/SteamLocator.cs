using System.IO.Abstractions;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Steam;

/// <summary>Source of Steam paths stored in the Windows registry (abstracted for tests and other OSes).</summary>
public interface ISteamRegistry
{
    /// <summary>Raw registry values, most specific first.</summary>
    IEnumerable<string> GetSteamPathCandidates();
}

/// <summary>Host facts the locator depends on.</summary>
/// <param name="IsWindows">Selects registry-based or Linux path-based detection.</param>
/// <param name="HomeDirectory">User home folder.</param>
/// <param name="ProgramFilesX86Directory">Windows only.</param>
public sealed record SteamLocatorEnvironment(bool IsWindows, string HomeDirectory, string? ProgramFilesX86Directory)
{
    public static SteamLocatorEnvironment Current() =>
        new(
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) : null);
}

/// <summary>Finds Steam installations and validates user-picked folders.</summary>
public sealed class SteamLocator
{
    private static readonly (string RelativePath, SteamInstallKind Kind)[] LinuxCandidates =
    [
        (".steam/root", SteamInstallKind.Native),
        (".steam/steam", SteamInstallKind.Native),
        (".local/share/Steam", SteamInstallKind.Native),
        (".var/app/com.valvesoftware.Steam/.local/share/Steam", SteamInstallKind.Flatpak),
        (".var/app/com.valvesoftware.Steam/data/Steam", SteamInstallKind.Flatpak),
        ("snap/steam/common/.local/share/Steam", SteamInstallKind.Snap),
    ];

    private readonly IFileSystem _fileSystem;
    private readonly SteamLocatorEnvironment _environment;
    private readonly ISteamRegistry? _registry;

    public SteamLocator(IFileSystem fileSystem, SteamLocatorEnvironment environment, ISteamRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(environment);
        _fileSystem = fileSystem;
        _environment = environment;
        _registry = registry;
    }

    /// <summary>All distinct Steam installations found, best candidate first.</summary>
    public IReadOnlyList<SteamInstallation> FindInstallations()
    {
        var candidates = _environment.IsWindows ? GetWindowsCandidates() : GetLinuxCandidates();
        var seen = new HashSet<string>(PathUtilities.FileNameComparer);
        var found = new List<SteamInstallation>();

        foreach (var (path, kind) in candidates)
        {
            if (TryNormalize(path) is not { } fullPath || !IsSteamRoot(fullPath))
            {
                continue;
            }

            // ~/.steam/root and ~/.steam/steam are symlinks to the real folder: report it once.
            var realPath = ResolveLinks(fullPath);
            if (seen.Add(realPath))
            {
                found.Add(Create(realPath, kind));
            }
        }

        return found;
    }

    /// <summary>Validates a folder picked by the user; <c>null</c> if it does not look like Steam.</summary>
    public SteamInstallation? TryCreateManual(string path) =>
        TryNormalize(path) is { } fullPath && IsSteamRoot(fullPath) ? Create(fullPath, SteamInstallKind.Manual) : null;

    /// <summary>A Steam root contains its <c>config</c> or <c>steamapps</c> folder, or the Steam launcher.</summary>
    public bool IsSteamRoot(string path)
    {
        try
        {
            return _fileSystem.Directory.Exists(path)
                && (_fileSystem.Directory.Exists(_fileSystem.Path.Combine(path, "config"))
                    || _fileSystem.Directory.Exists(_fileSystem.Path.Combine(path, "steamapps"))
                    || _fileSystem.File.Exists(_fileSystem.Path.Combine(path, "steam.exe"))
                    || _fileSystem.File.Exists(_fileSystem.Path.Combine(path, "steam.sh")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<(string Path, SteamInstallKind Kind)> GetWindowsCandidates()
    {
        if (_registry is not null)
        {
            foreach (var value in _registry.GetSteamPathCandidates())
            {
                // HKCU SteamPath is stored lower-case with forward slashes ("c:/program files (x86)/steam").
                yield return (value.Replace('/', '\\'), SteamInstallKind.Registry);
            }
        }

        if (!string.IsNullOrEmpty(_environment.ProgramFilesX86Directory))
        {
            yield return (_fileSystem.Path.Combine(_environment.ProgramFilesX86Directory, "Steam"), SteamInstallKind.DefaultLocation);
        }
    }

    private IEnumerable<(string Path, SteamInstallKind Kind)> GetLinuxCandidates() =>
        LinuxCandidates.Select(candidate => (
            _fileSystem.Path.Combine([_environment.HomeDirectory, .. candidate.RelativePath.Split('/')]),
            candidate.Kind));

    private SteamInstallation Create(string root, SteamInstallKind kind)
    {
        var displayRoot = WithActualCasing(root);
        return new SteamInstallation(displayRoot, kind, _fileSystem.Path.Combine(displayRoot, "config", "uioverrides", "movies"));
    }

    /// <summary>
    /// The registry stores "c:/program files (x86)/steam"; rebuild the on-disk casing so the UI shows a
    /// familiar path. Windows only (case-sensitive file systems already have the right casing).
    /// </summary>
    private string WithActualCasing(string path)
    {
        if (!_environment.IsWindows)
        {
            return path;
        }

        try
        {
            var root = _fileSystem.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }

            var current = root.ToUpperInvariant();
            foreach (var segment in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = _fileSystem.Directory.EnumerateDirectories(current, segment).FirstOrDefault();
                if (match is null)
                {
                    return path;
                }

                current = match;
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    private string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return PathUtilities.NormalizeDirectory(_fileSystem, path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string ResolveLinks(string path)
    {
        try
        {
            var target = _fileSystem.DirectoryInfo.New(path).ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? path : PathUtilities.NormalizeDirectory(_fileSystem, target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or NotImplementedException)
        {
            return path;
        }
    }
}
