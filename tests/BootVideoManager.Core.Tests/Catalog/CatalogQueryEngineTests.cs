using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Tests.Catalog;

public class CatalogQueryEngineTests
{
    private static readonly Dictionary<string, int> NoTrending = [];

    private static string[] Ids(IEnumerable<Post> posts, CatalogQuery query, IReadOnlyDictionary<string, int>? trending = null) =>
        CatalogQueryEngine.Apply(posts, trending ?? NoTrending, query).Select(p => p.Id).ToArray();

    [Fact]
    public void RemovedAndUnknownKinds_AreNeverListed()
    {
        Post[] posts =
        [
            PostFactory.Create("boot", type: VideoType.BootVideo),
            PostFactory.Create("suspend", type: VideoType.SuspendVideo),
            PostFactory.Create("removed", type: VideoType.Removed),
            PostFactory.Create("unknown", type: VideoType.Unknown),
        ];

        Assert.Equal(["boot", "suspend"], Ids(posts, new CatalogQuery { Sort = CatalogSort.Oldest }).Order());
    }

    [Fact]
    public void TypeFilter_KeepsOnlyRequestedKind()
    {
        Post[] posts = [PostFactory.Create("boot"), PostFactory.Create("suspend", type: VideoType.SuspendVideo)];

        Assert.Equal(["suspend"], Ids(posts, new CatalogQuery { Type = VideoType.SuspendVideo }));
    }

    [Fact]
    public void DeviceFilter_MatchesAnyTaggedDevice()
    {
        Post[] posts =
        [
            PostFactory.Create("deck", devices: [DeviceTag.SteamDeck]),
            PostFactory.Create("both", devices: [DeviceTag.SteamDeck, DeviceTag.SteamMachine]),
            PostFactory.Create("machine", devices: [DeviceTag.SteamMachine]),
            PostFactory.Create("untagged", devices: []),
        ];

        Assert.Equal(["both", "machine"], Ids(posts, new CatalogQuery { Device = DeviceTag.SteamMachine }).Order());
    }

    [Fact]
    public void DurationFilter_IsInclusive_AndExcludesUnknownDurations()
    {
        Post[] posts =
        [
            PostFactory.Create("short", durationSeconds: 3),
            PostFactory.Create("five", durationSeconds: 5),
            PostFactory.Create("thirty", durationSeconds: 30),
            PostFactory.Create("long", durationSeconds: 90),
            PostFactory.Create("unknown", durationSeconds: null),
        ];
        var query = new CatalogQuery { MinDuration = TimeSpan.FromSeconds(5), MaxDuration = TimeSpan.FromSeconds(30) };

        Assert.Equal(["five", "thirty"], Ids(posts, query).Order());
        Assert.Contains("unknown", Ids(posts, new CatalogQuery()));
    }

    [Fact]
    public void Search_IgnoresCaseAndAccents_AndRequiresEveryTerm()
    {
        Post[] posts =
        [
            PostFactory.Create("pokemon", title: "Pokémon Yellow Boot"),
            PostFactory.Create("pokemon-red", title: "POKEMON red"),
            PostFactory.Create("zelda", title: "Zelda"),
        ];

        Assert.Equal(["pokemon", "pokemon-red"], Ids(posts, new CatalogQuery { SearchText = "pokemon" }).Order());
        Assert.Equal(["pokemon"], Ids(posts, new CatalogQuery { SearchText = "  pokémon   yellow " }));
        Assert.Empty(Ids(posts, new CatalogQuery { SearchText = "pokemon zelda" }));
    }

    [Fact]
    public void Search_MatchesAuthorName()
    {
        Post[] posts = [PostFactory.Create("a", author: "Jaidek"), PostFactory.Create("b", author: "Someone")];

        Assert.Equal(["a"], Ids(posts, new CatalogQuery { SearchText = "jaidek" }));
    }

    [Fact]
    public void BlankSearch_ReturnsEverything()
    {
        Post[] posts = [PostFactory.Create("a"), PostFactory.Create("b")];

        Assert.Equal(2, Ids(posts, new CatalogQuery { SearchText = "   " }).Length);
    }

    [Fact]
    public void Sorts_OrderAsTheSiteDoes()
    {
        var day = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Post[] posts =
        [
            PostFactory.Create("old-popular", likes: 10, downloads: 900, createdAt: day),
            PostFactory.Create("new-liked", likes: 50, downloads: 100, createdAt: day.AddDays(20)),
            PostFactory.Create("mid", likes: 5, downloads: 500, createdAt: day.AddDays(10)),
        ];

        Assert.Equal(["old-popular", "mid", "new-liked"], Ids(posts, new CatalogQuery { Sort = CatalogSort.MostDownloaded }));
        Assert.Equal(["new-liked", "old-popular", "mid"], Ids(posts, new CatalogQuery { Sort = CatalogSort.MostLiked }));
        Assert.Equal(["new-liked", "mid", "old-popular"], Ids(posts, new CatalogQuery { Sort = CatalogSort.Newest }));
        Assert.Equal(["old-popular", "mid", "new-liked"], Ids(posts, new CatalogQuery { Sort = CatalogSort.Oldest }));
    }

    [Fact]
    public void TrendingSort_UsesServerRanksFirst_ThenDownloads()
    {
        Post[] posts =
        [
            PostFactory.Create("unranked-popular", downloads: 10_000),
            PostFactory.Create("unranked", downloads: 10),
            PostFactory.Create("second", downloads: 1),
            PostFactory.Create("first", downloads: 2),
        ];
        var ranks = new Dictionary<string, int> { ["first"] = 0, ["second"] = 1 };

        Assert.Equal(
            ["first", "second", "unranked-popular", "unranked"],
            Ids(posts, new CatalogQuery { Sort = CatalogSort.Trending }, ranks));
    }

    [Fact]
    public void Ties_AreBrokenDeterministically()
    {
        Post[] posts = [PostFactory.Create("b"), PostFactory.Create("a"), PostFactory.Create("c")];

        Assert.Equal(["a", "b", "c"], Ids(posts, new CatalogQuery { Sort = CatalogSort.MostLiked }));
    }
}
