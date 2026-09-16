using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BootVideoManager.Core.Platform;

/// <summary>User preferences persisted between sessions.</summary>
public sealed record AppSettings
{
    /// <summary>Steam folder chosen manually; <c>null</c> to use automatic detection.</summary>
    public string? SteamRootOverride { get; init; }
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _path;

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
        JsonFile.WriteAtomically(_fileSystem, _path, JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
