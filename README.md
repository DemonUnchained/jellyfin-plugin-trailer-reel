# Trailer Reel for Jellyfin

Trailer Reel is a Jellyfin 12.2 server plugin that maintains isolated local catalogs of recent and upcoming regular-movie and anime-movie trailers and inserts up to three closely matched trailers before a movie.

## What version 0.4.1 does

- Reads the genres already present on movies in the local Jellyfin database and separates movies belonging to the configured `Anime Movies` library.
- Uses the TMDb API with locale `en-US`, region `US`, and U.S. limited/theatrical/digital release types.
- Requires both the movie's primary release date and the returned U.S. release date to fall inside the configured window, preventing old theatrical re-releases from cycling through the catalog.
- Recalculates the discovery window on every refresh: two months before today through six months after today by default.
- Validates coverage against the actual ordered genre profiles of local movies using the same strict matcher as playback.
- Prioritizes compatible stock for underrepresented profiles and searches up to ten TMDb pages per genre within the current release window.
- Rebalances full pools by replacing surplus trailers only after acquiring a compatible replacement; scarce coverage in other profiles is protected.
- Discovers a dedicated anime pool using TMDb's Animation genre together with Japanese original-language and Japan-origin filters.
- Caps the regular pool at 100 files and the anime pool at 30 files by default.
- Returns anime trailers only for movies in the configured anime-movie library; ordinary movies can use only the regular pool.
- Excludes movies already hosted in Jellyfin, matching by TMDb ID and falling back to normalized title/year when a local movie has no TMDb ID.
- Chooses official YouTube trailers listed by TMDb.
- Calls a user-supplied `yt-dlp` executable and requires Full HD (1080p) quality by default, including widescreen 1920×960 and 1920×800 uploads.
- Names every downloaded file `Movie Name-trailer.ext` regardless of its pool.
- Uses `Movie Name (Year)-trailer.ext`, then `Movie Name [tmdb-ID]-trailer.ext`, only when duplicate movie names would collide.
- Creates a Jellyfin home-video library named `Trailer Reel (Internal)`, excludes it from users' My Media and Latest views, and indexes the downloaded files for playback.
- Preserves the required `Movie Name-trailer.ext` files while creating relative symlink aliases under `TrailerReelIndex/` whose names Jellyfin 12.2 will index. The aliases consume no duplicate video space and resolve back to the originals during playback.
- Implements Jellyfin's native `IIntroProvider` and returns up to three files from the correct pool that match the selected movie's anchor genre and overall genre profile.
- Uses Jellyfin's per-user watched state so a trailer committed to one user's preroll queue is not selected for that user again; other users can still receive it.
- Skips trailers when resuming a partially watched movie by default.
- Never deletes an arbitrary file: cleanup is limited to filenames recorded in `.trailer-reel-catalog.json` inside the configured trailer folder.

## Requirements

- Jellyfin Server **12.2.x**. This build targets Jellyfin ABI `12.2.0.0` and is compiled against the released Jellyfin 12.2.0 packages.
- A free TMDb API **read access token**.
- `yt-dlp` and Deno available inside the Jellyfin container. The release includes a checksum-verifying Unraid installer for both tools.
- `ffmpeg` available to `yt-dlp` inside the container. Most Jellyfin images already include Jellyfin FFmpeg, but its location may need to be set in the optional yt-dlp config.
- A persistent, writable trailer directory mounted into the Jellyfin container.
- Internet access from the Jellyfin container to TMDb and the selected trailer host.

The TMDb token is masked in the plugin page but Jellyfin stores plugin configuration on disk; protect the Jellyfin configuration volume as you would any other credential-bearing server data.

## Manual installation

1. Download the `trailer-reel_0.4.1.1-manual.zip` release asset and extract `TrailerReel_0.4.1.1` into Jellyfin's persistent `plugins` directory. The similarly named ZIP without `-manual` is the root-level package used by Jellyfin's plugin catalog and must not be extracted directly into the shared `plugins` directory.
2. Confirm this path exists inside the configuration volume:

   ```text
   plugins/TrailerReel_0.4.1.1/Jellyfin.Plugin.TrailerReel.dll
   ```

