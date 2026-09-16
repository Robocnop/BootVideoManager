using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using BootVideoManager.Core.Api;

namespace BootVideoManager.Core.Caching;

/// <summary>
/// Disk cache for catalog thumbnails: each image is downloaded once, with a small concurrency limit so
/// scrolling the grid never floods the CDN, and old files are evicted beyond a size budget.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    public const long DefaultMaxBytes = 200L * 1024 * 1024;
    private const long MaxThumbnailBytes = 10L * 1024 * 1024;
    private const int MaxConcurrentDownloads = 4;
    private static readonly HashSet<string> KnownExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private readonly IFileSystem _fileSystem;
    private readonly HttpClient _http;
    private readonly RepoApiOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new(StringComparer.Ordinal);

    public ThumbnailCache(
        IFileSystem fileSystem,
        string directory,
        HttpClient http,
        RepoApiOptions options,
        TimeProvider timeProvider,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _fileSystem = fileSystem;
        Directory = directory;
        _http = http;
        _options = options;
        _timeProvider = timeProvider;
        _maxBytes = maxBytes;
    }

    public string Directory { get; }

    /// <summary>
    /// Local path of the image, downloading it if needed. Returns <c>null</c> when it cannot be obtained:
    /// thumbnails are cosmetic and a placeholder is shown instead of an error.
    /// </summary>
    public Task<string?> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var path = PathFor(uri);

        if (_fileSystem.File.Exists(path))
        {
            Touch(path);
            return Task.FromResult<string?>(path);
        }

        // Concurrent requests for the same image share one download; a caller cancelling only stops waiting.
        var download = _inFlight.GetOrAdd(path, key => new Lazy<Task<string?>>(() => DownloadAsync(uri, key)));
        return download.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Deletes least recently used images until the cache is back under 80 % of its budget.</summary>
    public void Trim()
    {
        try
        {
            if (!_fileSystem.Directory.Exists(Directory))
            {
                return;
            }

            var files = _fileSystem.DirectoryInfo.New(Directory)
                .EnumerateFiles()
                .OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= _maxBytes)
            {
                return;
            }

            var target = _maxBytes * 8 / 10;
            foreach (var file in files)
            {
                if (total <= target)
                {
                    break;
                }

                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eviction is opportunistic; it will run again next time.
        }
    }

    public void Dispose() => _downloadSlots.Dispose();

    internal string PathFor(Uri uri)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32];
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return _fileSystem.Path.Combine(Directory, key + (KnownExtensions.Contains(extension) ? extension : ".img"));
    }

    private async Task<string?> DownloadAsync(Uri uri, string path)
    {
        var partPath = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await _downloadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxThumbnailBytes)
                {
                    return null;
                }

                _fileSystem.Directory.CreateDirectory(Directory);
                await using (var destination = _fileSystem.FileStream.New(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    if (!await CopyWithLimitAsync(source, destination).ConfigureAwait(false))
                    {
                        return null;
                    }
                }

                _fileSystem.File.Move(partPath, path, overwrite: true);
                return path;
            }
            finally
            {
                _downloadSlots.Release();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (_fileSystem.File.Exists(partPath))
                {
                    _fileSystem.File.Delete(partPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temporary file will be evicted by Trim later.
            }

            _inFlight.TryRemove(path, out _);
        }
    }

    private static async Task<bool> CopyWithLimitAsync(Stream source, Stream destination)
    {
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxThumbnailBytes)
            {
                return false;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return total > 0;
    }

    private void Touch(string path)
    {
        try
        {
            _fileSystem.File.SetLastAccessTimeUtc(path, _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only affects eviction order.
        }
    }
}
