using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BootVideoManager.Core.Catalog;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Models;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Sharing;

/// <summary>What a pack entry points to.</summary>
public enum VideoPackItemKind
{
    /// <summary>steamdeckrepo.com post, downloaded again from the catalog on import.</summary>
    Catalog,

    /// <summary>Stock Steam animation, already present on every PC: only enabled on import.</summary>
    SteamBuiltIn,
}

/// <summary>One video of a pack. Only references travel: the video itself always comes from the catalog or Steam.</summary>
public sealed record VideoPackItem
{
    public VideoPackItemKind Kind { get; set; }

    /// <summary>Post id for <see cref="VideoPackItemKind.Catalog"/>, stock file name for <see cref="VideoPackItemKind.SteamBuiltIn"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Informative only (shown when the video is no longer available).</summary>
    public string Title { get; set; } = string.Empty;

    public string? Author { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A shareable list of installed videos (<c>.bvmpack</c> file). Properties use <c>set</c> so missing fields keep their
/// defaults with the JSON source generator.
/// </summary>
public sealed record VideoPack
{
    public const string FormatName = "bootvideomanager.pack";
    public const int CurrentVersion = 1;
    public const string FileExtension = ".bvmpack";

    public string Format { get; set; } = string.Empty;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset CreatedAt { get; set; }

    public IReadOnlyList<VideoPackItem> Videos { get; set; } = [];
}

/// <summary>The file is not a usable pack; the message is ready to show.</summary>
public sealed class VideoPackException : Exception
{
    public VideoPackException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Result of <see cref="VideoPacks.Create"/>.</summary>
/// <param name="Pack">The pack to save.</param>
/// <param name="SkippedVideos">
/// Enabled videos that cannot be shared by reference (imported files, added outside the app, Steam cache); disabled
/// ones are not counted since they do not play anyway.
/// </param>
public sealed record VideoPackExport(VideoPack Pack, int SkippedVideos);

/// <summary>A catalog video to download on import, and the state it must end up in.</summary>
public sealed record PlannedDownload(Post Post, bool Enabled);

/// <summary>What importing a pack would do on this PC.</summary>
/// <param name="Downloads">Catalog videos to download.</param>
/// <param name="AlreadyInstalled">Pack videos already present here (only their enabled state may change).</param>
/// <param name="Unavailable">Videos gone from the catalog, or stock animations this Steam does not have.</param>
/// <param name="Items">Valid, de-duplicated pack entries, in file order.</param>
public sealed record VideoPackPlan(
    IReadOnlyList<PlannedDownload> Downloads,
    int AlreadyInstalled,
    IReadOnlyList<VideoPackItem> Unavailable,
    IReadOnlyList<VideoPackItem> Items)
{
    public bool IsEmpty => Downloads.Count == 0 && AlreadyInstalled == 0;
}

/// <summary>Creates, saves, reads and plans <see cref="VideoPack"/> files.</summary>
public static partial class VideoPacks
{
    /// <summary>A pack is a few KB; anything much larger is not one.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    public const int MaxItems = 5000;

    /// <summary>Builds a pack from the installed list: catalog videos with their state, and the enabled stock animations.</summary>
    public static VideoPackExport Create(IEnumerable<InstalledVideo> installed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var items = new List<VideoPackItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var video in installed)
        {
            if (video.IsBuiltIn)
            {
                // Disabled stock animations are left out so importing never switches off the recipient's own.
                if (video.IsEnabled && seen.Add("b:" + video.FileName))
                {
                    items.Add(new VideoPackItem { Kind = VideoPackItemKind.SteamBuiltIn, Id = video.FileName, Title = video.DisplayTitle });
                }
            }
            else if (video is { Status: not InstalledVideoStatus.Untracked, Entry: { Source: InstalledVideoSource.SteamDeckRepo, PostId: { } postId } entry }
                     && IsValidPostId(postId))
            {
                if (seen.Add("c:" + postId))
                {
                    items.Add(new VideoPackItem
                    {
                        Kind = VideoPackItemKind.Catalog,
                        Id = postId,
                        Title = entry.Title,
                        Author = entry.Author,
                        Enabled = video.IsEnabled,
                    });
                }
            }
            else if (video.IsEnabled && !video.IsSteamShopItem && video.Entry?.Source != InstalledVideoSource.SteamBuiltIn)
            {
                skipped++;
            }
        }

        return new VideoPackExport(new VideoPack { Format = VideoPack.FormatName, CreatedAt = now, Videos = items }, skipped);
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Save(IFileSystem fileSystem, string path, VideoPack pack)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pack);
        JsonFile.WriteAtomically(fileSystem, path, JsonSerializer.SerializeToUtf8Bytes(pack, VideoPackJsonContext.Default.VideoPack));
    }

    /// <exception cref="VideoPackException">Unreadable, too large, or not a pack.</exception>
    public static VideoPack Load(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            if (fileSystem.FileInfo.New(path).Length > MaxFileBytes)
            {
                throw new VideoPackException(NotAPackMessage);
            }

            bytes = fileSystem.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VideoPackException(Loc.T("Impossible de lire ce fichier.", "Could not read this file."), ex);
        }

        return Parse(bytes);
    }

