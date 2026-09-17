using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Install;

/// <summary>Download progress; <see cref="TotalBytes"/> is unknown when the server sends no length.</summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0..1, or <c>null</c> when the total is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>Relationship between a file in the movies folder and the manifest.</summary>
public enum InstalledVideoStatus
{
    /// <summary>Installed by this app and unchanged: safe to delete.</summary>
    Tracked,

    /// <summary>Installed by this app but changed since: ask before deleting.</summary>
    Modified,

    /// <summary>Added by the user or another tool: ask before deleting.</summary>
    Untracked,

    /// <summary>Stock animation shipped with Steam (<c>steamui/movies</c>): never modified nor deleted, only copied when enabled.</summary>
    BuiltIn,
}

/// <summary>
/// A video Steam can use: a <c>.webm</c> of the movies folder (enabled), of its disabled sibling folder, or a stock
/// Steam animation (enabled when its copy sits in the movies folder).
/// </summary>
public sealed record InstalledVideo(string FileName, string FullPath, long SizeBytes, InstalledVideoStatus Status, ManifestEntry? Entry)
{
    /// <summary>True when Steam sees the video (Customization list and startup movie shuffle).</summary>
    public bool IsEnabled { get; init; } = true;

    public bool IsBuiltIn => Status == InstalledVideoStatus.BuiltIn;

    /// <summary>File of Steam's startup movie cache (<c>config/communityitemscache/startupmovies</c>) rather than the movies folder.</summary>
    public bool IsInSteamCache { get; init; }

    /// <summary>Points Shop item cached by Steam: managed in Steam, never moved nor deleted by the app.</summary>
    public bool IsSteamShopItem => IsInSteamCache && BuiltInVideos.IsSteamShopItemFileName(FileName);

    public bool RequiresConfirmationToDelete => Status is InstalledVideoStatus.Modified or InstalledVideoStatus.Untracked;

    public VideoType Type => Entry?.Type ?? BuiltInVideos.TypeOf(FileName);

    public string DisplayTitle => Entry?.Title
        ?? (IsBuiltIn ? BuiltInVideos.TitleOf(FileName) : Path.GetFileNameWithoutExtension(FileName));
}

public enum UninstallOutcome
{
    Deleted,

    /// <summary>Nothing was deleted: the file was not installed by this app (or was modified) and the user has not confirmed.</summary>
    RequiresConfirmation,

    /// <summary>The file was already absent; the manifest has been cleaned.</summary>
    AlreadyGone,
}

/// <summary>Summary of a "remove all" operation.</summary>
public sealed record UninstallAllResult(int Deleted, int RequiringConfirmation, IReadOnlyList<string> Failed);

public enum InstallErrorKind
{
    /// <summary>Server unreachable, HTTP error, interrupted or incomplete transfer.</summary>
    Download,

    /// <summary>The content is not a WebM video or is unreasonably large.</summary>
    InvalidFile,

    /// <summary>A file with the target name exists and does not belong to this app.</summary>
    FileConflict,

    /// <summary>Permissions, disk full, file locked by Steam…</summary>
    FileSystem,
}

/// <summary>Expected install/uninstall failure, with a category for user-facing messages.</summary>
public sealed class InstallException : Exception
{
    public InstallException(InstallErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public InstallErrorKind Kind { get; }
}
