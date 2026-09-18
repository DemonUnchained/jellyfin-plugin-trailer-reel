# Trailer Reel installation on Rocinante

This release includes setup scripts for the external tools required by Trailer Reel. It does not hide executable downloads inside the Jellyfin plugin DLL. The installer obtains the official Linux releases of yt-dlp and Deno, verifies their published SHA-256 checksums, and installs them into Jellyfin's persistent configuration volume.

## Files and paths

| Purpose | Unraid host path | Jellyfin container path |
| --- | --- | --- |
| Plugin | `/mnt/user/appdata/jellyfin/config/plugins/TrailerReel_0.3.0.0` | `/config/plugins/TrailerReel_0.3.0.0` |
| yt-dlp | `/mnt/user/appdata/jellyfin/config/trailer-tools/yt-dlp` | `/config/trailer-tools/yt-dlp` |
| Deno | `/mnt/user/appdata/jellyfin/config/trailer-tools/deno` | `/config/trailer-tools/deno` |
| yt-dlp config | `/mnt/user/appdata/jellyfin/config/trailer-tools/yt-dlp.conf` | `/config/trailer-tools/yt-dlp.conf` |
| Trailer media | `/mnt/user/appdata/jellyfin/trailer-reel/trailers` | `/trailers` |

## 1. Upgrade Jellyfin safely

Trailer Reel 0.3.0.0 is for Jellyfin 12.1.

When upgrading from Jellyfin 12.0:

1. Stop Jellyfin and take a manual backup of its persistent `/config` data.
2. Move `TrailerReel_0.2.0.0` out of `/mnt/user/appdata/jellyfin/config/plugins`. Do not delete Trailer Reel's configuration XML, `/config/trailer-tools`, or `/trailers`.
3. Change only the Jellyfin image tag to `jellyfin/jellyfin:12.1`, preserving all existing paths, devices, groups, networking, and environment settings.
4. Extract Trailer Reel 0.3.0.0 as described below, recreate only the Jellyfin service, and let startup finish without interruption.
5. Confirm the Dashboard reports Jellyfin 12.1 and Trailer Reel 0.3.0.0 before running the controlled plugin test.

Jellyfin 12.1 is a bug-fix release and does not carry Jellyfin 12.0's mandatory post-upgrade full-scan instruction. If Rocinante is still on Jellyfin 10.11, first follow Jellyfin's 12.0 major-upgrade requirements, including removing all third-party plugins and completing the required full library scan; versions older than 10.10.7 must reach 10.10.7 before upgrading to Jellyfin 12.

### Upgrade the working 0.2.1.4 plugin

When Jellyfin 12.1 and Trailer Reel 0.2.1.4 are already working:

1. Stop the `jellyfin` container.
2. Move `TrailerReel_0.2.1.4` out of `/mnt/user/appdata/jellyfin/config/plugins` as a temporary rollback copy.
3. Extract `TrailerReel_0.3.0.0` into that `plugins` directory and correct its ownership as shown below.
4. Start Jellyfin and confirm the Dashboard reports Trailer Reel 0.3.0.0.
5. Keep `/config/trailer-tools`, `/trailers`, the Trailer Reel configuration XML, and the existing catalog. Version 0.3.0.0 migrates prior catalog entries into the regular pool and preserves per-user watched state.

No Docker Compose change, Jellyfin image update, or yt-dlp/Deno reinstall is required for this plugin-only upgrade.

## 2. Extract the plugin release

Extract the release ZIP so the host contains:

```text
/mnt/user/appdata/jellyfin/config/plugins/TrailerReel_0.3.0.0/Jellyfin.Plugin.TrailerReel.dll
```

Do not create a second nested `TrailerReel_0.3.0.0/TrailerReel_0.3.0.0` directory. Keep older Trailer Reel assemblies outside the active `plugins` directory. The unchanged plugin GUID allows the Jellyfin 12.1 build to reuse the existing Trailer Reel settings and per-user watched history.

Rocinante runs Jellyfin as UID/GID `99:100`. A ZIP extracted by `root` remains root-owned, but Jellyfin 12.1 must update the plugin's `meta.json` during activation. Correct the ownership before startup:

