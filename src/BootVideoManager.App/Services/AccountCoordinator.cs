using BootVideoManager.Core.Account;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BootVideoManager.App.Services;

/// <summary>
/// Shared steamdeckrepo.com account state: who is signed in and which posts they liked. Signing in happens in an
/// embedded browser on the real Steam pages; only the site's session cookies are kept (encrypted on Windows).
/// </summary>
public sealed partial class AccountCoordinator : ObservableObject
{
    private readonly SiteAccountClient _client;
    private readonly SiteSessionStore _store;
    private readonly IPlatformServices _platform;
    private readonly INotifier _notifier;
    private readonly string _webViewDataDirectory;
    private readonly HashSet<string> _liked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingLikes = new(StringComparer.Ordinal);

    public AccountCoordinator(SiteAccountClient client, SiteSessionStore store, IPlatformServices platform, INotifier notifier, string webViewDataDirectory)
    {
        _client = client;
        _store = store;
        _platform = platform;
        _notifier = notifier;
        _webViewDataDirectory = webViewDataDirectory;
        _client.CookiesChanged += (_, _) => SaveCookies();
    }

    /// <summary>The liked set changed (sign-in, sign-out, refresh or a like toggled).</summary>
    public event EventHandler? LikesChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    public partial SiteUser? User { get; private set; }

    public bool IsSignedIn => User is not null;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>The list of likes is known (it may still be empty).</summary>
    [ObservableProperty]
    public partial bool LikesLoaded { get; private set; }

    public int LikedCount => _liked.Count;

    public bool IsLiked(string postId) => _liked.Contains(postId);

    /// <summary>Snapshot of the liked post ids.</summary>
    public IReadOnlySet<string> LikedIds => _liked.ToHashSet(StringComparer.Ordinal);

    /// <summary>Restores the saved session (if any) and loads the likes in the background.</summary>
    public async Task InitializeAsync()
    {
        if (_store.Load() is not { } session)
        {
            return;
        }

        _client.UseCookies(session.Cookies);
        User = session.User;
        AppLog.Info($"Restored the steamdeckrepo.com session of user {session.UserId}.");
        await RefreshLikesAsync(quiet: true);
    }

    /// <summary>Opens the Steam sign-in window, then checks the site really opened a session.</summary>
    public async Task SignInAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var cookies = await _platform.SignInWithBrowserAsync(_client.LoginUri, _client.IsBackOnSite, _webViewDataDirectory);
            if (cookies is null)
            {
                return; // Window closed by the user.
            }

            _client.UseCookies(cookies
                .Where(c => _client.IsSiteCookieDomain(c.Domain))
                .Select(c => new SiteCookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires.ToUniversalTime(), TimeSpan.Zero),
                }));

            var user = await _client.GetCurrentUserAsync();
            if (user is null)
            {
                _client.ClearCookies();
                AppLog.Warn($"Sign-in window closed on the site without a session ({cookies.Count} cookies captured).");
                _notifier.ShowError(Loc.T(
                    "La connexion n'a pas abouti. Réessayez, et validez bien la connexion sur la page Steam.",
                    "Signing in did not complete. Try again and confirm the sign-in on the Steam page."));
                return;
            }

            User = user;
            SaveCookies();
            AppLog.Info($"Signed in to steamdeckrepo.com as user {user.Id}.");
            _notifier.ShowInfo(Loc.T($"Connecté en tant que {user.Name}.", $"Signed in as {user.Name}."));
            await RefreshLikesAsync(quiet: false);
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("The sign-in browser could not be used.", ex);
            _notifier.ShowError(OperatingSystem.IsWindows()
                ? Loc.T(
                    "Impossible d'ouvrir la fenêtre de connexion : le composant Microsoft Edge WebView2 est nécessaire.",
                    "Could not open the sign-in window: the Microsoft Edge WebView2 runtime is required.")
                : Loc.T(
                    "Impossible d'ouvrir la fenêtre de connexion : WebKitGTK (webkit2gtk 4.1) est nécessaire. La version Flatpak l'inclut.",
                    "Could not open the sign-in window: WebKitGTK (webkit2gtk 4.1) is required. The Flatpak version includes it."));
        }
        catch (RepoApiException ex)
        {
            AppLog.Warn("Sign-in could not be verified.", ex);
            _client.ClearCookies();
            _notifier.ShowError(UserMessages.For(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SignOutAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.SignOutAsync();
        }
        finally
        {
            Forget();
            IsBusy = false;
        }

        AppLog.Info("Signed out of steamdeckrepo.com.");
    }

    /// <param name="quiet">Only log failures (startup), except an expired session which the user must know about.</param>
    public async Task RefreshLikesAsync(bool quiet)
    {
        if (User is not { } user)
        {
            return;
        }

        try
        {
            var ids = await _client.GetLikedPostIdsAsync(user.Id);
            _liked.Clear();
            _liked.UnionWith(ids);
            LikesLoaded = true;
            AppLog.Info($"Loaded {ids.Count} liked posts.");
            OnLikesChanged();
        }
        catch (RepoApiException ex) when (ex.Kind == RepoApiErrorKind.SignedOut)
        {
            AppLog.Warn("The steamdeckrepo.com session expired.", ex);
            Forget();
            _notifier.ShowError(UserMessages.For(ex));
        }
        catch (RepoApiException ex)
        {
            AppLog.Warn("Liked posts could not be loaded.", ex);
            if (!quiet)
            {
                _notifier.ShowError(UserMessages.For(ex));
            }
        }
    }

    /// <summary>Likes or unlikes a post on the site.</summary>
    /// <returns>The new state, or <c>null</c> if it failed (the user was told why).</returns>
    public async Task<LikeState?> ToggleLikeAsync(string postId)
    {
        if (!IsSignedIn || !_pendingLikes.Add(postId))
        {
            return null;
        }

        try
        {
            var state = await _client.ToggleLikeAsync(postId);
            if (state.Liked)
            {
                _liked.Add(postId);
            }
            else
            {
                _liked.Remove(postId);
            }

            AppLog.Info($"{(state.Liked ? "Liked" : "Unliked")} post {postId}.");
            OnLikesChanged();
            return state;
        }
        catch (RepoApiException ex)
        {
            AppLog.Warn($"Could not toggle the like of post {postId}.", ex);
            if (ex.Kind == RepoApiErrorKind.SignedOut)
            {
                Forget();
            }

            _notifier.ShowError(UserMessages.For(ex));
            return null;
        }
        finally
        {
            _pendingLikes.Remove(postId);
        }
    }

    private void Forget()
    {
        _client.ClearCookies();
        _store.Delete();
        User = null;
        _liked.Clear();
        LikesLoaded = false;
        OnLikesChanged();
    }

    private void SaveCookies()
    {
        if (User is not { } user)
        {
            return;
        }

        try
        {
            _store.Save(new SiteSession
            {
                Cookies = [.. _client.ExportCookies()],
                UserId = user.Id,
                UserName = user.Name,
                AvatarUrl = user.AvatarUri?.AbsoluteUri,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save the steamdeckrepo.com session.", ex);
        }
    }

    private void OnLikesChanged()
    {
        OnPropertyChanged(nameof(LikedCount));
        LikesChanged?.Invoke(this, EventArgs.Empty);
    }
}
