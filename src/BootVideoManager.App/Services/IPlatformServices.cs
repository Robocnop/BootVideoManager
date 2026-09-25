namespace BootVideoManager.App.Services;

/// <summary>OS integrations the view models need, kept behind an interface so they stay UI-toolkit agnostic.</summary>
public interface IPlatformServices
{
    /// <returns>The chosen local folder, or <c>null</c> if cancelled.</returns>
    Task<string?> PickFolderAsync(string title);

    /// <returns>The chosen local <c>.webm</c> file, or <c>null</c> if cancelled.</returns>
    Task<string?> PickWebmFileAsync();

    /// <returns>The chosen <c>.bvmpack</c> file to import, or <c>null</c> if cancelled.</returns>
    Task<string?> PickPackFileAsync();

    /// <returns>Where to save a new <c>.bvmpack</c> file, or <c>null</c> if cancelled.</returns>
    Task<string?> PickPackSavePathAsync(string suggestedName);

    Task OpenUriAsync(Uri uri);

    /// <summary>Opens a folder in the file manager, creating it first if needed.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>Closes the application, optionally starting it again (language change).</summary>
    void CloseApplication(bool restart);
}

/// <summary>Modal confirmation shown inside the main window (works with mouse, keyboard and controller).</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive);
}

/// <summary>Transient messages shown to the user.</summary>
public interface INotifier
{
    void ShowInfo(string message);

    void ShowError(string message);
}
