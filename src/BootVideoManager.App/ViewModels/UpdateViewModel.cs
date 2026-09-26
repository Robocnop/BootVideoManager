using System.ComponentModel;
using BootVideoManager.App.Localization;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Platform;
using BootVideoManager.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>
/// Update banner and settings section: checks GitHub for a newer release and, for installed copies, downloads the
/// verified installer, starts it and closes the app so it can be replaced (the installer restarts it).
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly UpdateOptions _options;
    private readonly SettingsStore _settings;
    private readonly INotifier _notifier;
    private readonly IPlatformServices _platform;
    private readonly bool _canSelfUpdate;

    public UpdateViewModel(
        UpdateService updates,
        UpdateOptions options,
        SettingsStore settings,
        INotifier notifier,
        IPlatformServices platform,
        bool canSelfUpdate,
        bool managedByStore = false)
    {
        IsManagedByStore = managedByStore;
        _updates = updates;
        _options = options;
        _settings = settings;
        _notifier = notifier;
        _platform = platform;
        _canSelfUpdate = canSelfUpdate;
        CheckOnStartup = settings.Load().CheckForUpdates;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible), nameof(BannerText))]
    public partial AvailableUpdate? Available { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    public partial bool IsDismissed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>Result of the last check, shown in the settings.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial bool CheckOnStartup { get; set; }

    public bool IsBannerVisible => Available is not null && !IsDismissed;

    public bool IsIdle => !IsChecking && !IsDownloading;

    public string CurrentVersionText => _updates.CurrentVersion.ToString(3);

    public string BannerText => Available is { } update
        ? Loc.T(
            $"La version {update.VersionText} de Boot Video Manager est disponible (vous utilisez la {CurrentVersionText}).",
            $"Boot Video Manager {update.VersionText} is available (you are using {CurrentVersionText}).")
        : string.Empty;

    /// <summary>Installed copies update in place; portable ones open the download page.</summary>
    public string InstallButtonText => _canSelfUpdate ? Strings.UpdateInstall : Loc.T("Télécharger", "Download");

    /// <summary>Startup check: silent on failure, and skipped versions are not offered again.</summary>
    /// <summary>Installed as a Flatpak: updates come from its repository, the GitHub check is not used.</summary>
    public bool IsManagedByStore { get; }

    public async Task CheckAtStartupAsync()
    {
        var settings = _settings.Load();
        if (IsManagedByStore || !settings.CheckForUpdates)
        {
            return;
        }

        var update = await CheckAsync(reportErrors: false);
        if (update is not null && update.VersionText == settings.SkippedUpdateVersion)
        {
            AppLog.Info($"Update {update.VersionText} skipped by the user.");
            IsDismissed = true;
        }
    }

    partial void OnCheckOnStartupChanged(bool value) =>
        SaveSettings(s => s with { CheckForUpdates = value });

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        IsDismissed = false;
        await CheckAsync(reportErrors: true);
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } update || IsDownloading)
        {
            return;
        }

        if (!_canSelfUpdate || update.Installer is null)
        {
            await _platform.OpenUriAsync(update.PageUri);
            return;
        }

        IsDownloading = true;
        Progress = 0;
        var progress = new Progress<DownloadProgress>(p => Progress = (p.Fraction ?? 0) * 100);
        try
        {
            var installer = await _updates.DownloadInstallerAsync(update, progress);
            AppLog.Info($"Starting the installer of {update.VersionText}: {installer}");
            AppRuntime.LaunchInstaller(installer);
            _platform.CloseApplication(restart: false); // The installer waits for this copy to close, then restarts it.
        }
        catch (UpdateException ex)
        {
            AppLog.Warn($"Update to {update.VersionText} failed ({ex.Kind}).", ex);
            _notifier.ShowError(UserMessages.For(ex));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Typically the administrator prompt was refused.
            AppLog.Warn("The installer could not be started.", ex);
            _notifier.ShowError(Loc.T(
                "L'installateur de la mise à jour n'a pas pu être lancé.",
                "The update installer could not be started."));
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        if (Available is { } update)
        {
            SaveSettings(s => s with { SkippedUpdateVersion = update.VersionText });
        }

        IsDismissed = true;
    }

    [RelayCommand]
    private void Dismiss() => IsDismissed = true;

    [RelayCommand]
    private Task OpenReleaseNotesAsync() => _platform.OpenUriAsync(Available?.PageUri ?? _options.ReleasesPageUri);

    private async Task<AvailableUpdate?> CheckAsync(bool reportErrors)
    {
        IsChecking = true;
        StatusText = Loc.T("Recherche en cours…", "Checking…");
        try
        {
            var update = await _updates.CheckAsync();
            Available = update;
            StatusText = update is null
                ? Loc.T($"Vous utilisez la dernière version ({CurrentVersionText}).", $"You are using the latest version ({CurrentVersionText}).")
                : Loc.T($"La version {update.VersionText} est disponible.", $"Version {update.VersionText} is available.");
            AppLog.Info(update is null ? "No update available." : $"Update available: {update.VersionText}.");
            return update;
        }
        catch (UpdateException ex)
        {
            AppLog.Warn("Update check failed.", ex);
            StatusText = UserMessages.For(ex);
            if (reportErrors)
            {
                _notifier.ShowError(StatusText);
            }

            return null;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void SaveSettings(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _settings.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save the update settings.", ex);
        }
    }
}
