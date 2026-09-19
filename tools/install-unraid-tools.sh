#!/usr/bin/env bash
set -euo pipefail

# Run this script from the Unraid host, not from inside the Jellyfin container.
# The defaults are derived from the plugin's location under Jellyfin's config directory.

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
plugin_dir="$(cd "$script_dir/.." && pwd)"
config_dir="$(cd "$plugin_dir/../.." && pwd)"
jellyfin_appdata_dir="$(dirname "$config_dir")"
tool_dir="${TRAILER_REEL_TOOL_DIR:-$config_dir/trailer-tools}"
trailer_dir="${TRAILER_REEL_MEDIA_DIR:-$jellyfin_appdata_dir/trailer-reel/trailers}"
jellyfin_owner="${TRAILER_REEL_JELLYFIN_OWNER:-}"

for command_name in curl unzip sha256sum install awk chown stat; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        printf 'Required host command is missing: %s\n' "$command_name" >&2
        exit 1
    fi
done

if [[ -z "$jellyfin_owner" ]]; then
    jellyfin_owner="$(stat -c '%u:%g' "$config_dir")"
fi

case "$(uname -m)" in
    x86_64|amd64)
        deno_asset="deno-x86_64-unknown-linux-gnu.zip"
        ;;
    *)
        printf 'Unsupported host architecture: %s. This installer requires x86_64.\n' "$(uname -m)" >&2
        exit 1
        ;;
esac

work_dir="$(mktemp -d)"
cleanup() {
    rm -rf "$work_dir"
}
trap cleanup EXIT

resolve_latest_tag() {
    local repository="$1"
    local release_url
    release_url="$(curl -fsSL --retry 4 --retry-all-errors \
        -o /dev/null -w '%{url_effective}' \
        "https://github.com/${repository}/releases/latest")"
    printf '%s\n' "${release_url##*/}"
}

yt_dlp_tag="$(resolve_latest_tag yt-dlp/yt-dlp)"
deno_tag="$(resolve_latest_tag denoland/deno)"

printf 'Downloading yt-dlp %s from the official release...\n' "$yt_dlp_tag"
curl -fL --retry 4 --retry-all-errors \
    "https://github.com/yt-dlp/yt-dlp/releases/download/${yt_dlp_tag}/yt-dlp_linux" \
    -o "$work_dir/yt-dlp_linux"
curl -fL --retry 4 --retry-all-errors \
    "https://github.com/yt-dlp/yt-dlp/releases/download/${yt_dlp_tag}/SHA2-256SUMS" \
    -o "$work_dir/yt-dlp.SHA2-256SUMS"

yt_dlp_checksum="$(awk '$2 == "yt-dlp_linux" { print; exit }' "$work_dir/yt-dlp.SHA2-256SUMS")"
if [[ -z "$yt_dlp_checksum" ]]; then
    printf 'The official yt-dlp checksum file did not contain yt-dlp_linux.\n' >&2
    exit 1
fi
(
    cd "$work_dir"
    printf '%s\n' "$yt_dlp_checksum" | sha256sum -c -
)

printf 'Downloading Deno %s from the official release...\n' "$deno_tag"
curl -fL --retry 4 --retry-all-errors \
    "https://github.com/denoland/deno/releases/download/${deno_tag}/${deno_asset}" \
    -o "$work_dir/$deno_asset"
curl -fL --retry 4 --retry-all-errors \
    "https://github.com/denoland/deno/releases/download/${deno_tag}/${deno_asset}.sha256sum" \
    -o "$work_dir/${deno_asset}.sha256sum"
(
    cd "$work_dir"
    sha256sum -c "${deno_asset}.sha256sum"
    unzip -q "$deno_asset"
)

install -d -m 0755 "$tool_dir" "$trailer_dir"
install -m 0755 "$work_dir/yt-dlp_linux" "$tool_dir/yt-dlp"
install -m 0755 "$work_dir/deno" "$tool_dir/deno"

if [[ ! -e "$tool_dir/yt-dlp.conf" ]]; then
    install -m 0644 "$script_dir/yt-dlp.conf" "$tool_dir/yt-dlp.conf"
    printf 'Installed the supplied yt-dlp.conf.\n'
else
    printf 'Kept the existing yt-dlp.conf unchanged.\n'
fi

chown -R "$jellyfin_owner" "$plugin_dir" "$tool_dir" "$trailer_dir"

"$tool_dir/yt-dlp" --ignore-config --version > "$tool_dir/yt-dlp.version"
"$tool_dir/deno" --version > "$tool_dir/deno.version"

printf '\nInstalled persistent Trailer Reel tools:\n'
printf '  Host yt-dlp: %s\n' "$tool_dir/yt-dlp"
printf '  Host Deno:   %s\n' "$tool_dir/deno"
printf '  Host config: %s\n' "$tool_dir/yt-dlp.conf"
printf '  Trailers:    %s\n' "$trailer_dir"
printf '  Ownership:   %s\n' "$jellyfin_owner"
printf '\nNo Jellyfin image was modified. Continue with INSTALL-UNRAID.md.\n'
