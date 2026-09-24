# Research — steamdeckrepo.com API & Steam integration

_Surveyed on 2026-09-16, with a handful of manual requests and an identifiable User-Agent._

## Reference projects

| Project | Status | What we take from it |
|---|---|---|
| `CapitaineJSparrow/steam-repo-manager` | **Deleted** from GitHub; Flathub package `com.steamdeckrepo.manager` archived | — |
| `cmontesano/steam-repo-manager` (fork, 2022) | Frozen | `GET /api/posts?page=N`. **Anti-pattern**: empties the whole `movies/` folder before every install. |
| `waylaidwanderer/steam-deck-repo-manager` (official manager, Python/PySide6, 2026) | Active | `GET /api/posts/all` + disk cache, download through `/post/download/{id}`, `{slug}.webm` file, metadata in `movies/.manager/`, suspend → `deck-suspend-animation.webm` + `.bak`. |

## Endpoints

Laravel + Inertia/Vue site. Public routes are exposed by Ziggy in the HTML (`api.posts.index`, `api.posts.all`, `post.download`, `post.show`).

### `GET https://steamdeckrepo.com/api/posts/all`
- Response: `{ "posts": Post[], "last_cached_at": <unix seconds> }` — ~8,400 posts, 7.5 MB (1.9 MB gzipped).
- Regenerated server-side about once an hour (`Cache-Control: private, max-age=3600`).
- `Last-Modified` is returned; **`If-Modified-Since` → 304**: conditional refresh is almost free.

### `GET https://steamdeckrepo.com/api/posts`
- Response: `{ "posts": Post[], "sortOptions": {key: label}, "currentSort": string }` — **no pagination metadata**.
- Honored parameters: `page`, `per_page` (capped at 100), `sort`, `search`, `device`.
- **Ignored** parameters: `duration`, `type` (boot and suspend are mixed).
- `sort` ∈ `trending`, `downloads-desc`, `likes-desc`, `created_at-desc`, `created_at-asc`.

### `GET https://steamdeckrepo.com/post/download/{id}`
- `302` to a pre-signed Backblaze B2 URL (expires in 300 s), `content-disposition: attachment; filename="{slug}.webm"`.
- This is the site's official download link (it probably feeds the `downloads` counter — not verified).

### CDN `https://cdn.steamdeckrepo.com/{videos,thumbnails,previews}/…`
- `Accept-Ranges: bytes`, `Content-Length`, `Cache-Control: max-age=31536000` → resumable downloads and long caching are possible.

### Rate limits
- `x-ratelimit-limit: 60` / min on `/api/*`, `100` / min on `/post/download/*`.

## `Post` model

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

Quirks observed across the full catalog:
- `type`: `boot_video` (7,609), `suspend_video` (809), `boot_video_removed` (3 → to hide). Unknown values must be tolerated.
- `devices`: `steam_deck`, `steam_machine`; the front end also knows `steam_frame`. Unknown values must be tolerated.
- `video_duration` can be `null`; `video_preview` can be `null` or hosted on imgur; `content` is often empty.
- **246 duplicate slugs** → the local file name must include the `id`.
- **No OLED/LCD information** → that filter cannot be implemented.
- `url` uses `http://`.

## Steam locations

| Platform | Detection | Target folder |
|---|---|---|
| Windows | `HKCU\Software\Valve\Steam\SteamPath` (lowercase, `/` — normalize), fallback `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`, then `C:\Program Files (x86)\Steam` | `<Steam>\config\uioverrides\movies\` |
| Linux / Steam Deck | `~/.steam/root`, `~/.steam/steam`, `~/.local/share/Steam` | `<Steam>/config/uioverrides/movies/` |
| Linux Flatpak | `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`, `~/.var/app/com.valvesoftware.Steam/data/Steam` | same |
| Linux Snap | `~/snap/steam/common/.local/share/Steam` | same |

- The `uioverrides/movies` folder does not exist by default: create it.
- Format: a real WebM (renaming an MP4 gives a black screen), 1280×800, ≤ 30 s recommended.
- The user picks the video in **Settings > Customization** ("Use as Wake Movie" option for resuming from sleep).
- On Windows, the video only plays when **Big Picture** starts.
- A corrupted video can leave the Deck on a black screen: deleting the file restores the default animation.

## Mod manager practices we kept

- **Vortex**: deployment manifest + detection of files modified outside the manager → manifest with SHA-256, compared before deleting.
- **Mod Organizer 2 / r2modman**: profiles → of little use here (Steam handles the active selection); postponed.
- **Decky Loader**: clean uninstall, nothing left behind.
- **Transactions**: download to a `.part` file in the target folder, size check, atomic rename; manifest written through a temporary file + replace.
