using BootVideoManager.Core.Install;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Models;
using BootVideoManager.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BootVideoManager.App.Services;

/// <summary>
/// Single source of truth for "what is installed in the selected Steam folder", shared by the catalog and the
/// installed-videos tab. Wraps <see cref="InstallService"/> with confirmations, a download queue and user-facing
/// messages.
/// </summary>
public sealed partial class InstallCoordinator : ObservableObject, IDisposable
{
    /// <summary>Downloads running at the same time; the others wait their turn (polite to the site, fast enough).</summary>
    public const int MaxConcurrentDownloads = 2;

    private readonly InstallService _install;
    private readonly IDialogService _dialogs;
    private readonly INotifier _notifier;
    private readonly Dictionary<string, InstalledVideo> _byPostId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

    public InstallCoordinator(InstallService install, IDialogService dialogs, INotifier notifier)
    {
        _install = install;
        _dialogs = dialogs;
        _notifier = notifier;
    }

    /// <summary>Raised on the UI thread whenever <see cref="Installed"/> changes.</summary>
    public event EventHandler? InstalledChanged;

    /// <summary>Steam installation currently managed; <c>null</c> until one is found or chosen.</summary>
    [ObservableProperty]
    public partial SteamInstallation? Steam { get; set; }

    public IReadOnlyList<InstalledVideo> Installed { get; private set; } = [];

    private static string NoSteamMessage => Loc.T(
        "Aucun dossier Steam n'est sélectionné. Choisissez-le dans l'onglet Réglages.",
        "No Steam folder is selected. Choose it in the Settings tab.");

    public bool IsInstalled(string postId) => _byPostId.ContainsKey(postId);

    public void Dispose() => _downloadSlots.Dispose();

    public async Task RefreshAsync()
    {
        if (Steam is null)
        {
            SetInstalled([]);
            return;
        }

        try
        {
            SetInstalled(await _install.GetInstalledAsync(Steam.MoviesDirectory));
        }
        catch (InstallException ex)
        {
            Report(ex);
        }
    }

    /// <summary>Downloads and installs a post once a download slot is free.</summary>
    /// <param name="started">Called when the download actually starts (after waiting in the queue).</param>
    /// <returns><c>true</c> when the video ended up installed.</returns>
    public async Task<bool> InstallAsync(Post post, IProgress<DownloadProgress> progress, CancellationToken cancellationToken, Action? started = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (Steam is null)
        {
            _notifier.ShowError(NoSteamMessage);
            return false;
        }

        try
        {
            await _downloadSlots.WaitAsync(cancellationToken);
            try
            {
                started?.Invoke();
                await _install.InstallAsync(post, Steam.MoviesDirectory, progress, cancellationToken);
            }
            finally
            {
                _downloadSlots.Release();
            }

            AppLog.Info($"Installed post {post.Id} ({post.Title}).");
            _notifier.ShowInfo(Loc.T(
                $"« {post.Title} » est installée. Sélectionnez-la dans Steam › Paramètres › Personnalisation.",
                $"“{post.Title}” is installed. Select it in Steam › Settings › Customization."));
            return true;
        }
        catch (InstallException ex)
        {
            Report(ex);
            return false;
        }
        catch (OperationCanceledException)
        {
            _notifier.ShowInfo(Loc.T($"Téléchargement de « {post.Title} » annulé.", $"Download of “{post.Title}” cancelled."));
            return false;
        }
        finally
        {
            await RefreshAsync();
        }
    }

    public Task RemovePostAsync(string postId) =>
        _byPostId.TryGetValue(postId, out var video) ? RemoveAsync(video) : Task.CompletedTask;