3. If Jellyfin runs as a numeric Docker user, make the extracted directory owned by that same UID/GID before startup:

   ```bash
   chown -R <jellyfin-uid>:<jellyfin-gid> /path/to/jellyfin/config/plugins/TrailerReel_0.4.1.1
   ```

4. Restart Jellyfin.
5. Open **Dashboard → Plugins → Trailer Reel**.

## Persistent storage layout

Trailer media belongs on the Applications pool, not under the read-only `/media` mount and not inside Jellyfin's `/config` directory. Use this dedicated mapping:

| Host path | Container path | Access |
| --- | --- | --- |
| `/path/to/jellyfin/trailer-reel/trailers` | `/trailers` | Read/write |

Place this directory on application storage while keeping the 10–30 GB trailer catalog out of normal Jellyfin configuration backups.

The existing host Jellyfin configuration directory mapped to `/config` holds the much smaller downloader tools. The included `tools/install-unraid-tools.sh` downloads the official yt-dlp and Deno Linux releases, verifies their published SHA-256 checksums, and installs them as:

```text
/config/trailer-tools/yt-dlp
/config/trailer-tools/deno
```

The installer also supplies `/config/trailer-tools/yt-dlp.conf`, which points yt-dlp to Deno and Jellyfin FFmpeg and enables the current EJS challenge components. This is also the right place for any later cookies, proxy, or PO-token-provider settings. Trailer Reel passes the file to yt-dlp with `--config-locations` but never parses or logs its contents.

Follow `INSTALL-UNRAID.md` in the release ZIP for the exact installation, Compose, validation, and controlled first-run procedure.

Test the downloader from an Unraid terminal before the first catalog refresh:

```bash
docker exec jellyfin /config/trailer-tools/yt-dlp --version
docker exec jellyfin /config/trailer-tools/deno --version
docker exec jellyfin /config/trailer-tools/yt-dlp -F 'https://www.youtube.com/watch?v=VIDEO_ID'
```

Replace `jellyfin` if the container has a different name.

## Configuration and first refresh

1. Enter the TMDb read access token.
2. Keep the trailer folder's **container path** at `/trailers`.
3. Confirm the yt-dlp executable path.
4. Keep these defaults for the requested behavior:

   - Months before today: `2`
   - Months after today: `6`
   - Maximum regular-movie trailer files: `100`
   - Separate anime-movie trailer pool: enabled
   - Anime movie library name: `Anime Movies`
   - Maximum anime-movie trailer files: `30`
   - Target compatible trailers per genre profile: `6` (adjusted to fit the pool cap)
   - Trailers before each movie: `3`
   - Region/language: fixed at `US` / `en-US`
   - Require Full HD (1080p) quality: enabled
   - Official trailers only: enabled

5. Save.
6. Run **Dashboard → Scheduled Tasks → Download/refresh Trailer Reel**.

The task saves the catalog after every successful download, so a cancellation or individual trailer failure does not discard completed work. After downloading, it scans Trailer Reel's hidden physical library folder so the new files are immediately available to Jellyfin's preroll queue. It runs again daily at 4:00 AM server time; Jellyfin allows the schedule to be changed from Scheduled Tasks.

## Enable cinema mode on clients

Jellyfin clients request `IIntroProvider` results only when Cinema Mode/prerolls are enabled.

- Jellyfin Web: **Settings → Playback → Cinema mode**. Jellyfin Web currently defaults this setting off.
- Jellyfin Android TV: **Settings → Playback → Prerolls → Cinema mode**. Current Android TV code defaults it on, but verify it for the user profile/device.

Only a fresh movie start requests trailers. Resuming is deliberately skipped, and Android TV also limits intro requests to movies starting at position zero.

