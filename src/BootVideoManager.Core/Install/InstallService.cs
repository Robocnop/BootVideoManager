using System.IO.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Localization;
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

    /// <summary>Attempts for one download: the first transfer plus resumes after interruptions.</summary>
    private const int MaxDownloadAttempts = 4;

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
            throw new InstallException(InstallErrorKind.InvalidFile, Loc.T("Steam ne lit que les fichiers .webm.", "Steam only plays .webm files."));
        }

        if (!_fileSystem.File.Exists(sourcePath))
        {
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Le fichier sélectionné n'existe pas.", "The selected file does not exist."));
        }

        EnsureDirectory(directory);
        var partPath = NewPartPath(directory, "import.webm");
        try
        {
            var (sha256, size) = await CopyLocalFileAsync(sourcePath, partPath, Loc.T("Impossible de lire le fichier sélectionné.", "Could not read the selected file."), cancellationToken).ConfigureAwait(false);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically a file removed or locked by Steam between listing and reading its size.
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible de lire le dossier des vidéos. Réessayez dans un instant.", "Could not read the videos folder. Try again in a moment."), ex);
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
                throw new InstallException(InstallErrorKind.FileSystem, Loc.T($"Impossible de supprimer « {fileName} ». Steam est peut-être en train de la lire.", $"Could not delete “{fileName}”. Steam may be playing it."), ex);
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
            throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {video.FileName} » est gérée par Steam et ne peut pas être supprimée.", $"“{video.FileName}” is managed by Steam and cannot be deleted."));
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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T($"Impossible de supprimer « {video.FileName} ». Steam est peut-être en train de la lire.", $"Could not delete “{video.FileName}”. Steam may be playing it."), ex);
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
                throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {copyName} » a été modifiée en dehors de l'application : supprimez-la plutôt depuis la liste.", $"“{copyName}” was modified outside the app: delete it from the list instead."));
            }

            return;
        }

        if (video.IsSteamShopItem)
        {
            throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {video.FileName} » provient de la Boutique des points Steam : gérez-la depuis Steam › Paramètres › Personnalisation.", $"“{video.FileName}” comes from the Steam Points Shop: manage it in Steam › Settings › Customization."));
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
                throw new InstallException(InstallErrorKind.FileSystem, Loc.T($"« {video.FileName} » n'existe plus.", $"“{video.FileName}” no longer exists."));
            }

            if (targetExists)
            {
                throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {video.FileName} » se trouve à la fois dans son dossier et dans le dossier des vidéos désactivées.", $"“{video.FileName}” is both in its folder and in the disabled videos folder."));
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
                ?? throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {fileName} » existe déjà et n'a pas été ajoutée par cette application.", $"“{fileName}” already exists and was not added by this app."));

            var (status, current) = await VerifyAsync(entry, existingPath, cancellationToken).ConfigureAwait(false);
            if (status != InstalledVideoStatus.Tracked)
            {
                throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {fileName} » a été modifiée en dehors de l'application.", $"“{fileName}” was modified outside the app."));
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
            ?? throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible de trouver le dossier des animations d'origine de Steam.", "Could not find Steam's stock animations folder."));
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
                Loc.T($"Impossible de lire l'animation de Steam « {builtInFileName} ».", $"Could not read the Steam animation “{builtInFileName}”."),
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
        await using var writer = VerifiedFileWriter.Create(_fileSystem, partPath, MaxVideoBytes);
        try
        {
            await using var source = _fileSystem.FileStream.New(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            await writer.CopyFromAsync(source, source.Length, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallErrorKind.FileSystem, readError, ex);
        }

        return await writer.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the video into <paramref name="partPath"/>. A transfer cut mid-way is resumed where it stopped (HTTP
    /// range request), or restarted when the server ignores the range, a few times before giving up.
    /// </summary>
    private async Task<(string Sha256, long Size)> DownloadAsync(Uri uri, string partPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await using var writer = VerifiedFileWriter.Create(_fileSystem, partPath, MaxVideoBytes);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            if (writer.Length > 0)
            {
                request.Headers.Range = new RangeHeaderValue(writer.Length, null);
            }

            using var response = await SendDownloadRequestAsync(request, cancellationToken).ConfigureAwait(false);

            long? expectedTotal;
            if (writer.Length > 0
                && response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { From: { } from } range
                && from == writer.Length)
            {
                expectedTotal = range.Length ?? writer.Length + response.Content.Headers.ContentLength;
            }
            else
            {
                if (writer.Length > 0)
                {
                    writer.Reset(); // The server ignored the range and sends the whole file again.
                }

                expectedTotal = response.Content.Headers.ContentLength;
            }

            if (expectedTotal > MaxVideoBytes)
            {
                throw new InstallException(InstallErrorKind.InvalidFile, Loc.T("La vidéo est anormalement volumineuse.", "The video is abnormally large."));
            }

            Exception? interruption = null;
            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await writer.CopyFromAsync(source, expectedTotal, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                interruption = ex; // Network failure while reading: disk failures are InstallException.
            }

            if (interruption is null && (expectedTotal is not { } expected || writer.Length == expected))
            {
                return await writer.CompleteAsync().ConfigureAwait(false);
            }

            if (interruption is null && writer.Length > expectedTotal)
            {
                throw new InstallException(InstallErrorKind.Download, Loc.T("Le serveur a envoyé plus de données qu'annoncé.", "The server sent more data than announced."));
            }

            if (attempt >= MaxDownloadAttempts)
            {
                throw new InstallException(
                    InstallErrorKind.Download,
                    interruption is null
                        ? Loc.T("Le téléchargement est incomplet.", "The download is incomplete.")
                        : Loc.T("Le téléchargement a été interrompu.", "The download was interrupted."),
                    interruption);
            }

            await Task.Delay(_options.Retry.BaseDelay * attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a download request; connection failures and HTTP errors become <see cref="InstallException"/>.</summary>
    private async Task<HttpResponseMessage> SendDownloadRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException(InstallErrorKind.Download, Loc.T("Impossible de joindre le serveur de téléchargement.", "Could not reach the download server."), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallException(InstallErrorKind.Download, Loc.T("Le serveur de téléchargement n'a pas répondu à temps.", "The download server did not answer in time."), ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new InstallException(
                InstallErrorKind.Download,
                status == (int)HttpStatusCode.TooManyRequests
                    ? Loc.T("Trop de téléchargements en peu de temps. Réessayez dans une minute.", "Too many downloads in a short time. Try again in a minute.")
                    : Loc.T($"Le serveur de téléchargement a répondu par une erreur HTTP {status}.", $"The download server answered HTTP {status}."));
        }

        return response;
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
                throw new InstallException(InstallErrorKind.FileConflict, Loc.T($"« {entry.FileName} » est apparue dans le dossier des vidéos pendant le téléchargement.", $"“{entry.FileName}” appeared in the videos folder during the download."));
            }

            try
            {
                _fileSystem.File.Move(partPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible de placer la vidéo dans le dossier des vidéos.", "Could not move the video into the videos folder."), ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T($"Impossible de lire « {entry.FileName} ».", $"Could not read “{entry.FileName}”."), ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible de lire le dossier des vidéos.", "Could not read the videos folder."), ex);
        }
    }

    private string SteamCacheDirectory(string directory) =>
        BuiltInVideos.SteamCacheDirectoryFor(_fileSystem, directory)
        ?? throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible de trouver le cache des vidéos de démarrage de Steam.", "Could not find Steam's startup movie cache."));

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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T($"Impossible de déplacer « {_fileSystem.Path.GetFileName(from)} ». Steam est peut-être en train de la lire.", $"Could not move “{_fileSystem.Path.GetFileName(from)}”. Steam may be playing it."), ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible de lire le registre des installations.", "Could not read the install registry."), ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T("Impossible d'enregistrer le registre des installations.", "Could not save the install registry."), ex);
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
            throw new InstallException(InstallErrorKind.FileSystem, Loc.T($"Impossible de créer le dossier des vidéos : {directory}", $"Could not create the videos folder: {directory}"), ex);
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
