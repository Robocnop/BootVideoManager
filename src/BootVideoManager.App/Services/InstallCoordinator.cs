using BootVideoManager.Core.Install;
using BootVideoManager.Core.Models;
using BootVideoManager.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BootVideoManager.App.Services;

/// <summary>
/// Single source of truth for "what is installed in the selected Steam folder", shared by the catalog and the
/// installed-videos tab. Wraps <see cref="InstallService"/> with confirmations and user-facing messages.
/// </summary>
public sealed partial class InstallCoordinator : ObservableObject
{
    private readonly InstallService _install;
    private readonly IDialogService _dialogs;
    private readonly INotifier _notifier;
    private readonly Dictionary<string, InstalledVideo> _byPostId = new(StringComparer.Ordinal);

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

    public bool IsInstalled(string postId) => _byPostId.ContainsKey(postId);

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
            _notifier.ShowError(UserMessages.For(ex));
        }
    }

    /// <returns><c>true</c> when the video ended up installed.</returns>
    public async Task<bool> InstallAsync(Post post, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (Steam is null)
        {
            _notifier.ShowError("Aucun dossier Steam sélectionné. Choisissez-le dans l'onglet Réglages.");
            return false;
        }

        try
        {
            await _install.InstallAsync(post, Steam.MoviesDirectory, progress, cancellationToken);
            _notifier.ShowInfo($"« {post.Title} » est installée. Activez-la dans Steam › Paramètres › Personnalisation.");
            return true;
        }
        catch (InstallException ex)
        {
            _notifier.ShowError(UserMessages.For(ex));
            return false;
        }
        catch (OperationCanceledException)
        {
            _notifier.ShowInfo($"Téléchargement de « {post.Title} » annulé.");
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
        if (Steam is null)
        {
            return;
        }

        try
        {
            var outcome = await _install.UninstallAsync(Steam.MoviesDirectory, video.FileName, userConfirmed: false);
            if (outcome == UninstallOutcome.RequiresConfirmation)
            {
                var reason = video.Status == InstalledVideoStatus.Modified
                    ? $"« {video.DisplayTitle} » a été modifiée depuis son installation."
                    : $"« {video.FileName} » n'a pas été installée par cette application (ajout manuel ou autre outil).";

                if (!await _dialogs.ConfirmAsync("Supprimer ce fichier ?", $"{reason}\nLa supprimer quand même ?", "Supprimer", destructive: true))
                {
                    return;
                }

                outcome = await _install.UninstallAsync(Steam.MoviesDirectory, video.FileName, userConfirmed: true);
            }

            if (outcome == UninstallOutcome.Deleted)
            {
                _notifier.ShowInfo($"« {video.DisplayTitle} » a été retirée.");
            }
        }
        catch (InstallException ex)
        {
            _notifier.ShowError(UserMessages.For(ex));
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

        var tracked = Installed.Count(v => !v.RequiresConfirmationToDelete);
        var others = Installed.Count - tracked;
        var deleted = 0;
        var failed = new List<string>();

        try
        {
            if (tracked > 0)
            {
                var message = others == 0
                    ? $"Retirer les {tracked} vidéo(s) installées par l'application ?"
                    : $"Retirer les {tracked} vidéo(s) installées par l'application ?\nLes {others} autre(s) fichier(s) seront traités ensuite, avec une confirmation séparée.";
                if (!await _dialogs.ConfirmAsync("Tout retirer", message, "Tout retirer", destructive: true))
                {
                    return;
                }

                var result = await _install.UninstallAllAsync(Steam.MoviesDirectory, includeUnconfirmed: false);
                deleted += result.Deleted;
                failed.AddRange(result.Failed);
                others = result.RequiringConfirmation;
            }

            if (others > 0 && await _dialogs.ConfirmAsync(
                    "Fichiers ajoutés hors de l'application",
                    $"{others} fichier(s) du dossier n'ont pas été installés par cette application ou ont été modifiés.\nLes supprimer aussi ?",
                    "Supprimer aussi",
                    destructive: true))
            {
                var result = await _install.UninstallAllAsync(Steam.MoviesDirectory, includeUnconfirmed: true);
                deleted += result.Deleted;
                failed.AddRange(result.Failed);
            }

            if (failed.Count > 0)
            {
                _notifier.ShowError($"{deleted} vidéo(s) retirée(s). Impossible de supprimer : {string.Join(", ", failed)} (Steam les utilise peut-être).");
            }
            else if (deleted > 0)
            {
                _notifier.ShowInfo($"{deleted} vidéo(s) retirée(s).");
            }
        }
        catch (InstallException ex)
        {
            _notifier.ShowError(UserMessages.For(ex));
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
            _notifier.ShowError("Aucun dossier Steam sélectionné. Choisissez-le dans l'onglet Réglages.");
            return;
        }

        try
        {
            var video = await _install.ImportLocalFileAsync(path, Steam.MoviesDirectory);
            _notifier.ShowInfo($"« {video.DisplayTitle} » a été importée. Activez-la dans Steam › Paramètres › Personnalisation.");
        }
        catch (InstallException ex)
        {
            _notifier.ShowError(UserMessages.For(ex));
        }
        finally
        {
            await RefreshAsync();
        }
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