## How matching works

Trailer Reel first asks Jellyfin which top-level library contains the selected movie. A movie in `Anime Movies` can use only the anime pool; a movie in every other library can use only the regular pool. It then uses the movie's first genre listed by Jellyfin as the queue's mandatory anchor genre. If Jellyfin lists `Horror`, `Thriller`, and `Fantasy`, every queued trailer must include `Horror`; a trailer matching only `Thriller` or `Fantasy` is rejected. `Sci-Fi`, `Sci Fi`, and `Science-Fiction` are normalized to TMDb's `Science Fiction`.

An anchor match is necessary but no longer sufficient. Version 0.4 rejects candidates whose genres are mostly outside the feature's profile, candidates that use the anchor only as a lone secondary overlap, and distinctive tone/style shifts such as `Animation`, `Comedy`, `Documentary`, `Family`, or `Music` when the feature does not have that genre. This prevents, for example, an animated or horror-comedy trailer from being placed before a straight live-action horror movie merely because both carry a `Horror` tag.

Eligible candidates are ranked by whole-profile similarity, number of shared genres, primary-genre alignment, and fewest unmatched genres; randomness is used only to break equal-quality ties. Up to three unique candidates are returned. A selected trailer is marked watched for the requesting Jellyfin user before it is returned, and future queues exclude watched trailer items for that user. A short 30-second cache keeps duplicate intro lookups from the same movie launch stable without creating a long repeat window.

This watched state is per Jellyfin profile. Trailer media is not deleted merely because one user saw it, so another profile can still receive it. Files are removed by managed eligibility/cap cleanup and by successful replacements that improve genre coverage. If a client fails after requesting its queue, those trailers still count as watched; this deliberate optimistic marking gives the strongest guarantee against repeats.

Common local labels are normalized (`Anime` → `Animation`, `Kids` → `Family`, `Musical` → `Music`, and `Suspense` → `Thriller`). A custom local genre that has no TMDb movie-genre equivalent is reported in the Jellyfin log and cannot be used as a discovery category.

If fewer than three indexed, unwatched profile-compatible trailers exist, the plugin returns only the matches it has. It does not fill the queue with weaker or off-theme matches. The playback log reports the anchor genre, all feature genres, the number rejected as off-theme, and the title, genres, and similarity score of every queued trailer.

## Genre coverage validation

A full catalog can still contain no usable trailers for a particular movie. A `Horror` tag on an animated or comedic trailer does not make it compatible with a straight horror feature. Refresh therefore builds a separate set of actual local movie profiles for each pool, preserving the first/anchor genre, and runs `GenreMatchEvaluator` for every stock/profile pairing.

The new **Target compatible trailers per genre profile** setting defaults to six. For each pool, the effective reserve is the smaller of `max(configured target, trailers per queue)` and `max(1, pool cap / distinct local anchor genres)`. This gives rare anchor genres a fair reserve without raising the 100 regular / 30 anime caps. Several profiles can share the same stock. The reserve is best effort; the configured date window, strict matching, local-movie exclusions, official sources, and Full HD availability still apply.

Each acquisition is ranked by the remaining deficits, with each anchor genre given equal weight across its local profiles. At capacity, a replacement must improve at least one profile's reserve and preserve every other profile's existing coverage up to its target. The replacement is downloaded first, then the changed catalog is saved, and only then is the old managed media removed. Failed acquisition preserves the old stock; failed catalog saving restores it and discards the new download. One replacement download can temporarily coexist with the old file while that transaction completes. Disabling managed-file deletion prevents replacements in a full pool.

The first search includes all mapped local genres. Later pages focus on deficient anchors, or continue filling an underfull pool, with limits of ten pages per genre and five acquisition attempts per available catalog slot. Pagination follows TMDb's `total_pages`, so a page emptied by release-date filtering does not incorrectly end the search. Anime shortage queries require both Animation and the target genre, plus the existing Japanese language/origin filters.

