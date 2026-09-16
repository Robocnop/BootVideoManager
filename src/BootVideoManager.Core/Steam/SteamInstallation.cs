namespace BootVideoManager.Core.Steam;

/// <summary>How a Steam folder was found.</summary>
public enum SteamInstallKind
{
    /// <summary>Windows registry (<c>SteamPath</c> / <c>InstallPath</c>).</summary>
    Registry,

    /// <summary>Windows default location under Program Files (x86).</summary>
    DefaultLocation,

    /// <summary>Linux native package / Steam Deck.</summary>
    Native,

    /// <summary>Flathub <c>com.valvesoftware.Steam</c>.</summary>
    Flatpak,

    /// <summary>Snap package.</summary>
    Snap,

    /// <summary>Chosen by the user.</summary>
    Manual,
}

/// <summary>A Steam installation and the folder where custom movies go.</summary>
/// <param name="RootPath">Steam root folder.</param>
/// <param name="Kind">Detection source.</param>
/// <param name="MoviesDirectory"><c>&lt;root&gt;/config/uioverrides/movies</c> (may not exist yet).</param>
public sealed record SteamInstallation(string RootPath, SteamInstallKind Kind, string MoviesDirectory);
