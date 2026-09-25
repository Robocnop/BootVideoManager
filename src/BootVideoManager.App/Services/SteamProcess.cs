using System.ComponentModel;
using System.Diagnostics;

namespace BootVideoManager.App.Services;

/// <summary>
/// Closes and restarts the Steam client, needed to change its <c>config.vdf</c> (Steam rewrites that file from
/// memory when it exits). Automated on Windows only; elsewhere the user is asked to quit Steam themselves.
/// </summary>
internal static class SteamProcess
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    public static async Task<bool> ShutdownAsync(string steamRoot)
    {
        if (!IsRunning())
        {
            return true;
        }

        if (!OperatingSystem.IsWindows() || !TryLaunch(steamRoot, "-shutdown"))
        {
            return false;
        }

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < ShutdownTimeout)
        {
            await Task.Delay(PollInterval);
            if (!IsRunning())
            {
                AppLog.Info($"Steam closed after {watch.Elapsed.TotalSeconds:0.0} s.");
                return true;
            }
        }

        AppLog.Warn("Steam did not close in time.");
        return false;
    }

    public static void Start(string steamRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            TryLaunch(steamRoot, arguments: null);
        }
    }

    private static bool TryLaunch(string steamRoot, string? arguments)
    {
        var executable = Path.Combine(steamRoot, "steam.exe");
        if (!File.Exists(executable))
        {
            AppLog.Warn($"Steam executable not found: {executable}");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                WorkingDirectory = steamRoot,
            });
            return true;
        }
        catch (Win32Exception ex)
        {
            AppLog.Warn($"Could not start Steam ({arguments ?? "no arguments"}).", ex);
            return false;
        }
    }
}
