using System.Net;

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

    /// <summary>
    /// Opens a browser window on <paramref name="startUri"/> so the user signs in on the real pages (Steam's
    /// included); the app never sees the password. Once a page accepted by <paramref name="isFinished"/> has loaded,
    /// the window closes and its cookies are returned.
    /// </summary>
    /// <param name="dataDirectory">Browser profile folder (private browsing: nothing of the Steam session is kept).</param>
    /// <returns>The cookies, or <c>null</c> if the user closed the window first.</returns>
    /// <exception cref="InvalidOperationException">No embedded browser is available on this system.</exception>
    Task<IReadOnlyList<Cookie>?> SignInWithBrowserAsync(Uri startUri, Func<Uri, bool> isFinished, string dataDirectory);

    /// <summary>Opens a folder in the file manager, creating it first if needed.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>Closes the application, optionally starting it again (language change).</summary>
    void CloseApplication(bool restart);

    /// <summary>Whether the Steam client is running.</summary>
    bool IsSteamRunning();

    /// <summary>Asks Steam to quit and waits for it.</summary>
    /// <returns><c>false</c> when Steam is still running (timeout, or not supported on this system).</returns>
    Task<bool> ShutdownSteamAsync(string steamRoot);

    /// <summary>Starts Steam again (best effort).</summary>
    void StartSteam(string steamRoot);
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
