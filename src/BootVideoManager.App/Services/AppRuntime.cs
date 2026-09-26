using System.Diagnostics;
using System.Runtime.InteropServices;
using BootVideoManager.Core.Localization;

namespace BootVideoManager.App.Services;

/// <summary>Facts about the running copy: version, architecture, and how it was installed.</summary>
public static class AppRuntime
{
    public static Version Version { get; } = typeof(AppRuntime).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string VersionText => Version.ToString(3);

    /// <summary>Selects the matching installer in a release (<c>win-x64</c>, <c>win-arm64</c>…).</summary>
    public static string RuntimeIdentifier =>
        (OperatingSystem.IsWindows() ? "win-" : "linux-")
        + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>
    /// Running as a Flatpak (e.g. from Flathub): the store installs updates, so the app must not offer its own.
    /// </summary>
    public static bool IsFlatpak => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"));

    /// <summary>
    /// True when installed by the Windows installer (its uninstaller sits next to the executable): only then can the
    /// app update itself. Portable copies send the user to the releases page instead.
    /// </summary>
    public static bool CanSelfUpdate =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Installed for every user (under Program Files): the update then needs administrator rights.</summary>
    private static bool IsPerMachineInstall
    {
        get
        {
            var directory = AppContext.BaseDirectory;
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(Environment.GetFolderPath)
                .Where(folder => !string.IsNullOrEmpty(folder))
                .Any(folder => directory.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Starts the downloaded installer in update mode: it waits for this app to exit, installs over the current copy
    /// (same folder, same scope) and restarts the app.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        var arguments = string.Join(
            ' ',
            "/SILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/UPDATE=1",
            $"/LANG={Loc.Current}",
            IsPerMachineInstall ? "/ALLUSERS" : "/CURRENTUSER");

        using var process = Process.Start(new ProcessStartInfo(installerPath, arguments) { UseShellExecute = true });
    }
}
