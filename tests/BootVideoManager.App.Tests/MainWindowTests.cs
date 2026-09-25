using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BootVideoManager.App.Services;
using BootVideoManager.App.ViewModels;
using BootVideoManager.App.Views;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Updates;

namespace BootVideoManager.App.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly FakePlatform _platform = new();

    public void Dispose() => _test.Dispose();

    private (MainWindow Window, MainWindowViewModel ViewModel) Show()
    {
        var viewModel = new MainWindowViewModel(_test.Services, _platform, canSelfUpdate: true);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void ConfirmDialog_IsShown_AndItsConfirmButtonAnswersYes()
    {
        var (window, viewModel) = Show();

        var answer = viewModel.ConfirmAsync("Title", "Message", "Do it", destructive: true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Grid>(window, "DialogOverlay").IsVisible);
        var confirm = Find<Button>(window, "DialogConfirmButton");
        Assert.Equal("Do it", confirm.Content);
        Assert.Contains("danger", confirm.Classes);

        confirm.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(answer.IsCompletedSuccessfully && answer.Result);
        Assert.Null(viewModel.Dialog);
        Assert.False(Find<Grid>(window, "DialogOverlay").IsVisible);
    }

    [AvaloniaFact]
    public void Escape_CancelsTheDialog()
    {
        var (window, viewModel) = Show();
        var answer = viewModel.ConfirmAsync("Title", "Message", "OK", destructive: false);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.False(answer.Result);
    }

    [AvaloniaFact]
    public void ErrorNotification_StaysUntilDismissed()
    {
        var (window, viewModel) = Show();

        viewModel.ShowError("Something failed");
        Dispatcher.UIThread.RunJobs();

        var banner = Find<Border>(window, "NotificationBanner");
        Assert.True(banner.IsVisible);
        Assert.Contains("error", banner.Classes);

        viewModel.Notification!.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible);
    }

    [AvaloniaFact]
    public void UpdateBanner_ShowsTheNewVersion_AndSkipRemembersIt()
    {
        var (_, viewModel) = Show();
        var updates = viewModel.Updates;

        updates.Available = new AvailableUpdate(new Version(9, 1, 0), "v9.1.0", new Uri("https://example.test/v9.1.0"), "Notes", null, null);

        Assert.True(updates.IsBannerVisible);
        Assert.Contains("9.1.0", updates.BannerText, StringComparison.Ordinal);

        updates.SkipCommand.Execute(null);

        Assert.False(updates.IsBannerVisible);
        Assert.Equal("9.1.0", _test.Services.Settings.Load().SkippedUpdateVersion);
    }

    [AvaloniaFact]
    public async Task UpdateWithoutInstaller_OpensTheReleasePage()
    {
        var (_, viewModel) = Show();
        var page = new Uri("https://example.test/v9.1.0");
        viewModel.Updates.Available = new AvailableUpdate(new Version(9, 1, 0), "v9.1.0", page, "Notes", null, null);

        await viewModel.Updates.InstallCommand.ExecuteAsync(null);

        Assert.Equal([page], _platform.OpenedUris);
        Assert.Null(_platform.ClosedWithRestart);
    }

    [AvaloniaFact]
    public void CheckForUpdatesSetting_IsSaved()
    {
        var (_, viewModel) = Show();

        viewModel.Updates.CheckOnStartup = false;

        Assert.False(_test.Services.Settings.Load().CheckForUpdates);
    }

    [AvaloniaFact]
    public void LanguageChange_IsSaved_AndOffersARestart()
    {
        var (_, viewModel) = Show();
        var settings = viewModel.Settings;
        var other = settings.LanguageOptions.First(o => o.Value is not null && o.Value != Loc.Current);

        settings.SelectedLanguage = other;

        Assert.Equal(other.Value, _test.Services.Settings.Load().Language);
        Assert.True(settings.IsRestartRequired);

        settings.RestartCommand.Execute(null);
        Assert.True(_platform.ClosedWithRestart);
    }

    [AvaloniaFact]
    public void CatalogFilters_AreRestoredOnTheNextStart()
    {
        var (_, first) = Show();
        first.Catalog.SelectedSort = first.Catalog.SortOptions.Single(o => o.Value == CatalogSort.MostLiked);
        first.Catalog.SelectedDuration = first.Catalog.DurationOptions[3];

        var second = new MainWindowViewModel(_test.Services, _platform, canSelfUpdate: false);

        Assert.Equal(CatalogSort.MostLiked, second.Catalog.SelectedSort.Value);
        Assert.Same(second.Catalog.DurationOptions[3], second.Catalog.SelectedDuration);
    }

    [AvaloniaFact]
    public void PreviewSound_IsRemembered()
    {
        var (_, first) = Show();
        first.Catalog.PreviewSound.Volume = 35;
        first.Catalog.PreviewSound.IsMuted = true;

        var second = new MainWindowViewModel(_test.Services, _platform, canSelfUpdate: false);

        Assert.Equal(35, second.Catalog.PreviewSound.Volume);
        Assert.True(second.Catalog.PreviewSound.IsMuted);
    }

    [AvaloniaFact]
    public async Task OpenLogs_OpensTheLogFolder()
    {
        AppLog.Initialize(_test.Services.Log);
        var (_, viewModel) = Show();

        await viewModel.Settings.OpenLogsCommand.ExecuteAsync(null);

        Assert.Equal([_test.Services.Log.Directory], _platform.OpenedFolders);
    }

    [AvaloniaFact]
    public async Task ImportPack_WithAFileThatIsNotAPack_ShowsAnError()
    {
        var (_, viewModel) = Show();
        var path = Path.Combine(Path.GetTempPath(), $"bvm-test-{Guid.NewGuid():N}.bvmpack");
        await File.WriteAllTextAsync(path, "not a pack", TestContext.Current.CancellationToken);
        try
        {
            _platform.PackToImport = path;

            await viewModel.Installed.ImportPackCommand.ExecuteAsync(null);

            Assert.True(viewModel.Notification?.IsError);
            Assert.False(viewModel.Installed.IsBusy);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
