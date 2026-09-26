#!/usr/bin/env bash
# Builds the GitHub Pages site that hosts the Flatpak repository (robocnop.github.io/BootVideoManager):
#   repo/                                    signed OSTree repository, read by `flatpak update`
#   io.github.Robocnop.BootVideoManager.flatpakref   one-click install from a software center
#   BootVideoManager.flatpakrepo             adds the repository only
# Usage: make-site.sh <ostree repo> <output dir> <public repo URL>
set -euo pipefail

repo="$1"
site="$2"
url="${3%/}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
id=io.github.Robocnop.BootVideoManager
key="$(base64 -w0 "$here/boot-video-manager.gpg")"

rm -rf "$site"
mkdir -p "$site"
cp -r "$repo" "$site/repo"
cp "$here/../linux/boot-video-manager.png" "$site/icon.png"

cat > "$site/$id.flatpakref" <<EOF
[Flatpak Ref]
Name=$id
Branch=stable
Title=Boot Video Manager
Url=$url/repo/
RuntimeRepo=https://dl.flathub.org/repo/flathub.flatpakrepo
IsRuntime=false
SuggestRemoteName=bootvideomanager
GPGKey=$key
EOF

cat > "$site/BootVideoManager.flatpakrepo" <<EOF
[Flatpak Repo]
Title=Boot Video Manager
Url=$url/repo/
Homepage=https://github.com/Robocnop/BootVideoManager
Comment=Custom startup videos for Steam
Icon=$url/icon.png
GPGKey=$key
EOF

cat > "$site/index.html" <<EOF
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Boot Video Manager for Linux</title>
<style>
  body { font-family: system-ui, sans-serif; max-width: 44rem; margin: 3rem auto; padding: 0 1rem; line-height: 1.5;
         background: #1b2838; color: #c7d5e0; }
  a { color: #66c0f4; }
  code { background: #16202d; padding: .1rem .35rem; border-radius: 4px; }
  .button { display: inline-block; background: #5c7e10; color: #fff; padding: .6rem 1rem; border-radius: 4px;
            text-decoration: none; font-weight: 600; }
</style>
</head>
<body>
<h1><img src="icon.png" alt="" width="48" height="48" style="vertical-align: middle"> Boot Video Manager</h1>
<p>Custom startup videos for Steam Big Picture and the Steam Deck. Linux package (Flatpak), updated with every release.</p>
<p><a class="button" href="$id.flatpakref">Install</a></p>
<p>Opens in your software center (Linux Mint, Steam Deck desktop mode, GNOME Software, KDE Discover…), which then
keeps the app up to date. From a terminal:</p>
<p><code>flatpak install --user $url/$id.flatpakref</code></p>
<p>Source code, Windows downloads and help: <a href="https://github.com/Robocnop/BootVideoManager">github.com/Robocnop/BootVideoManager</a>.</p>
</body>
</html>
EOF

echo "Site written to $site"
