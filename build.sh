#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_path="$repo_dir/src/Jellyfin.Plugin.TrailerReel/Jellyfin.Plugin.TrailerReel.csproj"
test_path="$repo_dir/tests/Jellyfin.Plugin.TrailerReel.Tests/Jellyfin.Plugin.TrailerReel.Tests.csproj"
build_dir="$repo_dir/src/Jellyfin.Plugin.TrailerReel/bin/Release/net10.0"
dist_dir="$repo_dir/dist"
stage_dir="$dist_dir/TrailerReel_0.3.0.0"
source_zip="$dist_dir/jellyfin-trailer-reel-source_0.3.0.0.zip"

if [[ "${1:-}" == "--package-only" ]]; then
    if [[ ! -f "$build_dir/Jellyfin.Plugin.TrailerReel.dll" \
        || ! -f "$build_dir/Jellyfin.Plugin.TrailerReel.pdb" ]]; then
        echo "Package-only mode requires an existing validated Release build." >&2
        exit 1
    fi
else
    dotnet restore "$test_path"
    dotnet build "$test_path" -c Release --no-restore
    dotnet "$repo_dir/tests/Jellyfin.Plugin.TrailerReel.Tests/bin/Release/net10.0/Jellyfin.Plugin.TrailerReel.Tests.dll"
fi

rm -rf "$stage_dir"
mkdir -p "$stage_dir"
cp "$build_dir/Jellyfin.Plugin.TrailerReel.dll" "$stage_dir/"
cp "$build_dir/Jellyfin.Plugin.TrailerReel.pdb" "$stage_dir/"
cp "$repo_dir/src/Jellyfin.Plugin.TrailerReel/meta.json" "$stage_dir/"
cp "$repo_dir/README.md" "$stage_dir/"
cp "$repo_dir/INSTALL-UNRAID.md" "$stage_dir/"
cp "$repo_dir/LICENSE" "$stage_dir/"
cp -R "$repo_dir/tools" "$stage_dir/"
cp -R "$repo_dir/deploy" "$stage_dir/"

rm -f "$dist_dir/trailer-reel_0.3.0.0.zip"
(
    cd "$dist_dir"
    zip -qr "trailer-reel_0.3.0.0.zip" "TrailerReel_0.3.0.0"
)

rm -f "$source_zip"
(
    cd "$repo_dir/.."
    zip -qr "$source_zip" "$(basename "$repo_dir")" \
        -x '*/dist/*' '*/bin/*' '*/obj/*' '*/.git/*'
)

echo "Created $dist_dir/trailer-reel_0.3.0.0.zip"
echo "Created $source_zip"
