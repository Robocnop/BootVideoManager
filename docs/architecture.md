# Architecture

See [research.md](research.md) for the findings about the API and Steam.

## Decisions

| Topic | Decision | Reason |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | .NET 8 leaves support on 2026-11-10; .NET 10 is supported until November 2028. |
| UI | Avalonia 12 + CommunityToolkit.Mvvm (partial `[ObservableProperty]` properties) | Cross-platform Windows / Linux / Steam Deck. |
| Catalog source | `/api/posts/all` cached on disk, refreshed at most once an hour with `If-Modified-Since` | 1 request (often a 304) instead of dozens of pages; the paginated API has no metadata and ignores `type`/`duration`. |
| Search / sort / filters | Run locally on the catalog | ~8,400 entries: instant, zero server load, works offline. |
| "Trending" sort | Order fetched from `/api/posts?sort=trending&per_page=…` (rarely, cached) | The server formula cannot be reproduced locally. |
| OLED / LCD filter | **Dropped** | Not present in the API. Replaced by: type (boot / wake), device, duration. |
| Wake videos | Included, installed like the others (`{slug}_{id}.webm`) | Steam offers them in Customization ("Use as Wake Movie"); an existing file is never overwritten. |
| Download | `/post/download/{id}` (redirect) + `.part` file + atomic rename | Goes through the site's official link; never a half-written file. |
| File name | `{slug}_{id}.webm`, sanitized slug | 246 duplicate slugs in the catalog. |
| Preview | LibVLCSharp, rendered into a `WriteableBitmap` (video callbacks) | Avoids the "airspace" problem of the native `VideoView` (overlays, controller mode). |
| Tests | xUnit v3 (Microsoft.Testing.Platform) + System.IO.Abstractions.TestingHelpers + FakeTimeProvider | No commercially licensed dependency (FluentAssertions ≥ 8); .NET 10 requires MTP for `dotnet test`. |
| Enable / disable | Move to `uioverrides/movies_disabled/` (sibling folder, not read by Steam); Steam's built-in intros (`steamui/movies`) are enabled by copying them to `steam_default_{name}.webm`, tracked in the manifest (`source: SteamBuiltIn`) | Steam's shuffle picks from all of `uioverrides/movies`: the file being there *is* the selection. Nothing is re-downloaded or deleted, and Steam's own files are never touched. |
| Steam cache | `config/communityitemscache/startupmovies` is listed too (outside the manifest), disabling moves to `startupmovies_disabled/`; store items (`{communityitemid}_{sha1}.webm`) are shown but never moved or deleted | Steam stores purchased intros there, but the shuffle also plays any `.webm` dropped there by hand: without this, "invisible" intros play at startup. |
| Manual testing | `--steam-root <folder>` option | Try the app on a copy without touching the real Steam folder. |

## Layers

```
src/BootVideoManager.Core           UI-free library, 100% testable
  Models/       Post, PostAuthor, VideoType, DeviceTag, CatalogQuery, InstalledVideo, Manifest
  Api/          RepoApiClient      HTTP + JSON (source-generated), identifiable User-Agent
                RepoJsonContext    System.Text.Json context
  Catalog/      CatalogCache       cache file + Last-Modified
                CatalogService     loading / refreshing / local queries
  Steam/        ISteamLocator      Windows (registry), Linux (native, Flatpak, Snap)
  Install/      InstallService     download → .part → move; reconciled listing (tracked / modified / added
                                   outside the app); uninstall with safeguards; local import
                ManifestStore      atomic JSON in the app's config folder
                VideoFileNames     safe file names and path-traversal validation
  Caching/      ThumbnailCache     on-disk thumbnail cache
  Platform/     AppPaths, SettingsStore
src/BootVideoManager.App            Avalonia
  Services/     AppServices (composition), InstallCoordinator (shared state + confirmations),
                IPlatformServices (file pickers, opening URLs), UserMessages
  ViewModels/   MainWindow, Catalog, PostCard, PostDetail, Installed, Settings, dialogs
  Views/        MainWindow, CatalogView, InstalledView, SettingsView
  Controls/     VideoPreview + VlcFrameRenderer (libvlc → WriteableBitmap)
tests/BootVideoManager.Core.Tests   API parsing, HTTP client, retries, cache, queries, install/uninstall,
                                    manifest, Steam detection, thumbnails, settings
```

## Manifest

Location: `%APPDATA%\BootVideoManager\manifest.json` (Windows) or `$XDG_CONFIG_HOME/BootVideoManager/manifest.json` (Linux, default `~/.config`).

```json
{
  "version": 1,
  "entries": [
    {
      "postId": "AbCdE",
      "slug": "example_video",
      "title": "Example Video",
      "author": "ExampleAuthor",
      "type": "boot_video",
      "fileName": "example_video_AbCdE.webm",
      "moviesDirectory": "C:/Program Files (x86)/Steam/config/uioverrides/movies",
      "sha256": "…",
      "sizeBytes": 1000000,
      "installedAt": "2026-09-16T18:30:00Z"
    }
  ]
}
```

Rules:
- Only files that are in the manifest **and** whose SHA-256 matches are deleted without confirmation.
- A modified file, or one unknown to the manifest → explicit confirmation.
- An entry whose file has disappeared (neither in `movies/` nor in `movies_disabled/`) → removed from the manifest during reconciliation.
- Steam's built-in intros: never deleted; "disable" only removes the tracked copy (refused if it was modified).

## Network

- One shared `HttpClient`, gzip/brotli decompression, timeout, User-Agent `BootVideoManager/<version> (+<repo url>)`.
- Honors `429` / `Retry-After`, at most 3 attempts with back-off; no loops.
- Network errors are turned into domain exceptions (`RepoApiException`) shown as clear messages.