After indexing, refresh logs each anchor genre's minimum compatible, present and indexed counts across its local profiles. It also writes `.trailer-reel-coverage.json` in the configured trailer folder, containing the target and counts for every profile, including unresolved shortages. A completed refresh can still have a shortage; the report does not claim that unavailable trailers were acquired.

This report measures shared stock. Per-user watched filtering is applied at playback and is not reset by balancing. A short queue now gets an explicit log reason: missing anchor stock, rejected profiles, missing files, unindexed files, exhausted watched history, or insufficient remaining matches. If a user has watched every compatible trailer, the plugin continues to return fewer trailers; it never substitutes off-theme or already-watched trailers to satisfy the target.

After upgrading from 0.4.0.0, run **Download/refresh Trailer Reel** once to validate and rebalance the existing catalog. Subsequent daily refreshes repeat the validation.

## Operational notes

- Downloading up to 100 regular trailers plus 30 anime trailers can take a long time and may trigger source-site rate limits. Downloads are intentionally serial.
- Each movie can try up to three distinct eligible trailer uploads in ranked order. An upload that needs age verification or has no format matching the quality policy is skipped; the next eligible upload is tried before moving on to another movie. All source attempts also share the existing per-pool acquisition budget.
- Expected age/format skips are counted in one summary per pool rather than logged as exception stacks. Individual rejected upload IDs are available at Debug level. Format failures with extraction/runtime warnings produce one separate warning per pool; they do not prove that the source lacks 1080p.
- Full HD remains required when enabled: video must be 1080 pixels high or 1920 pixels wide, with both dimensions inside a 1920×1080 frame. This accepts widescreen Full HD uploads without black bars and excludes 720p, 1440p, and 4K. The existing `RequireExact1080p` configuration key is retained, so saved settings continue to work. Authentication is optional and is supplied only through the existing yt-dlp config; Trailer Reel does not collect browser cookies or alter account settings.
- The plugin does not download a second copy of a TMDb movie already present in its catalog.
- Each refresh drops trailers for movies now hosted locally from the active catalog. When managed-file cleanup is enabled, it also deletes those catalog-managed trailer files.
- `TrailerReelIndex/` contains only relative symlink aliases used to avoid Jellyfin's special handling of the `-trailer` suffix. Trailer Reel reconciles its own marked aliases on every refresh; deleting an alias does not delete the original video.
- The hidden library is implementation plumbing: Jellyfin requires real library item IDs to construct playable media sources for intros.
- This release has been compiled against the released Jellyfin 12.2.0 packages, and its pool-isolation, anime-classification, profile-matching, coverage/replacement, discovery-pagination, selection, indexing, and naming tests pass on .NET 10. Exercise the anime pool with a low cap before allowing the full 30-file run.

## Build

.NET SDK 10 is required only to build the source; it is not installed separately on the Jellyfin server. `global.json` records the validated SDK feature band for reproducible development builds.

```bash
bash ./build.sh
```

CI also runs `tests/Jellyfin.Plugin.TrailerReel.Tests/format_selection_test.py` against yt-dlp 2026.08.19. These offline checks exercise the production format selectors with standard and cropped Full HD, lower resolutions, higher resolutions, audio requirements, and unknown dimensions. Socket connections are blocked during the checks.

The script restores packages, builds Release, runs the lightweight test executable, and writes three ZIP assets plus a checksum file to `dist/`: a root-level runtime ZIP for Jellyfin's plugin catalog, a full `-manual.zip` bundle with one enclosing plugin directory, and a source ZIP.

For a documentation/tooling-only revision using an already validated Release build:

```bash
bash ./build.sh --package-only
```

## Source-service notice

This product uses the TMDB API but is not endorsed or certified by TMDB.

Downloading and retaining trailers may be restricted by copyright or the source platform's terms. Configure and operate the plugin only for sources and uses you are authorized to access.