    /// <summary>Deletes one video, asking first if the app did not install it or it was modified.</summary>
    public async Task RemoveAsync(InstalledVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (Steam is null)
        {
            return;
        }

        try
        {
            var outcome = await _install.UninstallAsync(Steam.MoviesDirectory, video, userConfirmed: false);
            if (outcome == UninstallOutcome.RequiresConfirmation)
            {
                var reason = video switch
                {
                    { Status: InstalledVideoStatus.Modified } => Loc.T(
                        $"« {video.DisplayTitle} » a été modifiée depuis son installation.",
                        $"“{video.DisplayTitle}” was modified since it was installed."),
                    { IsInSteamCache: true } => Loc.T(
                        $"« {video.FileName} » se trouve dans le cache des vidéos de démarrage de Steam et n'a pas été installée par cette application.\nAstuce : pour la retirer de la lecture aléatoire sans la supprimer, désactivez-la simplement.",
                        $"“{video.FileName}” is in Steam's startup movie cache and was not installed by this app.\nTip: to take it out of the shuffle without deleting it, simply disable it."),
                    _ => Loc.T(
                        $"« {video.FileName} » n'a pas été installée par cette application (ajout manuel ou par un autre outil).",
                        $"“{video.FileName}” was not installed by this app (added manually or by another tool)."),
                };

                if (!await _dialogs.ConfirmAsync(
                        Loc.T("Supprimer ce fichier ?", "Delete this file?"),
                        $"{reason}\n{Loc.T("La supprimer malgré tout ?", "Delete it anyway?")}",
                        Loc.T("Supprimer", "Delete"),
                        destructive: true))
                {
                    return;
                }

                outcome = await _install.UninstallAsync(Steam.MoviesDirectory, video, userConfirmed: true);
            }

            if (outcome == UninstallOutcome.Deleted)
            {
                AppLog.Info($"Deleted {video.FileName}.");
                _notifier.ShowInfo(Loc.T($"« {video.DisplayTitle} » a été supprimée.", $"“{video.DisplayTitle}” was deleted."));
            }
        }
        catch (InstallException ex)
        {
            Report(ex);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <summary>Makes a video playable at startup or not, without downloading or deleting it.</summary>
    public async Task SetEnabledAsync(InstalledVideo video, bool enabled)
    {
        if (Steam is null)
        {
            return;
        }

        try
        {
            await _install.SetEnabledAsync(Steam.MoviesDirectory, video, enabled);
        }
        catch (InstallException ex)
        {
            Report(ex);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// Removes everything the app installed after one confirmation, then asks separately about files it did not
    /// install, so user-added videos are never swept away by accident.
    /// </summary>
    public async Task RemoveAllAsync()
    {
        if (Steam is null || Installed.Count == 0)
        {
            return;
        }

        // Stock Steam animations are only disabled along with the tracked videos, never deleted.
        var tracked = Installed.Count(v => v.Status == InstalledVideoStatus.Tracked || (v.IsBuiltIn && v.IsEnabled));
        var others = Installed.Count(v => v.RequiresConfirmationToDelete && !v.IsSteamShopItem);
        var deleted = 0;
        var failed = new List<string>();

        try
        {
            if (tracked > 0)
            {
                var message = tracked == 1
                    ? Loc.T("Supprimer la vidéo installée par l'application ?", "Delete the video installed by the app?")
                    : Loc.T($"Supprimer les {tracked} vidéos installées par l'application ?", $"Delete the {tracked} videos installed by the app?");
                if (others > 0)
                {
                    message += others == 1
                        ? Loc.T("\nL'autre fichier sera traité ensuite, avec une confirmation distincte.", "\nThe other file will be handled next, with a separate confirmation.")
                        : Loc.T($"\nLes {others} autres fichiers seront traités ensuite, avec une confirmation distincte.", $"\nThe {others} other files will be handled next, with a separate confirmation.");
                }

                if (!await _dialogs.ConfirmAsync(Loc.T("Tout retirer", "Remove all"), message, Loc.T("Tout retirer", "Remove all"), destructive: true))
                {
                    return;
                }

                var result = await _install.UninstallAllAsync(Steam.MoviesDirectory, includeUnconfirmed: false);
                deleted += result.Deleted;
                failed.AddRange(result.Failed);
                others = result.RequiringConfirmation;
            }

            if (others > 0 && await _dialogs.ConfirmAsync(
                    Loc.T("Fichiers ajoutés hors de l'application", "Files added outside the app"),
                    others == 1
                        ? Loc.T(
                            "Un fichier du dossier n'a pas été installé par cette application ou a été modifié.\nLe supprimer aussi ?",
                            "One file in the folder was not installed by this app or was modified.\nDelete it too?")
                        : Loc.T(
                            $"{others} fichiers du dossier n'ont pas été installés par cette application ou ont été modifiés.\nLes supprimer aussi ?",
                            $"{others} files in the folder were not installed by this app or were modified.\nDelete them too?"),
                    Loc.T("Supprimer aussi", "Delete too"),
                    destructive: true))
            {
                var result = await _install.UninstallAllAsync(Steam.MoviesDirectory, includeUnconfirmed: true);
                deleted += result.Deleted;
                failed.AddRange(result.Failed);
            }

            var deletedText = deleted > 1
                ? Loc.T($"{deleted} vidéos supprimées.", $"{deleted} videos deleted.")
                : Loc.T($"{deleted} vidéo supprimée.", $"{deleted} video deleted.");
            if (failed.Count > 0)
            {
                var message = Loc.T(
                    $"{deletedText} Impossible de supprimer : {string.Join(", ", failed)} (Steam est peut-être en train de les lire).",
                    $"{deletedText} Could not delete: {string.Join(", ", failed)} (Steam may be playing them).");
                AppLog.Warn(message);
                _notifier.ShowError(message);
            }
            else if (deleted > 0)
            {
                _notifier.ShowInfo(deletedText);
            }
        }
        catch (InstallException ex)
        {
            Report(ex);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    public async Task ImportAsync(string path)
    {
        if (Steam is null)
        {
            _notifier.ShowError(NoSteamMessage);
            return;
        }

        try
        {
            var video = await _install.ImportLocalFileAsync(path, Steam.MoviesDirectory);
            AppLog.Info($"Imported {video.FileName}.");
            _notifier.ShowInfo(Loc.T(
                $"« {video.DisplayTitle} » a été importée. Sélectionnez-la dans Steam › Paramètres › Personnalisation.",
                $"“{video.DisplayTitle}” was imported. Select it in Steam › Settings › Customization."));
        }
        catch (InstallException ex)
        {
            Report(ex);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private void Report(InstallException exception)
    {
        AppLog.Warn($"Install operation failed ({exception.Kind}).", exception);
        _notifier.ShowError(UserMessages.For(exception));
    }

    private void SetInstalled(IReadOnlyList<InstalledVideo> videos)
    {
        Installed = videos;
        _byPostId.Clear();
        foreach (var video in videos)
        {
            if (video.Entry?.PostId is { } postId && video.Status != InstalledVideoStatus.Untracked)
            {
                _byPostId[postId] = video;
            }
        }

        InstalledChanged?.Invoke(this, EventArgs.Empty);
    }
}
