using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BootVideoManager.App.Localization;
using BootVideoManager.App.ViewModels;
using BootVideoManager.App.Views;

namespace BootVideoManager.App.Tests;

public sealed class AccountTests : IDisposable
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

    [AvaloniaFact]
    public async Task SignedOut_TheTopBarOffersToSignIn()
    {
        var (window, viewModel) = Show();
        await viewModel.Account.InitializeAsync(); // No saved session: stays signed out, no request sent.
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.Account.IsSignedIn);
        var signIn = window.GetVisualDescendants().OfType<Button>()
            .Where(b => Equals(b.Content, Strings.SignInWithSteam))
            .ToList();
        Assert.Contains(signIn, b => b.IsEffectivelyVisible && b.Command == viewModel.SignInCommand);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("account-chip") && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ClosingTheSignInWindow_LeavesTheUserSignedOut()
    {
        var (_, viewModel) = Show();
        _platform.SignInCookies = null; // The user closes the Steam window.

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal(new Uri("https://steamdeckrepo.com/login"), Assert.Single(_platform.SignInStarts));
        Assert.False(viewModel.Account.IsSignedIn);
        Assert.False(viewModel.Account.IsBusy);
        Assert.Null(viewModel.Notification);
        Assert.False(File.Exists(_test.Services.Paths.AccountPath));
    }

    [AvaloniaFact]
    public void AccountChip_OpensTheSettingsTab()
    {
        var (_, viewModel) = Show();

        viewModel.OpenAccountSettingsCommand.Execute(null);

        Assert.True(viewModel.IsSettingsTab);
    }

    [AvaloniaFact]
    public async Task InstalledFromFlathub_TheAppDoesNotLookForGitHubUpdates()
    {
        var updates = new UpdateViewModel(
            _test.Services.Updates, _test.Services.UpdateOptions, _test.Services.Settings, new FakeNotifier(), _platform,
            canSelfUpdate: false, managedByStore: true);

        await updates.CheckAtStartupAsync();

        Assert.True(updates.IsManagedByStore);
        Assert.Null(updates.Available);
        Assert.False(updates.IsChecking);
    }

    private sealed class FakeNotifier : Services.INotifier
    {
        public void ShowInfo(string message)
        {
        }

        public void ShowError(string message)
        {
        }
    }
}
