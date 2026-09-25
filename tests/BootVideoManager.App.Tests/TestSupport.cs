using Avalonia;
using Avalonia.Headless;
using BootVideoManager.App;
using BootVideoManager.App.Services;
using BootVideoManager.App.Tests;
using BootVideoManager.Core.Platform;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace BootVideoManager.App.Tests;

/// <summary>Runs the real application (styles, resources) on Avalonia's headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>Records what the view models ask of the operating system.</summary>
internal sealed class FakePlatform : IPlatformServices
{
    public List<Uri> OpenedUris { get; } = [];

    public List<string> OpenedFolders { get; } = [];

    public bool? ClosedWithRestart { get; private set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickWebmFileAsync() => Task.FromResult<string?>(null);

    /// <summary>File returned by the next "import a pack" picker.</summary>
    public string? PackToImport { get; set; }

    public Task<string?> PickPackFileAsync() => Task.FromResult(PackToImport);

    public Task<string?> PickPackSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);

    public Task OpenUriAsync(Uri uri)
    {
        OpenedUris.Add(uri);
        return Task.CompletedTask;
    }

    public Task OpenFolderAsync(string path)
    {
        OpenedFolders.Add(path);
        return Task.CompletedTask;
    }

    public void CloseApplication(bool restart) => ClosedWithRestart = restart;
}

/// <summary>Real services whose settings, caches and logs live in a temporary folder deleted afterwards.</summary>
internal sealed class TestServices : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bvm-tests-" + Guid.NewGuid().ToString("N"));

    public TestServices() => Services = AppServices.Create(AppPaths.Under(_root));

    public AppServices Services { get; }

    public void Dispose()
    {
        Services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }
}
