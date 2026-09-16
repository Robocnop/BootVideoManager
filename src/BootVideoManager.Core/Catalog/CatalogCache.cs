using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BootVideoManager.Core.Catalog;

/// <summary>Bookkeeping stored next to the cached catalog.</summary>
public sealed record CatalogCacheMetadata
{
    /// <summary>Server <c>Last-Modified</c> of the cached body, replayed as <c>If-Modified-Since</c>.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Last time the server confirmed or replaced the catalog.</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>Trending order, best first.</summary>
    public IReadOnlyList<string> TrendingPostIds { get; init; } = [];

    /// <summary>Last successful trending refresh.</summary>
    public DateTimeOffset? TrendingFetchedAt { get; init; }
}

/// <summary>Catalog body and its metadata as read from disk.</summary>
public sealed record CachedCatalog(byte[] Content, CatalogCacheMetadata Metadata);

/// <summary>
/// Disk cache of the raw <c>/api/posts/all</c> response. The raw body is kept (not a re-serialisation)
/// so cached and fresh data always go through the exact same parser.
/// </summary>
public sealed class CatalogCache
{
    public const string ContentFileName = "catalog.json";
    public const string MetadataFileName = "catalog.meta.json";

    private readonly IFileSystem _fileSystem;
    private readonly string _contentPath;
    private readonly string _metadataPath;

    /// <param name="fileSystem">File system abstraction (real or in-memory for tests).</param>
    /// <param name="directory">Application cache directory; created on first write.</param>
    public CatalogCache(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _fileSystem = fileSystem;
        Directory = directory;
        _contentPath = fileSystem.Path.Combine(directory, ContentFileName);
        _metadataPath = fileSystem.Path.Combine(directory, MetadataFileName);
    }

    public string Directory { get; }

    /// <summary>Reads the cache; returns <c>null</c> when absent or unreadable (a broken cache is just a cache miss).</summary>
    public CachedCatalog? TryLoad()
    {
        try
        {
            if (!_fileSystem.File.Exists(_contentPath) || !_fileSystem.File.Exists(_metadataPath))
            {
                return null;
            }

            var metadata = JsonSerializer.Deserialize(
                _fileSystem.File.ReadAllBytes(_metadataPath),
                CacheJsonContext.Default.CatalogCacheMetadata);

            return metadata is null ? null : new CachedCatalog(_fileSystem.File.ReadAllBytes(_contentPath), metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the cached body and metadata.</summary>
    /// <exception cref="IOException">Disk full, permissions…</exception>
    public void Save(byte[] content, CatalogCacheMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(content);
        WriteAtomically(_contentPath, content);
        SaveMetadata(metadata);
    }

    /// <summary>Updates metadata only (e.g. after a 304).</summary>
    /// <exception cref="IOException">Disk full, permissions…</exception>
    public void SaveMetadata(CatalogCacheMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        WriteAtomically(_metadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata, CacheJsonContext.Default.CatalogCacheMetadata));
    }

    /// <summary>Write to a sibling temp file then rename, so a crash never leaves a truncated file.</summary>
    private void WriteAtomically(string path, byte[] bytes)
    {
        _fileSystem.Directory.CreateDirectory(Directory);
        var temporaryPath = path + ".tmp";
        _fileSystem.File.WriteAllBytes(temporaryPath, bytes);
        _fileSystem.File.Move(temporaryPath, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CatalogCacheMetadata))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;
