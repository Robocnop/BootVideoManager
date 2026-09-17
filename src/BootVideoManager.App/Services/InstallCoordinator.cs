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
            _notifier.ShowError("Aucun dossier Steam n'est sélectionné. Choisissez-le dans l'onglet Réglages.");
            return false;
        }

        try
        {
            await _install.InstallAsync(post, Steam.MoviesDirectory, progress, cancellationToken);
            _notifier.ShowInfo($"« {post.Title} » est installée. Sélectionnez-la dans Steam › Paramètres › Personnalisation.");
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
            var outcome = await _install.UninstallAsync(Steam.MoviesDirectory, video, userConfirmed: false);
            if (outcome == UninstallOutcome.RequiresConfirmation)
            {
                var reason = video switch
                {
                    { Status: InstalledVideoStatus.Modified } => $"« {video.DisplayTitle} » a été modifiée depuis son installation.",
                    { IsInSteamCache: true } => $"« {video.FileName} » se trouve dans le cache des vidéos de démarrage de Steam et n'a pas été installée par cette application.\nAstuce : pour la retirer de la lecture aléatoire sans la supprimer, désactivez-la simplement.",
                    _ => $"« {video.FileName} » n'a pas été installée par cette application (ajout manuel ou par un autre outil).",
                };

                if (!await _dialogs.ConfirmAsync("Supprimer ce fichier ?", $"{reason}\nLa supprimer malgré tout ?", "Supprimer", destructive: true))
                {
                    return;
                }

                outcome = await _install.UninstallAsync(Steam.MoviesDirectory, video, userConfirmed: true);
            }

            if (outcome == UninstallOutcome.Deleted)
            {
                _notifier.ShowInfo($"« {video.DisplayTitle} » a été supprimée.");
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
                    ? "Supprimer la vidéo installée par l'application ?"
                    : $"Supprimer les {tracked} vidéos installées par l'application ?";
                if (others > 0)
                {
                    message += others == 1
                        ? "\nL'autre fichier sera traité ensuite, avec une confirmation distincte."
                        : $"\nLes {others} autres fichiers seront traités ensuite, avec une confirmation distincte.";
                }

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
                    others == 1
                        ? "Un fichier du dossier n'a pas été installé par cette application ou a été modifié.\nLe supprimer aussi ?"
                        : $"{others} fichiers du dossier n'ont pas été installés par cette application ou ont été modifiés.\nLes supprimer aussi ?",
                    "Supprimer aussi",
                    destructive: true))
            {
                var result = await _install.UninstallAllAsync(Steam.MoviesDirectory, includeUnconfirmed: true);
                deleted += result.Deleted;
                failed.AddRange(result.Failed);
            }

            var deletedText = deleted > 1 ? $"{deleted} vidéos supprimées." : $"{deleted} vidéo supprimée.";
            if (failed.Count > 0)
            {
                _notifier.ShowError($"{deletedText} Impossible de supprimer : {string.Join(", ", failed)} (Steam est peut-être en train de les lire).");
            }
            else if (deleted > 0)
            {
                _notifier.ShowInfo(deletedText);
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
            _notifier.ShowError("Aucun dossier Steam n'est sélectionné. Choisissez-le dans l'onglet Réglages.");
            return;
        }

        try
        {
            var video = await _install.ImportLocalFileAsync(path, Steam.MoviesDirectory);
            _notifier.ShowInfo($"« {video.DisplayTitle} » a été importée. Sélectionnez-la dans Steam › Paramètres › Personnalisation.");
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
