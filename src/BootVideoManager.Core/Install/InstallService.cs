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
            throw new InstallException(InstallErrorKind.InvalidFile, "Steam ne lit que les fichiers .webm.");
        }

        if (!_fileSystem.File.Exists(sourcePath))
        {
            throw new InstallException(InstallErrorKind.FileSystem, "Le fichier sélectionné n'existe pas.");
        }

        EnsureDirectory(directory);
        var partPath = NewPartPath(directory, "import.webm");
        try
        {
            var (sha256, size) = await CopyLocalFileAsync(sourcePath, partPath, "Impossible de lire le fichier sélectionné.", cancellationToken).ConfigureAwait(false);

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
    /// Lists the <c>.webm</c> files of the movies folder (enabled) and of its disabled sibling folder with their
    /// tracking status, plus Steam's stock animations, and drops manifest entries whose file has disappeared.
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
            var builtInCopies = new Dictionary<string, InstalledVideo>(PathUtilities.FileNameComparer);
            var files = ListVideoFiles(directory).Select(path => (Path: path, Enabled: true))
                .Concat(ListVideoFiles(BuiltInVideos.DisabledDirectoryFor(directory)).Select(path => (Path: path, Enabled: false)));

            foreach (var (path, enabled) in files)
            {
                var name = _fileSystem.Path.GetFileName(path);
                presentNames.Add(name);
                var size = _fileSystem.FileInfo.New(path).Length;
                var entry = entries.FirstOrDefault(e => string.Equals(e.FileName, name, PathUtilities.FileNameComparison));

                if (entry is null)
                {
                    videos.Add(new InstalledVideo(name, path, size, InstalledVideoStatus.Untracked, null) { IsEnabled = enabled });
                    continue;
                }

                var (status, current) = await VerifyAsync(entry, path, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(current, entry))
                {
                    replacements[entry] = current;
                }

                var video = new InstalledVideo(name, path, size, status, current) { IsEnabled = enabled };
                if (enabled && status == InstalledVideoStatus.Tracked && current.Source == InstalledVideoSource.SteamBuiltIn)
                {
                    builtInCopies[name] = video; // Shown as the "enabled" state of its stock animation.
                    continue;
                }

                videos.Add(video);
            }

            if (BuiltInVideos.DirectoryFor(_fileSystem, directory) is { } builtInDirectory)
            {
                foreach (var path in ListVideoFiles(builtInDirectory))
                {
                    var name = _fileSystem.Path.GetFileName(path);
                    if (!BuiltInVideos.IsSelectable(name))
                    {
                        continue;
                    }

                    builtInCopies.Remove(BuiltInVideos.CopyFileName(name), out var copy);
                    videos.Add(new InstalledVideo(name, path, _fileSystem.FileInfo.New(path).Length, InstalledVideoStatus.BuiltIn, copy?.Entry)
                    {
                        IsEnabled = copy is not null,
                    });
                }
            }

            videos.AddRange(builtInCopies.Values); // Stock animation removed by a Steam update: the copy is an ordinary video.

            // Steam also shuffles the videos of its startup movie cache; they are never in the manifest.
            if (BuiltInVideos.SteamCacheDirectoryFor(_fileSystem, directory) is { } cacheDirectory)
            {
                var cacheFiles = ListVideoFiles(cacheDirectory).Select(path => (Path: path, Enabled: true))
                    .Concat(ListVideoFiles(BuiltInVideos.DisabledDirectoryFor(cacheDirectory)).Select(path => (Path: path, Enabled: false)));

                videos.AddRange(cacheFiles.Select(file => new InstalledVideo(
                    _fileSystem.Path.GetFileName(file.Path),
                    file.Path,
                    _fileSystem.FileInfo.New(file.Path).Length,
                    InstalledVideoStatus.Untracked,
                    null)
                {
                    IsEnabled = file.Enabled,
                    IsInSteamCache = true,
                }));
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

            return videos
                .OrderBy(v => v.IsBuiltIn)
                .ThenBy(v => v.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    /// Deletes one video, enabled or disabled. Files not installed by the app, or modified since, are only deleted when
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

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = LoadManifest();
            var entry = FindEntry(manifest, directory, fileName);

            if (FindVideoFile(directory, fileName) is not { } path)
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
                throw new InstallException(InstallErrorKind.FileSystem, $"Impossible de supprimer « {fileName} ». Steam est peut-être en train de la lire.", ex);
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

    /// <summary>
    /// Deletes a listed video, wherever it lives: the movies folder (see the file name overload) or Steam's startup
    /// movie cache, whose files are never the app's own and always require <paramref name="userConfirmed"/>.
    /// </summary>
    /// <exception cref="InstallException">Stock animation or Points Shop item (managed by Steam), or the file could not be deleted.</exception>
    public async Task<UninstallOutcome> UninstallAsync(
        string moviesDirectory,
        InstalledVideo video,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.IsBuiltIn || video.IsSteamShopItem)
        {
            throw new InstallException(InstallErrorKind.FileConflict, $"« {video.FileName} » est gérée par Steam et ne peut pas être supprimée.");
        }

        if (!video.IsInSteamCache)
        {
            return await UninstallAsync(moviesDirectory, video.FileName, userConfirmed, cancellationToken).ConfigureAwait(false);
        }

        if (!VideoFileNames.IsSafeFileName(video.FileName))
        {
            throw new ArgumentException("Expected a .webm file name.", nameof(video));
        }

        var cacheDirectory = SteamCacheDirectory(NormalizeMoviesDirectory(moviesDirectory));
        if (FindVideoFile(cacheDirectory, video.FileName) is not { } path)
        {
            return UninstallOutcome.AlreadyGone;
        }

        if (!userConfirmed)
        {
            return UninstallOutcome.RequiresConfirmation;
        }

        try
        {
            _fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, $"Impossible de supprimer « {video.FileName} ». Steam est peut-être en train de la lire.", ex);
        }

        return UninstallOutcome.Deleted;
    }

    /// <summary>
    /// Removes every video of the folder; untracked or modified ones only with <paramref name="includeUnconfirmed"/>.
    /// Stock Steam animations are only disabled (their copy is removed), never deleted; Points Shop items are left to Steam.
    /// </summary>
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
                if (video.IsBuiltIn)
                {
                    if (video.IsEnabled)
                    {
                        await SetEnabledAsync(moviesDirectory, video, enabled: false, cancellationToken).ConfigureAwait(false);
                        deleted++;
                    }

                    continue;
                }

                if (video.IsSteamShopItem)
                {
                    continue;
                }

                switch (await UninstallAsync(moviesDirectory, video, includeUnconfirmed, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// Makes a video playable by Steam or not, without downloading or deleting anything the user cares about:
    /// other videos move between the movies folder and its disabled sibling; a stock Steam animation is enabled
    /// by copying it into the movies folder and disabled by removing that copy (the original is never touched).
    /// </summary>
    /// <exception cref="ArgumentException">The video's file name is not a bare <c>.webm</c> name.</exception>
    /// <exception cref="InstallException">Missing file, name conflict, modified copy or disk failure.</exception>
    public async Task SetEnabledAsync(string moviesDirectory, InstalledVideo video, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (!VideoFileNames.IsSafeFileName(video.FileName))
        {
            throw new ArgumentException("Expected a .webm file name.", nameof(video));
        }

        var directory = NormalizeMoviesDirectory(moviesDirectory);
        if (video.IsBuiltIn)
        {
            var copyName = BuiltInVideos.CopyFileName(video.FileName);
            if (enabled)
            {
                await EnableBuiltInAsync(directory, video.FileName, cancellationToken).ConfigureAwait(false);
            }
            else if (await UninstallAsync(directory, copyName, userConfirmed: false, cancellationToken).ConfigureAwait(false) == UninstallOutcome.RequiresConfirmation)
            {
                throw new InstallException(InstallErrorKind.FileConflict, $"« {copyName} » a été modifiée en dehors de l'application : supprimez-la plutôt depuis la liste.");
            }

            return;
        }

        if (video.IsSteamShopItem)
        {
            throw new InstallException(InstallErrorKind.FileConflict, $"« {video.FileName} » provient de la Boutique des points Steam : gérez-la depuis Steam › Paramètres › Personnalisation.");
        }

        // Videos of Steam's startup movie cache are disabled next to that cache, the others next to the movies folder.
        var home = video.IsInSteamCache ? SteamCacheDirectory(directory) : directory;
        var enabledPath = _fileSystem.Path.Combine(home, video.FileName);
        var disabledPath = _fileSystem.Path.Combine(BuiltInVideos.DisabledDirectoryFor(home), video.FileName);
        var (from, to) = enabled ? (disabledPath, enabledPath) : (enabledPath, disabledPath);

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sourceExists = _fileSystem.File.Exists(from);
            var targetExists = _fileSystem.File.Exists(to);
            if (targetExists && !sourceExists)
            {
                return;
            }

            if (!sourceExists)
            {
                throw new InstallException(InstallErrorKind.FileSystem, $"« {video.FileName} » n'existe plus.");
            }

            if (targetExists)
            {
                throw new InstallException(InstallErrorKind.FileConflict, $"« {video.FileName} » se trouve à la fois dans son dossier et dans le dossier des vidéos désactivées.");
            }

            EnsureDirectory(_fileSystem.Path.GetDirectoryName(to)!);
            MoveVideo(from, to);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    public void Dispose() => _manifestLock.Dispose();

    /// <summary>
    /// Returns the tracked, intact file if it is already installed (re-enabling it if it was disabled); throws if a
    /// foreign or modified file occupies the name; returns <c>null</c> if the name is free.
    /// </summary>
    private async Task<InstalledVideo?> GetTrackedOrThrowIfConflictAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        var path = _fileSystem.Path.Combine(directory, fileName);

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FindVideoFile(directory, fileName) is not { } existingPath)
            {
                return null;
            }

            var entry = FindEntry(LoadManifest(), directory, fileName)
                ?? throw new InstallException(InstallErrorKind.FileConflict, $"« {fileName} » existe déjà et n'a pas été ajoutée par cette application.");

            var (status, current) = await VerifyAsync(entry, existingPath, cancellationToken).ConfigureAwait(false);
            if (status != InstalledVideoStatus.Tracked)
            {
                throw new InstallException(InstallErrorKind.FileConflict, $"« {fileName} » a été modifiée en dehors de l'application.");
            }

            if (existingPath != path)
            {
                MoveVideo(existingPath, path); // Installing a disabled video re-enables it.
            }

            return new InstalledVideo(fileName, path, current.SizeBytes, status, current);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>Copies a stock Steam animation into the movies folder and tracks the copy.</summary>
    private async Task EnableBuiltInAsync(string directory, string builtInFileName, CancellationToken cancellationToken)
    {
        var builtInDirectory = BuiltInVideos.DirectoryFor(_fileSystem, directory)
            ?? throw new InstallException(InstallErrorKind.FileSystem, "Impossible de trouver le dossier des animations d'origine de Steam.");
        var copyName = BuiltInVideos.CopyFileName(builtInFileName);

        if (await GetTrackedOrThrowIfConflictAsync(directory, copyName, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        EnsureDirectory(directory);
        var partPath = NewPartPath(directory, copyName);
        try
        {
            var (sha256, size) = await CopyLocalFileAsync(
                _fileSystem.Path.Combine(builtInDirectory, builtInFileName),
                partPath,
                $"Impossible de lire l'animation de Steam « {builtInFileName} ».",
                cancellationToken).ConfigureAwait(false);

            var entry = new ManifestEntry
            {
                FileName = copyName,
                MoviesDirectory = directory,
                Source = InstalledVideoSource.SteamBuiltIn,
                Title = BuiltInVideos.TitleOf(builtInFileName),
                Type = BuiltInVideos.TypeOf(builtInFileName),
                Sha256 = sha256,
                SizeBytes = size,
            };

            await CommitAsync(partPath, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(partPath);
        }
    }

    private async Task<(string Sha256, long Size)> CopyLocalFileAsync(string sourcePath, string partPath, string readError, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = _fileSystem.FileStream.New(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            return await CopyVerifiedAsync(source, partPath, source.Length, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, readError, ex);
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
            throw new InstallException(InstallErrorKind.Download, "Impossible de joindre le serveur de téléchargement.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallException(InstallErrorKind.Download, "Le serveur de téléchargement n'a pas répondu à temps.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InstallException(
                    InstallErrorKind.Download,
                    response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Trop de téléchargements en peu de temps. Réessayez dans une minute."
                        : $"Le serveur de téléchargement a répondu par une erreur HTTP {(int)response.StatusCode}.");
            }

            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength > MaxVideoBytes)
            {
                throw new InstallException(InstallErrorKind.InvalidFile, "La vidéo est anormalement volumineuse.");
            }

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await CopyVerifiedAsync(source, partPath, expectedLength, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new InstallException(InstallErrorKind.Download, "Le téléchargement a été interrompu.", ex);
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
                    throw new InstallException(InstallErrorKind.InvalidFile, "La vidéo est anormalement volumineuse.");
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
            throw new InstallException(InstallErrorKind.Download, "Le téléchargement est incomplet.");
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);

        static InstallException NotWebm() => new(InstallErrorKind.InvalidFile, "Ce fichier n'est pas une vidéo WebM.");
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
                throw new InstallException(InstallErrorKind.FileConflict, $"« {entry.FileName} » est apparue dans le dossier des vidéos pendant le téléchargement.");
            }

            try
            {
                _fileSystem.File.Move(partPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InstallException(InstallErrorKind.FileSystem, "Impossible de placer la vidéo dans le dossier des vidéos.", ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, $"Impossible de lire « {entry.FileName} ».", ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, "Impossible de lire le dossier des vidéos.", ex);
        }
    }

    private string SteamCacheDirectory(string directory) =>
        BuiltInVideos.SteamCacheDirectoryFor(_fileSystem, directory)
        ?? throw new InstallException(InstallErrorKind.FileSystem, "Impossible de trouver le cache des vidéos de démarrage de Steam.");

    /// <summary>Path of <paramref name="fileName"/> in <paramref name="directory"/>, else in its disabled folder; <c>null</c> if in neither.</summary>
    private string? FindVideoFile(string directory, string fileName) =>
        new[] { directory, BuiltInVideos.DisabledDirectoryFor(directory) }
            .Select(folder => _fileSystem.Path.Combine(folder, fileName))
            .FirstOrDefault(_fileSystem.File.Exists);

    private void MoveVideo(string from, string to)
    {
        try
        {
            _fileSystem.File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, $"Impossible de déplacer « {_fileSystem.Path.GetFileName(from)} ». Steam est peut-être en train de la lire.", ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, "Impossible de lire le registre des installations.", ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, "Impossible d'enregistrer le registre des installations.", ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, $"Impossible de créer le dossier des vidéos : {directory}", ex);
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
