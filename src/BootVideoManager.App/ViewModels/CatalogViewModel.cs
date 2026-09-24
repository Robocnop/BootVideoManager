using System.Collections.ObjectModel;
using System.Globalization;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Caching;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Models;
using BootVideoManager.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>Catalog tab: search, filters, sort (remembered between sessions), paged grid and detail panel.</summary>
public sealed partial class CatalogViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 48;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Settings keys of <see cref="DurationOptions"/>, in the same order.</summary>
    private static readonly string[] DurationKeys = [CatalogPreferences.AnyDuration, "short", "medium", "long"];

    private readonly CatalogService _catalog;
    private readonly ThumbnailCache _thumbnails;
    private readonly InstallCoordinator _installs;
    private readonly IPlatformServices _platform;
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, PostCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly bool _restored;
    private IReadOnlyList<Post> _results = [];
    private CatalogSnapshot? _snapshot;
    private CancellationTokenSource? _searchDebounce;

    public CatalogViewModel(
        CatalogService catalog,
        ThumbnailCache thumbnails,
        InstallCoordinator installs,
        IPlatformServices platform,
        SettingsStore settings,
        PreviewSoundViewModel previewSound)
    {
        _catalog = catalog;
        _thumbnails = thumbnails;
        _installs = installs;
        _platform = platform;
        _settings = settings;
        PreviewSound = previewSound;

        SortOptions =
        [
            new(Loc.T("Tendances", "Trending"), CatalogSort.Trending),
            new(Loc.T("Les plus téléchargées", "Most downloaded"), CatalogSort.MostDownloaded),
            new(Loc.T("Les plus aimées", "Most liked"), CatalogSort.MostLiked),
            new(Loc.T("Les plus récentes", "Newest"), CatalogSort.Newest),
            new(Loc.T("Les plus anciennes", "Oldest"), CatalogSort.Oldest),
        ];
        TypeOptions =
        [
            new(Loc.T("Tous les types", "All types"), null),
            new(Loc.T("Vidéos de démarrage", "Startup videos"), VideoType.BootVideo),
            new(Loc.T("Animations de veille", "Suspend animations"), VideoType.SuspendVideo),
        ];
        DeviceOptions =
        [
            new(Loc.T("Tous les appareils", "All devices"), null),
            new("Steam Deck", DeviceTag.SteamDeck),
            new("Steam Machine", DeviceTag.SteamMachine),
        ];
        DurationOptions =
        [
            new(Loc.T("Toutes les durées", "Any duration"), new DurationRange(null, null)),
            new(Loc.T("Courtes (10 s ou moins)", "Short (10 s or less)"), new DurationRange(null, 10)),
            new(Loc.T("Moyennes (11 à 30 s)", "Medium (11 to 30 s)"), new DurationRange(11, 30)),
            new(Loc.T("Longues (plus de 30 s)", "Long (over 30 s)"), new DurationRange(31, null)),
        ];

        var saved = settings.Load().Catalog;
        SelectedSort = SortOptions.FirstOrDefault(o => o.Value == saved.Sort) ?? SortOptions[0];
        SelectedType = TypeOptions.FirstOrDefault(o => o.Value == saved.Type) ?? TypeOptions[0];
        SelectedDevice = DeviceOptions.FirstOrDefault(o => o.Value == saved.Device) ?? DeviceOptions[0];
        var durationIndex = Array.IndexOf(DurationKeys, saved.Duration);
        SelectedDuration = DurationOptions[durationIndex >= 0 ? durationIndex : 0];
        _restored = true;

        _installs.InstalledChanged += (_, _) =>
        {
            foreach (var card in Items)
            {
                card.SyncInstalledState();
            }

            SelectedPost?.Card.SyncInstalledState();
        };
    }

    public ObservableCollection<PostCardViewModel> Items { get; } = [];

    /// <summary>Volume and mute of the preview, shared by every detail panel.</summary>
    public PreviewSoundViewModel PreviewSound { get; }

    public IReadOnlyList<Choice<CatalogSort>> SortOptions { get; }

    public IReadOnlyList<Choice<VideoType?>> TypeOptions { get; }

    public IReadOnlyList<Choice<DeviceTag?>> DeviceOptions { get; }

    public IReadOnlyList<Choice<DurationRange>> DurationOptions { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<CatalogSort> SelectedSort { get; set; }

    [ObservableProperty]
    public partial Choice<VideoType?> SelectedType { get; set; }

    [ObservableProperty]
    public partial Choice<DeviceTag?> SelectedDevice { get; set; }

    [ObservableProperty]
    public partial Choice<DurationRange> SelectedDuration { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? WarningMessage { get; set; }

    [ObservableProperty]
    public partial string ResultText { get; set; } = Loc.T("Chargement du catalogue…", "Loading the catalog…");

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial bool CanLoadMore { get; set; }

    [ObservableProperty]
    public partial PostDetailViewModel? SelectedPost { get; set; }

    public async Task LoadAsync(bool forceRefresh)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _snapshot = await _catalog.LoadAsync(forceRefresh);
            var fetchedAt = _snapshot.FetchedAt.LocalDateTime;
            if (_snapshot.RefreshError is { } refreshError)
            {
                AppLog.Warn("Catalog refresh failed; showing the cached copy.", refreshError);
                WarningMessage = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{Loc.T($"Catalogue hors ligne (dernière mise à jour le {fetchedAt:g}).", $"Offline catalog (last updated {fetchedAt:g}).")} {UserMessages.For(refreshError)}");
            }
            else
            {
                WarningMessage = null;
            }

            StatusText = string.Create(CultureInfo.CurrentCulture, $"{Loc.T("Catalogue mis à jour le", "Catalog updated")} {fetchedAt:g}");
            ApplyQuery();
        }
        catch (RepoApiException ex)
        {
            AppLog.Warn("Catalog could not be loaded.", ex);
            ErrorMessage = UserMessages.For(ex);
            ResultText = Loc.T("Catalogue indisponible", "Catalog unavailable");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(forceRefresh: true);

    [RelayCommand]
    private void LoadMore() => AppendPage();

    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = string.Empty;
        SelectedType = TypeOptions[0];
        SelectedDevice = DeviceOptions[0];
        SelectedDuration = DurationOptions[0];
    }

    [RelayCommand]
    private void CloseDetail() => SelectedPost = null;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();
        _ = ApplyQueryAfterDelayAsync(_searchDebounce.Token);
    }

    partial void OnSelectedSortChanged(Choice<CatalogSort> value) => OnFiltersChanged();

    partial void OnSelectedTypeChanged(Choice<VideoType?> value) => OnFiltersChanged();

    partial void OnSelectedDeviceChanged(Choice<DeviceTag?> value) => OnFiltersChanged();

    partial void OnSelectedDurationChanged(Choice<DurationRange> value) => OnFiltersChanged();

    private void OnFiltersChanged()
    {
        ApplyQuery();
        SavePreferences();
    }

    private void SavePreferences()
    {
        if (!_restored || SelectedSort is null || SelectedType is null || SelectedDevice is null || SelectedDuration is null)
        {
            return;
        }

        var durationIndex = DurationOptions.ToList().IndexOf(SelectedDuration);
        var preferences = new CatalogPreferences
        {
            Sort = SelectedSort.Value,
            Type = SelectedType.Value,
            Device = SelectedDevice.Value,
            Duration = DurationKeys[Math.Max(0, durationIndex)],
        };

        try
        {
            _settings.Update(s => s with { Catalog = preferences });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save the catalog filters.", ex);
        }
    }

    private async Task ApplyQueryAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDebounce, cancellationToken);
            ApplyQuery();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this search.
        }
    }

    private void ApplyQuery()
    {
        if (_snapshot is null || SelectedSort is null || SelectedType is null || SelectedDevice is null || SelectedDuration is null)
        {
            return;
        }

        var duration = SelectedDuration.Value;
        _results = CatalogQueryEngine.Apply(_snapshot, new CatalogQuery
        {
            SearchText = SearchText,
            Sort = SelectedSort.Value,
            Type = SelectedType.Value,
            Device = SelectedDevice.Value,
            MinDuration = duration.MinSeconds is { } min ? TimeSpan.FromSeconds(min) : null,
            MaxDuration = duration.MaxSeconds is { } max ? TimeSpan.FromSeconds(max) : null,
        });

        var previous = Items.ToList();
        Items.Clear();
        foreach (var card in previous)
        {
            card.ReleaseThumbnail();
        }

        ResultText = _results.Count switch
        {
            0 => Loc.T("Aucune vidéo ne correspond à ces critères.", "No video matches these criteria."),
            1 => Loc.T("1 vidéo", "1 video"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{_results.Count:N0} {Loc.T("vidéos", "videos")}"),
        };

        AppendPage();
    }

    private void AppendPage()
    {
        foreach (var post in _results.Skip(Items.Count).Take(PageSize))
        {
            var card = GetOrCreateCard(post);
            card.SyncInstalledState();
            Items.Add(card);
            _ = card.EnsureThumbnailAsync();
        }

        CanLoadMore = Items.Count < _results.Count;
    }

    /// <summary>Cards are reused across searches so an ongoing download keeps its progress bar.</summary>
    private PostCardViewModel GetOrCreateCard(Post post)
    {
        if (_cards.TryGetValue(post.Id, out var existing) && (existing.IsInstalling || existing.Post == post))
        {
            return existing;
        }

        var card = new PostCardViewModel(post, _installs, _thumbnails, OpenDetail);
        _cards[post.Id] = card;
        return card;
    }

    private void OpenDetail(PostCardViewModel card) =>
        SelectedPost = new PostDetailViewModel(card, _platform, PreviewSound, () => SelectedPost = null);
}
