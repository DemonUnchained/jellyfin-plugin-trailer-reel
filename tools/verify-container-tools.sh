#!/usr/bin/env bash
set -euo pipefail

container_name="${1:-jellyfin}"
test_url="${2:-https://www.youtube.com/watch?v=YE7VzlLtp-4}"

if ! docker inspect "$container_name" >/dev/null 2>&1; then
    printf 'Container not found: %s\n' "$container_name" >&2
    exit 1
fi

if [[ "$(docker inspect -f '{{.State.Running}}' "$container_name")" != "true" ]]; then
    printf 'Container %s is stopped. Preserve the current stopped state and run this check after Jellyfin is intentionally started.\n' "$container_name" >&2
    exit 2
fi

docker exec "$container_name" test -x /config/trailer-tools/yt-dlp
docker exec "$container_name" test -x /config/trailer-tools/deno
docker exec "$container_name" test -r /config/trailer-tools/yt-dlp.conf
docker exec "$container_name" test -w /trailers

docker exec "$container_name" /config/trailer-tools/yt-dlp --ignore-config --version
docker exec "$container_name" /config/trailer-tools/deno --version
docker exec "$container_name" /usr/lib/jellyfin-ffmpeg/ffmpeg -version | sed -n '1p'

docker exec "$container_name" /config/trailer-tools/yt-dlp \
    --config-locations /config/trailer-tools/yt-dlp.conf \
    --simulate --no-playlist \
    "$test_url"

printf 'Trailer Reel tool and container preflight passed.\n'
