using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
}
