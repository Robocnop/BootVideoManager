using System.Buffers;
using System.IO.Abstractions;
using System.Net;
using System.Security.Cryptography;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Models;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Install;

/// <summary>
/// Installs, lists and removes videos in a Steam movies folder, tracking its own files in a manifest so it
/// never silently deletes something the user added.
/// </summary>
public sealed class InstallService : IDisposable
{
    /// <summary>Hard cap protecting the disk against a wrong or hostile link.</summary>
    public const long MaxVideoBytes = 512L * 1024 * 1024;

    /// <summary>Every WebM (Matroska/EBML) file starts with these bytes.</summary>
    private static readonly byte[] WebmSignature = [0x1A, 0x45, 0xDF, 0xA3];

    private const int BufferSize = 81_920;

    private readonly IFileSystem _fileSystem;
    private readonly ManifestStore _manifestStore;
    private readonly HttpClient _http;
    private readonly IRepoApiClient _api;
    private readonly RepoApiOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    public InstallService(
        IFileSystem fileSystem,
        ManifestStore manifestStore,
        HttpClient http,
        IRepoApiClient api,
        RepoApiOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(manifestStore);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _fileSystem = fileSystem;
        _manifestStore = manifestStore;
        _http = http;
        _api = api;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Downloads a post into the movies folder: <c>.part</c> file → size and WebM checks → rename → manifest.
    /// Installing an already installed post is a no-op.
    /// </summary>
    /// <exception cref="InstallException">Download, validation, conflict or disk failure; nothing is left behind.</exception>
    /// <exception cref="OperationCanceledException">Cancelled by the caller; nothing is left behind.</exception>
    public async Task<InstalledVideo> InstallAsync(
        Post post,
        string moviesDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var directory = NormalizeMoviesDirectory(moviesDirectory);
        var fileName = VideoFileNames.ForPost(post);

        if (await GetTrackedOrThrowIfConflictAsync(directory, fileName, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        EnsureDirectory(directory);
        var partPath = NewPartPath(directory, fileName);
        try
        {
            var (sha256, size) = await DownloadAsync(_api.GetDownloadUri(post.Id), partPath, progress, cancellationToken).ConfigureAwait(false);

            var entry = new ManifestEntry
            {
                FileName = fileName,
                MoviesDirectory = directory,
                Source = InstalledVideoSource.SteamDeckRepo,
                PostId = post.Id,
                Title = post.Title,
                Author = post.Author.Name,
                Type = post.Type,
                PageUri = post.PageUri,
                ThumbnailUri = post.ThumbnailUri,
                Sha256 = sha256,
                SizeBytes = size,
            };

            return await CommitAsync(partPath, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(partPath);
        }
    }

    /// <summary>Copies a local <c>.webm</c> into the movies folder and tracks it like a downloaded video.</summary>
    /// <exception cref="InstallException">Missing, unreadable or invalid file, conflict or disk failure.</exception>
    public async Task<InstalledVideo> ImportLocalFileAsync(string sourcePath, string moviesDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var directory = NormalizeMoviesDirectory(moviesDirectory);

        if (!sourcePath.EndsWith(VideoFileNames.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallException(InstallErrorKind.InvalidFile, "Steam only plays .webm files.");
        }

        if (!_fileSystem.File.Exists(sourcePath))
        {
            throw new InstallException(InstallErrorKind.FileSystem, "The selected file does not exist.");
        }

        EnsureDirectory(directory);
        var partPath = NewPartPath(directory, "import.webm");
        try
        {
            string sha256;
            long size;
            try
            {
                await using var source = _fileSystem.FileStream.New(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                (sha256, size) = await CopyVerifiedAsync(source, partPath, source.Length, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InstallException(InstallErrorKind.FileSystem, "The selected file could not be read.", ex);
            }

            var fileName = VideoFileNames.ForLocalImport(_fileSystem.Path.GetFileName(sourcePath), sha256);
            if (await GetTrackedOrThrowIfConflictAsync(directory, fileName, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return existing;
            }

            var entry = new ManifestEntry
            {
                FileName = fileName,
                MoviesDirectory = directory,
                Source = InstalledVideoSource.LocalImport,
                Title = _fileSystem.Path.GetFileNameWithoutExtension(sourcePath),
                Type = VideoType.BootVideo,
                Sha256 = sha256,
                SizeBytes = size,
            };

            return await CommitAsync(partPath, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(partPath);
        }
    }

    /// <summary>
    /// Lists the <c>.webm</c> files of the movies folder with their tracking status, and drops manifest
    /// entries whose file has disappeared.
    /// </summary>
    /// <exception cref="InstallException">The folder or the manifest cannot be read.</exception>
    public async Task<IReadOnlyList<InstalledVideo>> GetInstalledAsync(string moviesDirectory, CancellationToken cancellationToken = default)
    {
        var directory = NormalizeMoviesDirectory(moviesDirectory);

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = LoadManifest();
            var entries = manifest.Entries.Where(e => IsInDirectory(e, directory)).ToList();
            var replacements = new Dictionary<ManifestEntry, ManifestEntry>();
            var videos = new List<InstalledVideo>();
            var presentNames = new HashSet<string>(PathUtilities.FileNameComparer);

            foreach (var path in ListVideoFiles(directory))
            {
                var name = _fileSystem.Path.GetFileName(path);
                presentNames.Add(name);
                var size = _fileSystem.FileInfo.New(path).Length;
                var entry = entries.FirstOrDefault(e => string.Equals(e.FileName, name, PathUtilities.FileNameComparison));

                if (entry is null)
                {
                    videos.Add(new InstalledVideo(name, path, size, InstalledVideoStatus.Untracked, null));
                    continue;
                }

                var (status, current) = await VerifyAsync(entry, path, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(current, entry))
                {
                    replacements[entry] = current;
                }

                videos.Add(new InstalledVideo(name, path, size, status, current));
            }

            var vanished = entries.Where(e => !presentNames.Contains(e.FileName)).ToHashSet();
            if (vanished.Count > 0 || replacements.Count > 0)
            {
                SaveManifest(manifest with
                {
                    Entries = manifest.Entries
                        .Where(e => !vanished.Contains(e))
                        .Select(e => replacements.GetValueOrDefault(e, e))
                        .ToArray(),
                });
            }

            return videos.OrderBy(v => v.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    /// Deletes one video. Files not installed by the app, or modified since, are only deleted when
    /// <paramref name="userConfirmed"/> is true.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a bare <c>.webm</c> name.</exception>
    /// <exception cref="InstallException">The file could not be deleted (e.g. locked by Steam).</exception>
    public async Task<UninstallOutcome> UninstallAsync(
        string moviesDirectory,
        string fileName,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!VideoFileNames.IsSafeFileName(fileName))
        {
            throw new ArgumentException("Expected a .webm file name located directly in the movies folder.", nameof(fileName));
        }

        var directory = NormalizeMoviesDirectory(moviesDirectory);
        var path = _fileSystem.Path.Combine(directory, fileName);

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = LoadManifest();
            var entry = FindEntry(manifest, directory, fileName);

            if (!_fileSystem.File.Exists(path))
            {
                if (entry is not null)
                {
                    SaveManifest(Without(manifest, entry));
                }

                return UninstallOutcome.AlreadyGone;
            }

            var status = entry is null
                ? InstalledVideoStatus.Untracked
                : (await VerifyAsync(entry, path, cancellationToken).ConfigureAwait(false)).Status;

            if (status != InstalledVideoStatus.Tracked && !userConfirmed)
            {
                return UninstallOutcome.RequiresConfirmation;
            }

            try
            {
                _fileSystem.File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InstallException(InstallErrorKind.FileSystem, $"Could not delete \"{fileName}\". Is Steam playing it?", ex);
            }

            if (entry is not null)
            {
                SaveManifest(Without(manifest, entry));
            }

            return UninstallOutcome.Deleted;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>Removes every video of the folder; untracked or modified ones only with <paramref name="includeUnconfirmed"/>.</summary>
    public async Task<UninstallAllResult> UninstallAllAsync(string moviesDirectory, bool includeUnconfirmed, CancellationToken cancellationToken = default)
    {
        var videos = await GetInstalledAsync(moviesDirectory, cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        var requiringConfirmation = 0;
        var failed = new List<string>();

        foreach (var video in videos)
        {
            try
            {
                switch (await UninstallAsync(moviesDirectory, video.FileName, includeUnconfirmed, cancellationToken).ConfigureAwait(false))
                {
                    case UninstallOutcome.Deleted:
                        deleted++;
                        break;
                    case UninstallOutcome.RequiresConfirmation:
                        requiringConfirmation++;
                        break;
                }
            }
            catch (InstallException)
            {
                failed.Add(video.FileName);
            }
        }

        return new UninstallAllResult(deleted, requiringConfirmation, failed);
    }

    public void Dispose() => _manifestLock.Dispose();

    /// <summary>
    /// Returns the tracked, intact file if it is already installed; throws if a foreign or modified file
    /// occupies the name; returns <c>null</c> if the name is free.
    /// </summary>
    private async Task<InstalledVideo?> GetTrackedOrThrowIfConflictAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        var path = _fileSystem.Path.Combine(directory, fileName);

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fileSystem.File.Exists(path))
            {
                return null;
            }

            var entry = FindEntry(LoadManifest(), directory, fileName)
                ?? throw new InstallException(InstallErrorKind.FileConflict, $"\"{fileName}\" already exists and was not added by this application.");

            var (status, current) = await VerifyAsync(entry, path, cancellationToken).ConfigureAwait(false);
            return status == InstalledVideoStatus.Tracked
                ? new InstalledVideo(fileName, path, current.SizeBytes, status, current)
                : throw new InstallException(InstallErrorKind.FileConflict, $"\"{fileName}\" was modified outside the application.");
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private async Task<(string Sha256, long Size)> DownloadAsync(Uri uri, string partPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException(InstallErrorKind.Download, "Could not reach the download server.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallException(InstallErrorKind.Download, "The download server did not answer in time.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InstallException(
                    InstallErrorKind.Download,
                    response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Too many downloads in a short time. Try again in a minute."
                        : $"The download server answered HTTP {(int)response.StatusCode}.");
            }

            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength > MaxVideoBytes)
            {
                throw new InstallException(InstallErrorKind.InvalidFile, "The video is unreasonably large.");
            }

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await CopyVerifiedAsync(source, partPath, expectedLength, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new InstallException(InstallErrorKind.Download, "The download was interrupted.", ex);
            }
        }
    }

    /// <summary>Streams to <paramref name="destinationPath"/> while hashing, checking the WebM signature as early as possible.</summary>
    private async Task<(string Sha256, long Size)> CopyVerifiedAsync(
        Stream source,
        string destinationPath,
        long? expectedLength,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var signature = new byte[WebmSignature.Length];
        var signatureLength = 0;
        long total = 0;

        try
        {
            await using var destination = _fileSystem.FileStream.New(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (signatureLength < signature.Length)
                {
                    var count = Math.Min(read, signature.Length - signatureLength);
                    Array.Copy(buffer, 0, signature, signatureLength, count);
                    signatureLength += count;
                    if (signatureLength == signature.Length && !signature.AsSpan().SequenceEqual(WebmSignature))
                    {
                        throw NotWebm();
                    }
                }

                total += read;
                if (total > MaxVideoBytes)
                {
                    throw new InstallException(InstallErrorKind.InvalidFile, "The video is unreasonably large.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                progress?.Report(new DownloadProgress(total, expectedLength));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (signatureLength < signature.Length)
        {
            throw NotWebm();
        }

        if (expectedLength is { } expected && expected != total)
        {
            throw new InstallException(InstallErrorKind.Download, "The download is incomplete.");
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);

        static InstallException NotWebm() => new(InstallErrorKind.InvalidFile, "The file is not a WebM video.");
    }

    /// <summary>Moves the verified file into place and records it; rolls the file back if the manifest cannot be saved.</summary>
    private async Task<InstalledVideo> CommitAsync(string partPath, ManifestEntry entry, CancellationToken cancellationToken)
    {
        var targetPath = _fileSystem.Path.Combine(entry.MoviesDirectory, entry.FileName);

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fileSystem.File.Exists(targetPath))
            {
                throw new InstallException(InstallErrorKind.FileConflict, $"\"{entry.FileName}\" appeared in the movies folder during the download.");
            }

            try
            {
                _fileSystem.File.Move(partPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InstallException(InstallErrorKind.FileSystem, "The video could not be placed in the movies folder.", ex);
            }

            var committed = entry with
            {
                LastWriteTimeUtc = LastWriteTime(targetPath),
                InstalledAt = _timeProvider.GetUtcNow(),
            };

            try
            {
                var manifest = LoadManifest();
                SaveManifest(manifest with
                {
                    Entries = [.. manifest.Entries.Where(e => !IsSameFile(e, committed)), committed],
                });
            }
            catch (InstallException)
            {
                DeleteQuietly(targetPath); // Never leave an untracked file behind.
                throw;
            }

            return new InstalledVideo(committed.FileName, targetPath, committed.SizeBytes, InstalledVideoStatus.Tracked, committed);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>Checks a tracked file: size, then modification time, then hash only if the time changed.</summary>
    private async Task<(InstalledVideoStatus Status, ManifestEntry Entry)> VerifyAsync(ManifestEntry entry, string path, CancellationToken cancellationToken)
    {
        if (_fileSystem.FileInfo.New(path).Length != entry.SizeBytes)
        {
            return (InstalledVideoStatus.Modified, entry);
        }

        var lastWrite = LastWriteTime(path);
        if (lastWrite == entry.LastWriteTimeUtc)
        {
            return (InstalledVideoStatus.Tracked, entry);
        }

        string sha256;
        try
        {
            await using var stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, $"\"{entry.FileName}\" could not be read.", ex);
        }

        return string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
            ? (InstalledVideoStatus.Tracked, entry with { LastWriteTimeUtc = lastWrite })
            : (InstalledVideoStatus.Modified, entry);
    }

    private List<string> ListVideoFiles(string directory)
    {
        try
        {
            return _fileSystem.Directory.Exists(directory)
                ? _fileSystem.Directory
                    .EnumerateFiles(directory, "*" + VideoFileNames.Extension, SearchOption.TopDirectoryOnly)
                    .Where(path => VideoFileNames.IsSafeFileName(_fileSystem.Path.GetFileName(path)))
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, "The movies folder could not be read.", ex);
        }
    }

    private Manifest LoadManifest()
    {
        try
        {
            return _manifestStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, "The install manifest could not be read.", ex);
        }
    }

    private void SaveManifest(Manifest manifest)
    {
        try
        {
            _manifestStore.Save(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, "The install manifest could not be saved.", ex);
        }
    }

    private string NormalizeMoviesDirectory(string moviesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moviesDirectory);
        return PathUtilities.NormalizeDirectory(_fileSystem, moviesDirectory);
    }

    private void EnsureDirectory(string directory)
    {
        try
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, $"The movies folder could not be created: {directory}", ex);
        }
    }

    private string NewPartPath(string directory, string fileName) =>
        _fileSystem.Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.part");

    private DateTimeOffset LastWriteTime(string path) =>
        new(DateTime.SpecifyKind(_fileSystem.File.GetLastWriteTimeUtc(path), DateTimeKind.Utc));

    private bool IsInDirectory(ManifestEntry entry, string directory) =>
        PathUtilities.SameDirectory(_fileSystem, entry.MoviesDirectory, directory);

    private bool IsSameFile(ManifestEntry left, ManifestEntry right) =>
        string.Equals(left.FileName, right.FileName, PathUtilities.FileNameComparison)
        && PathUtilities.SameDirectory(_fileSystem, left.MoviesDirectory, right.MoviesDirectory);

    private ManifestEntry? FindEntry(Manifest manifest, string directory, string fileName) =>
        manifest.Entries.FirstOrDefault(e =>
            string.Equals(e.FileName, fileName, PathUtilities.FileNameComparison) && IsInDirectory(e, directory));

    private static Manifest Without(Manifest manifest, ManifestEntry entry) =>
        manifest with { Entries = manifest.Entries.Where(e => !ReferenceEquals(e, entry)).ToArray() };

    private void DeleteQuietly(string path)
    {
        try
        {
            if (_fileSystem.File.Exists(path))
            {
                _fileSystem.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temporary file: harmless (hidden, not .webm) and retried on the next install.
        }
    }
}
