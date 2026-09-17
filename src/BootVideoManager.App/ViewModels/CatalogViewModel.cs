using System.Collections.ObjectModel;
using System.Globalization;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Caching;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>Catalog tab: search, filters, sort, paged grid and detail panel.</summary>
public sealed partial class CatalogViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 48;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly CatalogService _catalog;
    private readonly ThumbnailCache _thumbnails;
    private readonly InstallCoordinator _installs;
    private readonly IPlatformServices _platform;
    private readonly Dictionary<string, PostCardViewModel> _cards = new(StringComparer.Ordinal);
    private IReadOnlyList<Post> _results = [];
    private CatalogSnapshot? _snapshot;
    private CancellationTokenSource? _searchDebounce;

    public CatalogViewModel(CatalogService catalog, ThumbnailCache thumbnails, InstallCoordinator installs, IPlatformServices platform)
    {
        _catalog = catalog;
        _thumbnails = thumbnails;
        _installs = installs;
        _platform = platform;

        SelectedSort = SortOptions[0];
        SelectedType = TypeOptions[0];
        SelectedDevice = DeviceOptions[0];
        SelectedDuration = DurationOptions[0];

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

    public IReadOnlyList<Choice<CatalogSort>> SortOptions { get; } =
    [
        new("Tendances", CatalogSort.Trending),
        new("Les plus téléchargées", CatalogSort.MostDownloaded),
        new("Les plus aimées", CatalogSort.MostLiked),
        new("Les plus récentes", CatalogSort.Newest),
        new("Les plus anciennes", CatalogSort.Oldest),
    ];

    public IReadOnlyList<Choice<VideoType?>> TypeOptions { get; } =
    [
        new("Tous les types", null),
        new("Vidéos de démarrage", VideoType.BootVideo),
        new("Animations de veille", VideoType.SuspendVideo),
    ];

    public IReadOnlyList<Choice<DeviceTag?>> DeviceOptions { get; } =
    [
        new("Tous les appareils", null),
        new("Steam Deck", DeviceTag.SteamDeck),
        new("Steam Machine", DeviceTag.SteamMachine),
    ];

    public IReadOnlyList<Choice<DurationRange>> DurationOptions { get; } =
    [
        new("Toutes les durées", new DurationRange(null, null)),
        new("Courtes (10 s ou moins)", new DurationRange(null, 10)),
        new("Moyennes (11 à 30 s)", new DurationRange(11, 30)),
        new("Longues (plus de 30 s)", new DurationRange(31, null)),
    ];

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
    public partial string ResultText { get; set; } = "Chargement du catalogue…";

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
            WarningMessage = _snapshot.RefreshError is { } refreshError
                ? string.Create(CultureInfo.CurrentCulture, $"Catalogue hors ligne (dernière mise à jour le {_snapshot.FetchedAt.LocalDateTime:g}). {UserMessages.For(refreshError)}")
                : null;
            StatusText = string.Create(CultureInfo.CurrentCulture, $"Catalogue mis à jour le {_snapshot.FetchedAt.LocalDateTime:g}");
            ApplyQuery();
        }
        catch (RepoApiException ex)
        {
            ErrorMessage = UserMessages.For(ex);
            ResultText = "Catalogue indisponible";
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

    partial void OnSelectedSortChanged(Choice<CatalogSort> value) => ApplyQuery();

    partial void OnSelectedTypeChanged(Choice<VideoType?> value) => ApplyQuery();

    partial void OnSelectedDeviceChanged(Choice<DeviceTag?> value) => ApplyQuery();

    partial void OnSelectedDurationChanged(Choice<DurationRange> value) => ApplyQuery();

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
            0 => "Aucune vidéo ne correspond à ces critères.",
            1 => "1 vidéo",
            _ => string.Create(CultureInfo.CurrentCulture, $"{_results.Count:N0} vidéos"),
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
        SelectedPost = new PostDetailViewModel(card, _platform, () => SelectedPost = null);
}
