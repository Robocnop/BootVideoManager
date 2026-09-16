using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace BootVideoManager.Core.Steam;

/// <summary>Reads the Steam install path written by the official Steam installer.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSteamRegistry : ISteamRegistry
{
    public IEnumerable<string> GetSteamPathCandidates()
    {
        var values = new List<string>();
        AddValue(values, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        AddValue(values, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        AddValue(values, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        return values;
    }

    private static void AddValue(List<string> values, RegistryKey hive, string keyPath, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            // A locked-down key simply means "not found here".
        }
    }
}
