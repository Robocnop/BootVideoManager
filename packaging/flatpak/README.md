# Flatpak / Flathub

App ID: `io.github.Robocnop.BootVideoManager` (its last component maps to github.com/Robocnop/BootVideoManager,
which lets Flathub verify the app).

| File | Role |
|---|---|
| `io.github.Robocnop.BootVideoManager.yml` | Manifest: GNOME 51 runtime (WebKitGTK 4.1 for the sign-in window), .NET 10 SDK extension, a minimal FFmpeg 4 + libvlc for the previews, then the app. |
| `nuget-sources.json` | Every NuGet package with its SHA-512: Flathub builds offline. **Regenerate it whenever a package changes.** |
| `*.desktop`, `*.metainfo.xml` | Launcher and store listing (screenshots are loaded from the release tag). |
| `boot-video-manager.gpg` | Public key of the update repository (the private key is the `FLATPAK_GPG_PRIVATE_KEY` secret). |
| `make-site.sh` | Builds the GitHub Pages site: signed repository, `.flatpakref`, `.flatpakrepo`, install page. |
| `flathub.json` | Kept for a future Flathub submission: x86_64 only (Steam has no official ARM Linux build). |

## Build and run locally (Linux, or WSL on Windows)

```sh
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.gnome.Sdk//51 org.freedesktop.Sdk.Extension.dotnet10//26.08 org.flatpak.Builder
flatpak-builder --user --install --force-clean --install-deps-from=flathub build-dir \
  packaging/flatpak/io.github.Robocnop.BootVideoManager.yml
flatpak run io.github.Robocnop.BootVideoManager
```

Build from a clean tree: Windows `bin/` and `obj/` folders confuse `dotnet publish` inside the sandbox.

Check it the way Flathub does:

```sh
flatpak run --command=flatpak-builder-lint org.flatpak.Builder manifest packaging/flatpak/io.github.Robocnop.BootVideoManager.yml
flatpak run --command=flatpak-builder-lint org.flatpak.Builder repo repo   # after --repo=repo
```

## Regenerate `nuget-sources.json`

With [flatpak-dotnet-generator.py](https://github.com/flatpak/flatpak-builder-tools/tree/master/dotnet)
and `org.freedesktop.Sdk//26.08` + `org.freedesktop.Sdk.Extension.dotnet10//26.08` installed:

```sh
python3 flatpak-dotnet-generator.py packaging/flatpak/nuget-sources.json \
  src/BootVideoManager.App/BootVideoManager.App.csproj --dotnet 10 --freedesktop 26.08 --runtime linux-x64
```

## Updates (self-hosted repository)

Every release (not pre-releases) runs the `flatpak` job of `.github/workflows/release.yml`:

1. pulls the currently published version back from https://robocnop.github.io/BootVideoManager/repo (for deltas);
2. builds the app into that OSTree repository on branch `stable`, signed with the `FLATPAK_GPG_PRIVATE_KEY` secret;
3. makes `BootVideoManager-<version>-x86_64.flatpak`, which embeds the repository URL and public key: installing it
   adds a `bootvideomanager-origin` remote, so software centers and `flatpak update` pick up later releases;
4. deploys the site (repository + `io.github.Robocnop.BootVideoManager.flatpakref` + install page) to GitHub Pages.

Add the `<release>` entry to the metainfo for each version. If the signing key is ever replaced, users must
reinstall once: their remote trusts the old public key.

Flathub is not used for now: its rules forbid AI-generated manifests and AI-opened submissions, and ask for a longer
project history.
