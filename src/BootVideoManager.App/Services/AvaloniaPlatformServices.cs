using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace BootVideoManager.App.Services;

/// <summary><see cref="IPlatformServices"/> implemented with Avalonia's storage provider and launcher.</summary>
public sealed class AvaloniaPlatformServices(TopLevel topLevel) : IPlatformServices
{
    private static readonly FilePickerFileType WebmFiles = new("Vidéo WebM")
    {
        Patterns = ["*.webm"],
        MimeTypes = ["video/webm"],
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
            Title = "Importer une vidéo .webm",
            AllowMultiple = false,
            FileTypeFilter = [WebmFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task OpenUriAsync(Uri uri) => await topLevel.Launcher.LaunchUriAsync(uri);

    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
