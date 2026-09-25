using System.IO.Abstractions;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Steam;

namespace BootVideoManager.App.Tests;

public sealed class StartupMovieCoordinatorTests : IDisposable
{
    private const string ShuffleOff =
        "\"InstallConfigStore\"\n{\n\t\"Customization\"\n\t{\n\t\t\"StartupMovie\"\n\t\t{\n\t\t\t\"MovieID\"\t\t\"0\"\n\t\t\t\"LocalPath\"\t\t\"\"\n\t\t\t\"Shuffle\"\t\t\"0\"\n\t\t}\n\t}\n}\n";

    private readonly TestServices _test = new();
    private readonly FakePlatform _platform = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeNotifier _notifier = new();
    private readonly string _steamRoot = Path.Combine(Path.GetTempPath(), "bvm-steam-" + Guid.NewGuid().ToString("N"));
    private readonly InstallCoordinator _installs;
    private readonly StartupMovieCoordinator _startupMovie;

    public StartupMovieCoordinatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_steamRoot, "config"));
        File.WriteAllText(ConfigPath, ShuffleOff);
        _installs = new InstallCoordinator(_test.Services.Install, _dialogs, _notifier);
        _startupMovie = new StartupMovieCoordinator(_installs, _platform, _dialogs, _notifier);
    }

    private string ConfigPath => Path.Combine(_steamRoot, "config", "config.vdf");

    public void Dispose()
    {
        _installs.Dispose();
        _test.Dispose();
        Directory.Delete(_steamRoot, recursive: true);
    }

    private async Task SelectSteamAsync()
    {
        _installs.Steam = new SteamInstallation(_steamRoot, SteamInstallKind.Manual, Path.Combine(_steamRoot, "config", "uioverrides", "movies"));
        await _installs.RefreshAsync();
    }

    [Fact]
    public async Task ShuffleOff_ShowsTheBanner()
    {
        Assert.False(_startupMovie.IsShuffleOff);

        await SelectSteamAsync();

        Assert.True(_startupMovie.IsShuffleOff);
    }

    [Fact]
    public async Task Enable_WhileSteamRuns_ClosesSteam_WritesTheConfig_AndStartsSteamAgain()
    {
        await SelectSteamAsync();
        _platform.SteamRunning = true;
        _dialogs.Answer = true;

        await _startupMovie.EnableCommand.ExecuteAsync(null);

        Assert.Contains("Steam", _dialogs.LastConfirmText, StringComparison.Ordinal);
        Assert.True(StartupMovieSettings.IsShuffleEnabled(new FileSystem(), _steamRoot));
        Assert.Equal(ShuffleOff, File.ReadAllText(ConfigPath + StartupMovieSettings.BackupSuffix));
        Assert.Equal(1, _platform.SteamStarts);
        Assert.False(_startupMovie.IsShuffleOff);
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Enable_Declined_ChangesNothing()
    {
        await SelectSteamAsync();
        _platform.SteamRunning = true;
        _dialogs.Answer = false;

        await _startupMovie.EnableCommand.ExecuteAsync(null);

        Assert.Equal(ShuffleOff, File.ReadAllText(ConfigPath));
        Assert.True(_platform.SteamRunning);
        Assert.Equal(0, _platform.SteamStarts);
        Assert.True(_startupMovie.IsShuffleOff);
    }

    [Fact]
    public async Task Offer_IsMadeOncePerSession()
    {
        await SelectSteamAsync();
        _dialogs.Answer = false;

        await _startupMovie.OfferAsync();
        await _startupMovie.OfferAsync();

        Assert.Equal(1, _dialogs.Count);
    }

    [Fact]
    public async Task UnreadableConfig_IsLeftAlone()
    {
        File.WriteAllText(ConfigPath, "\"InstallConfigStore\"\n{\n");
        await SelectSteamAsync();

        await _startupMovie.OfferAsync();

        Assert.False(_startupMovie.IsShuffleOff);
        Assert.Equal(0, _dialogs.Count);
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Answer { get; set; }

        public int Count { get; private set; }

        public string LastConfirmText { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive)
        {
            Count++;
            LastConfirmText = confirmText;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<string> Errors { get; } = [];

        public void ShowInfo(string message)
        {
        }

        public void ShowError(string message) => Errors.Add(message);
    }
}
