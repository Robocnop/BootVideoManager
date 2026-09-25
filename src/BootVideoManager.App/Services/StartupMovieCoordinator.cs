using System.IO.Abstractions;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.Services;

/// <summary>
/// Makes sure Steam actually plays the enabled videos: that needs its "Random startup movie" option, which is off
/// on a fresh Steam (it then keeps playing its own video). Offered once per session after a video is added, and
/// available any time from the banner of the Installed tab.
/// </summary>
public sealed partial class StartupMovieCoordinator : ObservableObject
{
    private static readonly IFileSystem Files = new FileSystem();

    private readonly InstallCoordinator _installs;
    private readonly IPlatformServices _platform;
    private readonly IDialogService _dialogs;
    private readonly INotifier _notifier;
    private bool _offered;

    public StartupMovieCoordinator(InstallCoordinator installs, IPlatformServices platform, IDialogService dialogs, INotifier notifier)
    {
        _installs = installs;
        _platform = platform;
        _dialogs = dialogs;
        _notifier = notifier;

        _installs.InstalledChanged += (_, _) => Refresh();
        _installs.VideosAdded += (_, _) => _ = OfferAsync();
    }

    /// <summary>The shuffle is known to be off: Steam plays its own video, not the enabled ones.</summary>
    [ObservableProperty]
    public partial bool IsShuffleOff { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Text appended to "video installed" messages: shuffle on = Steam will pick it; off = nothing, the offer to
    /// turn it on follows; unknown = the manual steps.
    /// </summary>
    public static string InstalledHint(SteamInstallation steam)
    {
        ArgumentNullException.ThrowIfNull(steam);
        return StartupMovieSettings.IsShuffleEnabled(Files, steam.RootPath) switch
        {
            true => " " + Loc.T("Steam la choisira au hasard parmi vos vidéos activées.", "Steam will pick it at random among your enabled videos."),
            false => string.Empty,
            null => " " + Loc.T("Sélectionnez-la dans Steam › Paramètres › Personnalisation.", "Select it in Steam › Settings › Customization."),
        };
    }

    public void Refresh() =>
        IsShuffleOff = _installs.Steam is { } steam && StartupMovieSettings.IsShuffleEnabled(Files, steam.RootPath) == false;

    /// <summary>After a video was added: offers to turn the shuffle on, once per session.</summary>
    public async Task OfferAsync()
    {
        Refresh();
        if (!IsShuffleOff || _offered || IsBusy)
        {
            return;
        }

        _offered = true;
        await EnableAsync();
    }

    private bool CanEnable() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private async Task EnableAsync()
    {
        if (_installs.Steam is not { } steam)
        {
            return;
        }

        IsBusy = true;
        var closedSteam = false;
        try
        {
            var running = _platform.IsSteamRunning();
            var message = Loc.T(
                "Pour l'instant, Steam lit sa propre vidéo : l'option « Vidéo de démarrage aléatoire » est désactivée dans ses paramètres.\nBoot Video Manager peut l'activer pour vous : à chaque lancement, Steam choisira une de vos vidéos activées. Une copie de sa configuration est gardée.",
                "For now, Steam plays its own video: the “Random startup movie” option is off in its settings.\nBoot Video Manager can turn it on for you: at each start, Steam will pick one of your enabled videos. A copy of its configuration is kept.");
            if (running)
            {
                message += "\n\n" + Loc.T(
                    "Steam est ouvert : il va être fermé puis relancé. Quittez vos jeux avant de continuer.",
                    "Steam is open: it will be closed and started again. Quit your games before continuing.");
            }

            if (!await _dialogs.ConfirmAsync(
                    Loc.T("Faire jouer vos vidéos par Steam ?", "Let Steam play your videos?"),
                    message,
                    running ? Loc.T("Activer et redémarrer Steam", "Turn on and restart Steam") : Loc.T("Activer", "Turn on"),
                    destructive: false))
            {
                return;
            }

            if (running)
            {
                if (!await _platform.ShutdownSteamAsync(steam.RootPath))
                {
                    _notifier.ShowError(Loc.T(
                        "Steam ne s'est pas fermé. Quittez-le (clic droit sur son icône › Quitter), puis cliquez sur « Activer » dans l'onglet Installées.",
                        "Steam did not close. Quit it (right-click its icon › Exit), then click “Turn on” in the Installed tab."));
                    return;
                }

                closedSteam = true;
            }

            await Task.Run(() => StartupMovieSettings.EnableShuffle(Files, steam.RootPath));
            AppLog.Info("Turned on Steam's random startup movie (previous config.vdf kept as config.vdf.bvm-backup).");
            _notifier.ShowInfo(Loc.T(
                "C'est fait : à chaque lancement, Steam choisira une de vos vidéos activées (sous Windows, au démarrage du mode Big Picture).",
                "Done: at each start, Steam will pick one of your enabled videos (on Windows, when Big Picture mode starts)."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("Could not turn on Steam's random startup movie.", ex);
            _notifier.ShowError(Loc.T(
                "Impossible de modifier la configuration de Steam. Activez « Vidéo de démarrage aléatoire » vous-même dans Steam › Paramètres › Personnalisation.",
                "Could not change Steam's configuration. Turn on “Random startup movie” yourself in Steam › Settings › Customization."));
        }
        finally
        {
            if (closedSteam)
            {
                _platform.StartSteam(steam.RootPath);
            }

            IsBusy = false;
            Refresh();
        }
    }
}
