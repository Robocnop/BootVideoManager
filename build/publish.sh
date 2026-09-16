#!/usr/bin/env bash
# Runs the tests, then builds self-contained release archives of Boot Video Manager.
# Usage: ./build/publish.sh [rid ...]      (default: linux-x64 win-x64)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app="$root/src/BootVideoManager.App/BootVideoManager.App.csproj"
output="$root/artifacts/publish"
runtimes=("$@")
[[ ${#runtimes[@]} -eq 0 ]] && runtimes=(linux-x64 win-x64)

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  dotnet test --solution "$root/BootVideoManager.slnx" -c Release
fi

version="$(dotnet msbuild "$app" -getProperty:Version | tr -d '[:space:]')"

for rid in "${runtimes[@]}"; do
  dir="$output/$rid"
  rm -rf "$dir"
  dotnet publish "$app" -c Release -r "$rid" --self-contained -o "$dir"

  archive="$output/BootVideoManager-$version-$rid"
  if [[ "$rid" == win-* ]]; then
    (cd "$dir" && zip -qr "$archive.zip" .)
    echo "Created $archive.zip"
  else
    chmod +x "$dir/BootVideoManager"
    tar -czf "$archive.tar.gz" -C "$dir" .
    echo "Created $archive.tar.gz"
  fi
done
