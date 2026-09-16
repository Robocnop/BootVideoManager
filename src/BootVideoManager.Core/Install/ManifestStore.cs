using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Install;

/// <summary>Persists the <see cref="Manifest"/> as JSON in the application config folder.</summary>
public sealed class ManifestStore
{
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    public ManifestStore(IFileSystem fileSystem, string path, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fileSystem = fileSystem;
        Path = path;
        _timeProvider = timeProvider;
    }

    public string Path { get; }

    /// <summary>
    /// Loads the manifest. A corrupted file is backed up and an empty manifest is returned: every file then
    /// shows as "not installed by the app" and requires confirmation before deletion — the safe direction.
    /// </summary>
    /// <exception cref="IOException">The manifest exists but cannot be read.</exception>
    public Manifest Load()
    {
        if (!_fileSystem.File.Exists(Path))
        {
            return new Manifest();
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(_fileSystem.File.ReadAllBytes(Path), ManifestJsonContext.Default.Manifest)
                ?? throw new JsonException("Empty manifest.");

            return manifest with
            {
                Entries = manifest.Entries
                    .Where(entry => VideoFileNames.IsSafeFileName(entry.FileName)
                        && !string.IsNullOrWhiteSpace(entry.MoviesDirectory)
                        && !string.IsNullOrWhiteSpace(entry.Sha256))
                    .ToArray(),
            };
        }
        catch (JsonException)
        {
            BackUpCorruptedFile();
            return new Manifest();
        }
    }

    /// <exception cref="IOException">The manifest could not be written.</exception>
    public void Save(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        JsonFile.WriteAtomically(_fileSystem, Path, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonContext.Default.Manifest));
    }

    private void BackUpCorruptedFile()
    {
        var suffix = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            _fileSystem.File.Copy(Path, $"{Path}.corrupt-{suffix}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: losing the backup must not prevent the app from starting.
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Manifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