    /// <summary>Reads a pack; malformed entries are dropped, a wrong format or a newer version is refused.</summary>
    /// <exception cref="VideoPackException">Not a pack, or made by a newer version of the app.</exception>
    public static VideoPack Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxFileBytes)
        {
            throw new VideoPackException(NotAPackMessage);
        }

        VideoPack? pack;
        try
        {
            pack = JsonSerializer.Deserialize(json, VideoPackJsonContext.Default.VideoPack);
        }
        catch (JsonException ex)
        {
            throw new VideoPackException(NotAPackMessage, ex);
        }

        if (pack is null || !string.Equals(pack.Format, VideoPack.FormatName, StringComparison.Ordinal) || pack.Version < 1)
        {
            throw new VideoPackException(NotAPackMessage);
        }

        if (pack.Version > VideoPack.CurrentVersion)
        {
            throw new VideoPackException(Loc.T(
                "Ce pack a été créé avec une version plus récente de Boot Video Manager. Mettez l'application à jour pour l'importer.",
                "This pack was made with a newer version of Boot Video Manager. Update the app to import it."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = (pack.Videos ?? [])
            .Where(item => item is not null && IsValid(item))
            .Where(item => seen.Add($"{item.Kind}:{item.Id}"))
            .Take(MaxItems)
            .Select(item => item with
            {
                Title = Truncate(item.Title ?? string.Empty, 200),
                Author = item.Author is null ? null : Truncate(item.Author, 100),
            })
            .ToArray();

        return pack with { Videos = items };
    }

    /// <summary>Matches a pack against the catalog and what is already installed here.</summary>
    public static VideoPackPlan Plan(VideoPack pack, IReadOnlyList<Post> catalog, IReadOnlyList<InstalledVideo> installed)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installed);

        var posts = new Dictionary<string, Post>(StringComparer.Ordinal);
        foreach (var post in catalog.Where(CatalogQueryEngine.IsListed))
        {
            posts.TryAdd(post.Id, post);
        }

        var downloads = new List<PlannedDownload>();
        var unavailable = new List<VideoPackItem>();
        var alreadyInstalled = 0;

        foreach (var item in pack.Videos)
        {
            if (FindInstalled(installed, item) is not null)
            {
                alreadyInstalled++;
            }
            else if (item.Kind == VideoPackItemKind.Catalog && posts.TryGetValue(item.Id, out var post))
            {
                downloads.Add(new PlannedDownload(post, item.Enabled));
            }
            else
            {
                unavailable.Add(item);
            }
        }

        return new VideoPackPlan(downloads, alreadyInstalled, unavailable, pack.Videos);
    }

    /// <summary>The installed video a pack entry refers to, if present here (enabled or not).</summary>
    public static InstalledVideo? FindInstalled(IReadOnlyList<InstalledVideo> installed, VideoPackItem item)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind == VideoPackItemKind.SteamBuiltIn
            ? installed.FirstOrDefault(v => v.IsBuiltIn && string.Equals(v.FileName, item.Id, PathUtilities.FileNameComparison))
            : installed.FirstOrDefault(v => v.Status != InstalledVideoStatus.Untracked && !v.IsBuiltIn
                && string.Equals(v.Entry?.PostId, item.Id, StringComparison.Ordinal));
    }

    /// <summary>True when the video belongs to the pack.</summary>
    public static bool Contains(VideoPack pack, InstalledVideo video)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(video);
        return pack.Videos.Any(item => ReferenceEquals(FindInstalled([video], item), video));
    }

    private static string NotAPackMessage => Loc.T(
        "Ce fichier n'est pas un pack Boot Video Manager valide.",
        "This file is not a valid Boot Video Manager pack.");

    private static bool IsValid(VideoPackItem item) => item.Kind switch
    {
        VideoPackItemKind.Catalog => IsValidPostId(item.Id),
        VideoPackItemKind.SteamBuiltIn => BuiltInVideos.IsSelectable(item.Id),
        _ => false,
    };

    private static bool IsValidPostId(string? id) => id is not null && PostIdPattern().IsMatch(id);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex PostIdPattern();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VideoPack))]
internal sealed partial class VideoPackJsonContext : JsonSerializerContext;
