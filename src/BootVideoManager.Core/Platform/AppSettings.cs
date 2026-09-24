using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Platform;

/// <summary>
/// User preferences persisted between sessions. Missing fields (older files) keep their defaults: properties use
/// <c>set</c> rather than <c>init</c> because the JSON source generator resets absent init-only properties.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Steam folder chosen manually; <c>null</c> to use automatic detection.</summary>
    public string? SteamRootOverride { get; set; }

    /// <summary>Last sort and filters of the catalog.</summary>
    public CatalogPreferences Catalog { get; set; } = new();

    /// <summary>Preview sound.</summary>
    public PreviewPreferences Preview { get; set; } = new();

    /// <summary>Look for a newer release on GitHub at startup.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Release the user chose to skip: not offered again at startup.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Interface language (<c>fr</c>, <c>en</c>), or <c>null</c> to follow the system.</summary>
    public string? Language { get; set; }
}

/// <summary>Catalog sort and filters.</summary>
public sealed record CatalogPreferences
{
    public const string AnyDuration = "all";

    public CatalogSort Sort { get; set; } = CatalogSort.Trending;

    public VideoType? Type { get; set; }

    public DeviceTag? Device { get; set; }

    /// <summary>Duration filter key (<see cref="AnyDuration"/>, <c>short</c>, <c>medium</c>, <c>long</c>).</summary>
    public string Duration { get; set; } = AnyDuration;
}

/// <summary>Video preview sound settings.</summary>
public sealed record PreviewPreferences
{
    public const int DefaultVolume = 70;

    /// <summary>0 to 100.</summary>
    public int Volume { get; set; } = DefaultVolume;

    public bool Muted { get; set; }
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fileSystem = fileSystem;
        _path = path;
    }

    /// <summary>Missing or unreadable settings fall back to defaults: preferences are never worth a crash.</summary>
    public AppSettings Load()
    {
        try
        {
            return _fileSystem.File.Exists(_path)
                ? JsonSerializer.Deserialize(_fileSystem.File.ReadAllBytes(_path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <exception cref="IOException">The settings file could not be written.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            JsonFile.WriteAtomically(_fileSystem, _path, JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings));
        }
    }

    /// <summary>Changes some settings while keeping the others (read, modify, write under a lock).</summary>
    /// <returns>The saved settings.</returns>
    /// <exception cref="IOException">The settings file could not be written.</exception>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = change(Load());
            JsonFile.WriteAtomically(_fileSystem, _path, JsonSerializer.SerializeToUtf8Bytes(updated, SettingsJsonContext.Default.AppSettings));
            return updated;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