```bash
chown -R 99:100 /mnt/user/appdata/jellyfin/config/plugins/TrailerReel_0.3.0.0
```

## 3. Install yt-dlp and Deno

Run this from the Unraid terminal after extraction:

```bash
bash /mnt/user/appdata/jellyfin/config/plugins/TrailerReel_0.3.0.0/tools/install-unraid-tools.sh
```

The script downloads only from the official `yt-dlp/yt-dlp` and `denoland/deno` GitHub release repositories. It resolves one release tag for each tool and uses that same tag for the executable and checksum, preventing a latest-release rollover from mixing versions during installation. Re-running the script updates both executables; it preserves an existing `yt-dlp.conf`.

No executable is installed into the Jellyfin container's writable layer. The tools survive container recreation because the existing host `/mnt/user/appdata/jellyfin/config` mapping appears inside the container as `/config`.

## 4. Add the trailer-media mapping

Add this line to the existing Jellyfin service's `volumes:` list:

```yaml
- /mnt/user/appdata/jellyfin/trailer-reel/trailers:/trailers
```

Preserve every existing Jellyfin setting, including its `/config`, `/cache`, and read-only `/media` mappings, Intel render device, and `group_add: "18"`. Do not add `:ro` to `/trailers`; the plugin must create and remove its managed trailer files.

There is no new companion container and no change to Jellyfin's network or VPN routing. If Jellyfin already runs the official `12.1` image, do not change the image for this plugin update.

## 5. Restart and validate

After extracting the Jellyfin 12.1 plugin build and confirming the `/trailers` mapping, restart Jellyfin. Then run:

```bash
bash /mnt/user/appdata/jellyfin/config/plugins/TrailerReel_0.3.0.0/tools/verify-container-tools.sh jellyfin
```

The validation checks that yt-dlp, Deno, Jellyfin FFmpeg, the yt-dlp configuration, Internet extraction, and write access to `/trailers` are all available from inside Jellyfin. It does not download the test video. Its optional second argument overrides the test URL if the default video later becomes unavailable. Existing yt-dlp and Deno files under `/config/trailer-tools` survive the Jellyfin image replacement and do not need to be reinstalled when these checks pass.

## 6. Configure Trailer Reel

Open **Jellyfin Dashboard → Plugins → Trailer Reel** and set:

| Setting | Value |
| --- | --- |
| Trailer folder | `/trailers` |
| yt-dlp executable | `/config/trailer-tools/yt-dlp` |
| yt-dlp configuration | `/config/trailer-tools/yt-dlp.conf` |
| TMDb read token | Your TMDb API read access token |
| Months before today | `2` |
| Months after today | `6` |
| Maximum regular-movie trailers | `100` |
| Enable separate anime-movie pool | Enabled |
| Anime movie library name | `Anime Movies` |
| Maximum anime-movie trailers | `30` |
| Trailers before movie | `3` |
| Exact 1080p | Enabled |
| Do not repeat trailers for the same Jellyfin user | Enabled |

For the first anime test, set **Maximum anime-movie trailer files** to `3` or `5`. Run **Dashboard → Scheduled Tasks → Download/refresh Trailer Reel**, then start a movie from the `Anime Movies` library. Those anime trailers must not appear before a movie from `Movies`. Raise the anime cap to `30` after confirming the isolation behavior.

Cinema mode/prerolls must be enabled on the playback client. On Wholphin/Jellyfin Android TV, verify **Settings → Playback → Prerolls → Cinema mode**.

Trailer Reel records the selected trailer as watched only for the Jellyfin profile that requested the preroll queue. It does not delete the shared trailer file or prevent other users from seeing it. The watched marker is committed when the queue is created, which prevents repeat selection even if a client later abandons playback.

## Updating yt-dlp or Deno

Re-run `install-unraid-tools.sh` from the Unraid host. This intentionally updates the external tools without rebuilding or replacing the Jellyfin plugin. Review any local `yt-dlp.conf` customizations separately.
