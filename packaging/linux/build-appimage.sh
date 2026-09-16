#!/usr/bin/env bash
# Builds artifacts/BootVideoManager-<version>-x86_64.AppImage.
#
# Requirements (Linux only): .NET 10 SDK and appimagetool on PATH
# (https://github.com/AppImage/appimagetool/releases).
#
# libvlc is NOT bundled: previews need VLC installed on the host. Without it the application still
# works, only the in-app preview is disabled with an explanatory message.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
here="$root/packaging/linux"
app="$root/src/BootVideoManager.App/BootVideoManager.App.csproj"
appdir="$root/artifacts/AppDir"
version="$(dotnet msbuild "$app" -getProperty:Version | tr -d '[:space:]')"

command -v appimagetool >/dev/null || { echo "appimagetool not found on PATH" >&2; exit 1; }

rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/lib/boot-video-manager" \
         "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/scalable/apps"

dotnet publish "$app" -c Release -r linux-x64 --self-contained -o "$appdir/usr/lib/boot-video-manager"
chmod +x "$appdir/usr/lib/boot-video-manager/BootVideoManager"
ln -s ../lib/boot-video-manager/BootVideoManager "$appdir/usr/bin/BootVideoManager"

cp "$here/boot-video-manager.desktop" "$appdir/usr/share/applications/"
cp "$here/boot-video-manager.desktop" "$appdir/"
cp "$here/boot-video-manager.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/"
cp "$here/boot-video-manager.svg" "$appdir/"

cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/boot-video-manager/BootVideoManager" "$@"
EOF
chmod +x "$appdir/AppRun"

ARCH=x86_64 appimagetool "$appdir" "$root/artifacts/BootVideoManager-$version-x86_64.AppImage"
