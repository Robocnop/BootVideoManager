# Recherche — API steamdeckrepo.com & intégration Steam

_Relevé effectué le 2026-09-16, requêtes manuelles peu nombreuses avec un User-Agent identifiable._

## Projets de référence

| Projet | État | Ce qu'on en retient |
|---|---|---|
| `CapitaineJSparrow/steam-repo-manager` | **Supprimé** de GitHub ; paquet Flathub `com.steamdeckrepo.manager` archivé | — |
| `cmontesano/steam-repo-manager` (fork, 2022) | Figé | `GET /api/posts?page=N`. **Anti-pattern** : vide tout `movies/` avant chaque install. |
| `waylaidwanderer/steam-deck-repo-manager` (manager officiel, Python/PySide6, 2026) | Actif | `GET /api/posts/all` + cache disque, download via `/post/download/{id}`, fichier `{slug}.webm`, métadonnées dans `movies/.manager/`, suspend → `deck-suspend-animation.webm` + `.bak`. |

## Endpoints

Site Laravel + Inertia/Vue. Routes publiques exposées par Ziggy dans le HTML (`api.posts.index`, `api.posts.all`, `post.download`, `post.show`).

### `GET https://steamdeckrepo.com/api/posts/all`
- Réponse : `{ "posts": Post[], "last_cached_at": <unix seconds> }` — ~8 400 posts, 7,5 Mo (1,9 Mo gzip).
- Régénéré côté serveur environ toutes les heures (`Cache-Control: private, max-age=3600`).
- `Last-Modified` renvoyé ; **`If-Modified-Since` → 304** : rafraîchissement conditionnel quasi gratuit.

### `GET https://steamdeckrepo.com/api/posts`
- Réponse : `{ "posts": Post[], "sortOptions": {key: label}, "currentSort": string }` — **aucune méta de pagination**.
- Paramètres respectés : `page`, `per_page`, `sort`, `search`, `device`.
- Paramètres **ignorés** : `duration`, `type` (mélange boot et suspend).
- `sort` ∈ `trending`, `downloads-desc`, `likes-desc`, `created_at-desc`, `created_at-asc`.

### `GET https://steamdeckrepo.com/post/download/{id}`
- `302` vers une URL Backblaze B2 pré-signée (expire en 300 s), `content-disposition: attachment; filename="{slug}.webm"`.
- C'est le lien de téléchargement officiel du site (il alimente vraisemblablement le compteur `downloads` — non vérifié).

### CDN `https://cdn.steamdeckrepo.com/{videos,thumbnails,previews}/…`
- `Accept-Ranges: bytes`, `Content-Length`, `Cache-Control: max-age=31536000` → reprise de téléchargement et cache long possibles.

### Limites de débit
- `x-ratelimit-limit: 60` / min sur `/api/*`, `100` / min sur `/post/download/*`.

## Modèle `Post`

```json
{
  "id": "MnZgE",
  "slug": "star_wars_intro_disney",
  "title": "Star Wars Intro (Disney+)",
  "content": "…",
  "user": { "id": 2, "steam_name": "Jaidek", "steam_avatar": "https://…" },
  "thumbnail": "https://cdn.steamdeckrepo.com/thumbnails/….png",
  "video": "https://cdn.steamdeckrepo.com/videos/….webm",
  "video_duration": 10,
  "video_preview": "https://cdn.steamdeckrepo.com/previews/….mp4",
  "created_at": "2022-10-06T16:34:50.000000Z",
  "updated_at": "2022-10-06T16:34:50.000000Z",
  "url": "http://steamdeckrepo.com/post/MnZgE/star_wars_intro_disney",
  "likes": 224,
  "downloads": 34826,
  "type": "boot_video",
  "devices": ["steam_deck"]
}
```

Particularités observées sur le catalogue complet :
- `type` : `boot_video` (7 609), `suspend_video` (809), `boot_video_removed` (3 → à masquer). Valeurs inconnues à tolérer.
- `devices` : `steam_deck`, `steam_machine` ; le front connaît aussi `steam_frame`. Valeurs inconnues à tolérer.
- `video_duration` peut être `null` ; `video_preview` peut être `null` ou hébergé sur imgur ; `content` souvent vide.
- **246 slugs dupliqués** → le nom de fichier local doit inclure l'`id`.
- **Aucune information OLED/LCD** → filtre non implémentable.
- `url` est en `http://`.

## Emplacements Steam

| Plateforme | Détection | Dossier cible |
|---|---|---|
| Windows | `HKCU\Software\Valve\Steam\SteamPath` (minuscules, `/` — normaliser), repli `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`, puis `C:\Program Files (x86)\Steam` | `<Steam>\config\uioverrides\movies\` |
| Linux / Steam Deck | `~/.steam/root`, `~/.steam/steam`, `~/.local/share/Steam` | `<Steam>/config/uioverrides/movies/` |
| Linux Flatpak | `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`, `~/.var/app/com.valvesoftware.Steam/data/Steam` | idem |
| Linux Snap | `~/snap/steam/common/.local/share/Steam` | idem |

- Le dossier `uioverrides/movies` n'existe pas par défaut : le créer.
- Format : vrai WebM (renommer un MP4 donne un écran noir), 1280×800, ≤ 30 s recommandé.
- L'utilisateur choisit la vidéo dans **Paramètres > Personnalisation** (option « Use as Wake Movie » pour la sortie de veille).
- Sous Windows, la vidéo ne joue qu'au lancement en **Big Picture**.
- Une vidéo corrompue peut bloquer le Deck sur écran noir : supprimer le fichier restaure l'animation par défaut.

## Pratiques des mod managers retenues

- **Vortex** : manifeste de déploiement + détection des fichiers modifiés en dehors du manager → manifeste avec SHA-256, comparaison avant suppression.
- **Mod Organizer 2 / r2modman** : profils → peu utiles ici (Steam gère la sélection active) ; reportés.
- **Decky Loader** : désinstallation propre, rien laissé derrière.
- **Transactions** : téléchargement en `.part` dans le dossier cible, vérification de taille, renommage atomique ; manifeste écrit via fichier temporaire + remplacement.
