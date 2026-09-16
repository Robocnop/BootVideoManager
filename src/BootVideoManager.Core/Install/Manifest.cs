using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Install;

/// <summary>Origin of an installed file.</summary>
public enum InstalledVideoSource
{
    SteamDeckRepo,
    LocalImport,
}

/// <summary>One file this application placed in a movies folder.</summary>
public sealed record ManifestEntry
{
    /// <summary>Bare file name inside <see cref="MoviesDirectory"/>.</summary>
    public required string FileName { get; init; }

    /// <summary>Normalised absolute movies folder (several Steam installs can coexist).</summary>
    public required string MoviesDirectory { get; init; }

    public InstalledVideoSource Source { get; init; }

    /// <summary>steamdeckrepo.com post id, for catalog look-ups.</summary>
    public string? PostId { get; init; }

    public required string Title { get; init; }

    /// <summary>Author name, kept for credit even if the post disappears from the catalog.</summary>
    public string? Author { get; init; }

    public VideoType Type { get; init; }

    public Uri? PageUri { get; init; }

    public Uri? ThumbnailUri { get; init; }

    /// <summary>Lower-case hex SHA-256 of the installed content.</summary>
    public required string Sha256 { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>File time right after install; when unchanged the hash does not need recomputing.</summary>
    public DateTimeOffset LastWriteTimeUtc { get; init; }

    public DateTimeOffset InstalledAt { get; init; }
}

/// <summary>Everything this application installed, across all Steam folders.</summary>
public sealed record Manifest
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public IReadOnlyList<ManifestEntry> Entries { get; init; } = [];
}
