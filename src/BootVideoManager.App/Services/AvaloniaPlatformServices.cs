using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System.Net;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Sharing;

namespace BootVideoManager.App.Services;

/// <summary><see cref="IPlatformServices"/> implemented with Avalonia's storage provider and launcher.</summary>
public sealed class AvaloniaPlatformServices(TopLevel topLevel) : IPlatformServices
{
    private static FilePickerFileType WebmFiles => new(Loc.T("Vidéo WebM", "WebM video"))
    {
        Patterns = ["*.webm"],
        MimeTypes = ["video/webm"],
    };

    private static FilePickerFileType PackFiles => new(Loc.T("Pack Boot Video Manager", "Boot Video Manager pack"))
    {
        Patterns = ["*" + VideoPack.FileExtension],
        MimeTypes = ["application/json"],
    };

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickWebmFileAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Importer une vidéo .webm", "Import a .webm video"),
            AllowMultiple = false,
            FileTypeFilter = [WebmFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPackFileAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Importer un pack", "Import a pack"),
            AllowMultiple = false,
            FileTypeFilter = [PackFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPackSavePathAsync(string suggestedName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Exporter mes vidéos", "Export my videos"),
            SuggestedFileName = suggestedName,
            DefaultExtension = VideoPack.FileExtension.TrimStart('.'),
            FileTypeChoices = [PackFiles],
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    public async Task OpenUriAsync(Uri uri) => await topLevel.Launcher.LaunchUriAsync(uri);

    public async Task<IReadOnlyList<Cookie>?> SignInWithBrowserAsync(Uri startUri, Func<Uri, bool> isFinished, string dataDirectory)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<Cookie>?>();
        using var dialog = new NativeWebDialog
        {
            Title = Loc.T("Connexion à steamdeckrepo.com", "Sign in to steamdeckrepo.com"),
            CanUserResize = true,
        };

        dialog.EnvironmentRequested += (_, e) =>
        {
            // Private browsing: the Steam session stays in this window only; the app keeps just the site cookies.
            switch (e)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                    Directory.CreateDirectory(dataDirectory);
                    webView2.UserDataFolder = dataDirectory;
                    webView2.IsInPrivateModeEnabled = true;
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.EphemeralDataManager = true;
                    break;
                case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                    apple.NonPersistentDataStore = true;
                    break;
            }
        };

        dialog.NavigationCompleted += async (_, e) =>
        {
            if (completion.Task.IsCompleted || !e.IsSuccess)
            {
                return;
            }

            var current = dialog.Source ?? e.Request;
            if (current is null || !isFinished(current))
            {
                return;
            }

            try
            {
                if (dialog.TryGetCookieManager() is { } manager)
                {
                    completion.TrySetResult(await manager.GetCookiesAsync());
                }
                else if (OperatingSystem.IsLinux() && dialog.TryGetWebViewPlatformHandle() is IGtkWebViewPlatformHandle gtk)
                {
                    // Avalonia's WebKitGTK backend has no cookie manager: read them from WebKit itself.
                    completion.TrySetResult(await WebKitGtkCookies.GetAsync(gtk.WebKitWebView, current));
                }
                else
                {
                    throw new InvalidOperationException("The embedded browser does not expose its cookies.");
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        };

        dialog.Closing += (_, _) => completion.TrySetResult(null);

        dialog.Resize(560, 780);
        dialog.Show(topLevel);
        dialog.Navigate(startUri);
        try
        {
            return await completion.Task;
        }
        finally
        {
            dialog.Close();
        }
    }

    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    public void CloseApplication(bool restart)
    {
        App.RestartRequested = restart;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public bool IsSteamRunning() => SteamProcess.IsRunning();

    public Task<bool> ShutdownSteamAsync(string steamRoot) => SteamProcess.ShutdownAsync(steamRoot);

    public void StartSteam(string steamRoot) => SteamProcess.Start(steamRoot);
}
