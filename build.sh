#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_path="$repo_dir/src/Jellyfin.Plugin.TrailerReel/Jellyfin.Plugin.TrailerReel.csproj"
test_path="$repo_dir/tests/Jellyfin.Plugin.TrailerReel.Tests/Jellyfin.Plugin.TrailerReel.Tests.csproj"
build_dir="$repo_dir/src/Jellyfin.Plugin.TrailerReel/bin/Release/net10.0"
dist_dir="$repo_dir/dist"
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$project_path" | head -n 1)"

if [[ -z "$version" ]]; then
    echo "Could not read the plugin version from $project_path" >&2
    exit 1
fi

package_dir_name="TrailerReel_$version"
stage_dir="$dist_dir/$package_dir_name"
catalog_zip="$dist_dir/trailer-reel_$version.zip"
manual_zip="$dist_dir/trailer-reel_$version-manual.zip"
source_zip="$dist_dir/jellyfin-trailer-reel-source_$version.zip"
checksum_file="$dist_dir/SHA256SUMS-$version.txt"

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

rm -f "$catalog_zip"
(
    cd "$stage_dir"
    zip -qr "$catalog_zip" \
        "Jellyfin.Plugin.TrailerReel.dll" \
        "Jellyfin.Plugin.TrailerReel.pdb" \
        "meta.json"
)

if unzip -Z1 "$catalog_zip" | grep -q '/'; then
    echo "Catalog ZIP must contain only root-level runtime files." >&2
    exit 1
fi

rm -f "$manual_zip"
(
    cd "$dist_dir"
    zip -qr "$manual_zip" "$package_dir_name"
)

rm -f "$source_zip"
(
    cd "$repo_dir/.."
    zip -qr "$source_zip" "$(basename "$repo_dir")" \
        -x '*/dist/*' '*/bin/*' '*/obj/*' '*/.git/*'
)

rm -f "$checksum_file"
(
    cd "$dist_dir"
    sha256sum \
        "$(basename "$catalog_zip")" \
        "$(basename "$manual_zip")" \
        "$(basename "$source_zip")" > "$(basename "$checksum_file")"
)

echo "Created $catalog_zip"
echo "Created $manual_zip"
echo "Created $source_zip"
echo "Created $checksum_file"
