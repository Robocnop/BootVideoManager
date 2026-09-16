# Architecture

Voir [research.md](research.md) pour les constats sur l'API et Steam.

## Décisions

| Sujet | Décision | Raison |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | .NET 8 sort du support le 2026-11-10 ; .NET 10 est supporté jusqu'en novembre 2028. |
| UI | Avalonia + CommunityToolkit.Mvvm | Cross-platform Windows / Linux / Steam Deck. |
| Source catalogue | `/api/posts/all` en cache disque, rafraîchi au plus 1×/h via `If-Modified-Since` | 1 requête (souvent 304) au lieu de dizaines de pages ; l'API paginée n'a pas de méta et ignore `type`/`duration`. |
| Recherche / tri / filtres | En local sur le catalogue | ~8 400 entrées : instantané, zéro charge serveur, fonctionne hors ligne. |
| Tri « Tendances » | Ordre récupéré via `/api/posts?sort=trending&per_page=…` (rare, mis en cache) | Formule serveur non reproductible localement. |
| Filtre OLED / LCD | **Abandonné** | Donnée absente de l'API. Remplacé par : type (démarrage / veille), appareil, durée. |
| Vidéos de veille | Incluses, installées comme les autres (`{slug}_{id}.webm`) | Steam les propose dans Personnalisation (« Use as Wake Movie ») ; on n'écrase jamais un fichier existant. |
| Téléchargement | `/post/download/{id}` (redirection) + fichier `.part` + renommage atomique | Passe par le lien officiel du site ; pas de fichier à moitié écrit. |
| Nom de fichier | `{slug}_{id}.webm`, slug assaini | 246 slugs dupliqués dans le catalogue. |
| Preview | LibVLCSharp, rendu dans un `WriteableBitmap` (callbacks vidéo) | Évite le problème « airspace » du `VideoView` natif (overlays, mode manette). |
| Tests | xUnit + System.IO.Abstractions.TestingHelpers | Pas de dépendance à licence commerciale (FluentAssertions ≥ 8). |

## Couches

```
src/BootVideoManager.Core           bibliothèque sans UI, 100 % testable
  Models/       Post, PostAuthor, VideoType, DeviceTag, CatalogQuery, InstalledVideo, Manifest
  Api/          RepoApiClient      HTTP + JSON (source-generated), User-Agent identifiable
                RepoJsonContext    contexte System.Text.Json
  Catalog/      CatalogCache       fichier cache + Last-Modified
                CatalogService     chargement / rafraîchissement / requêtes locales
  Steam/        ISteamLocator      Windows (registre), Linux (natif, Flatpak, Snap)
  Install/      InstallService     download → .part → move ; uninstall avec garde-fous
                ManifestStore      JSON atomique dans le dossier de config de l'app
                Reconciler         fichiers manquants / modifiés / ajoutés hors app
  Caching/      ThumbnailCache     cache disque des miniatures
src/BootVideoManager.App            Avalonia
  ViewModels/   Shell, Catalog, PostCard, PostDetail, Installed, Settings
  Views/
tests/BootVideoManager.Core.Tests
```

## Manifeste

Emplacement : `%APPDATA%\BootVideoManager\manifest.json` (Windows) ou `$XDG_CONFIG_HOME/BootVideoManager/manifest.json` (Linux, défaut `~/.config`).

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

Règles :
- Seuls les fichiers présents dans le manifeste **et** dont le SHA-256 correspond sont supprimés sans confirmation.
- Fichier modifié ou inconnu du manifeste → confirmation explicite.
- Entrée dont le fichier a disparu → nettoyée du manifeste à la réconciliation.

## Réseau

- Un `HttpClient` partagé, décompression gzip/brotli, timeout, User-Agent `BootVideoManager/<version> (+<repo url>)`.
- Respect de `429` / `Retry-After`, 3 tentatives max avec back-off ; aucune boucle.
- Erreurs réseau converties en exceptions métier (`RepoApiException`) affichées comme messages clairs.
