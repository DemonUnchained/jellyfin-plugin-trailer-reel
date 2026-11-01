Publication verified: [v0.4.1.2](https://github.com/DemonUnchained/jellyfin-plugin-trailer-reel/releases/tag/v0.4.1.2), October 31, 2026 (America/New_York). All four assets passed SHA-256 and archive checks; the published DLL passed 22 isolated Jellyfin 12.4 checks. Hosted release CI and catalog synchronization succeeded. Public catalog MD5, ABI, timestamp and URL match; historical entries and other plugins are unchanged. No server deployment or live playback test. Earlier development observations below retain their historical context.

# Build and validation

## Normal local command

Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Linux/WSL:

```bash
python3 tools/build-local.py
```

Use `-NoPackage` / `--no-package` to omit packaging. The backend locates `.codex-local/dotnet` first, otherwise the SDK on PATH; `global.json` controls SDK resolution. It installs only the pinned test dependency into `.codex-local/test-venv`, runs the existing console regressions, runs the offline real yt-dlp selectors, builds a separate Jellyfin 12.4.0 host by default, runs the binary checks, then packages.

The normal command does not download movie media or call TMDb/YouTube. Restore and first-time pip installation need network access. On Windows, symlink creation must be permitted (normally Developer Mode). The preflight provides an actionable error before the .NET tests run.

| Test group in the current development checkout | Checks | Coverage |
| --- | ---: | --- |
| .NET console | 107 | Genre matching, pool isolation, anime classification, coverage and replacement, discovery, migration/persistence, filenames, relative aliases/naming, selection and source rejection/fallback; corrupt/locked catalogs, recovery, cancellation and cleanup save failures |
| Existing yt-dlp Python suite | 22 | Production strict/relaxed selectors using yt-dlp 2026.08.19; standard/cropped Full HD, higher/lower resolutions, audio, unknown sizes; sockets blocked |
| Binary compatibility host (per version) | 22 | Actual plugin DLL with 12.3.0 or 12.4.0 runtime dependencies; type/JIT binding, construction/configuration, DI, resource page, aliases, intro item IDs, watched writes, duplicate lookups, per-user isolation, resume/disabled behavior, concurrent launches/duplicate lookups, corrupt-catalog suppression and ABI minimum |

`dotnet test` alone does not run these executable console checks. Test counts describe the current checkout, not values to fake after future changes.

## Compatibility host details

`tests/Jellyfin.Plugin.TrailerReel.Compatibility/` references the already-built plugin DLL and Jellyfin Controller/Model/Naming 12.4.0 with runtime assets by default (override JellyfinVersion for 12.3.0). Production's 12.2 references exclude host runtime assets as appropriate for a plugin. The separate host makes the actual assemblies used for compatibility explicit.

The host builds temporary synthetic files and in-memory implementations of Jellyfin services. It prepares all closed plugin methods to resolve API calls, constructs services, creates a three-item preroll queue, verifies watched-state persistence and filtering, and cleans its temporary folder. It does not start Jellyfin, decode a trailer, exercise HTTP streaming, or verify a specific client.

To test another published Jellyfin package version without changing production references:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoPackage -CompatibilityVersion 12.3.0
```

For a downloaded release DLL, override `PluginDllPath` and `PluginMetaPath` using the explicit MSBuild entry point, then execute its output DLL with the expected four-part host assembly version. For example, after setting those two PowerShell variables to absolute extracted file paths:

```powershell
dotnet msbuild tests/Jellyfin.Plugin.TrailerReel.Compatibility/Jellyfin.Plugin.TrailerReel.Compatibility.csproj -restore -target:Build -property:Configuration=Release "-property:PluginDllPath=$releaseDll" "-property:PluginMetaPath=$releaseMeta"
dotnet tests/Jellyfin.Plugin.TrailerReel.Compatibility/bin/Release/net10.0/Jellyfin.Plugin.TrailerReel.Compatibility.dll 12.4.0.0
```

The host copies the selected DLL explicitly and prints its SHA-256 so a same-version build cannot be mistaken for the intended release. The local build driver also compares the host's DLL bytes with the production build before executing tests. Verify the release ZIP SHA-256 before extracting it. `PROJECT_STATUS.json` contains the known 0.4.1.1 values. A DLL from an older/newer release must be paired with that release's own `meta.json`.

## Packaging

The native backend produces the same four asset names as the existing Bash flow:

- `trailer-reel_VERSION.zip`: exactly DLL, PDB and `meta.json` at ZIP root.
- `trailer-reel_VERSION-manual.zip`: one enclosing `TrailerReel_VERSION/` directory, with runtime files and installation tools/docs.
- `jellyfin-trailer-reel-source_VERSION.zip`: one source root, excluding SDKs, build output, Git internals and arbitrary untracked files.
- `SHA256SUMS-VERSION.txt`: SHA-256 checksums for the three ZIPs.

Source inputs are Git-tracked paths plus the declared handoff overlay files. Any other untracked paths cause packaging to fail with a clear message. Explicitly stage intended new source files, or use `--no-package`. No staging, commit, push or deployment occurs inside the build script. SDK/test dependencies live outside the source ZIP.

New source ZIP and PDB hashes can differ between local and hosted builds. Compare published assets to the checksums from that exact release, rather than expecting a rebuild to reproduce a historical ZIP byte for byte.

## Existing Linux/CI entry point

`bash ./build.sh` restores/builds and runs the 107 .NET checks, then packages. Its source ZIP excludes `.codex-local`, `.venv`, `__pycache__` and `.pyc` as well as Git/build output. `.github/workflows/build.yml` uses the SDK selected by `global.json`, then runs the 22 offline selector checks and the 22-check host for both 12.3.0 and 12.4.0 before artifact upload or tagged release publication. The host is built with explicit versioned dependencies and the existing production DLL; `cmp` verifies that its copied DLL matches the build. CI does not invoke the entire native packaging driver to perform the extra check. The existing main/PR/manual/tag triggers and four release assets are retained.

## Evidence and limits

Historical handoff evidence records a successful October 19, 2026 release/tag CI run: https://github.com/DemonUnchained/jellyfin-plugin-trailer-reel/actions/runs/39277216662, rechecked during handoff preparation on October 31. It covers the original 118 checks, not the updated workflow.

October 23 isolated investigation: source built cleanly against 12.3; the unchanged published 0.4.1.1 DLL passed the 96 regressions in a full 12.3 dependency runtime plus 17 additional contract checks; 22 offline format checks passed. No live-server playback test was performed. These are historical observations from this conversation; they are not proof that a new local build passed today.

Current local evidence, October 31, 2026 (America/New_York): the full PowerShell command passed on Windows using the local .NET SDK 10.0.401. It built against production 12.2.0 with zero warnings/errors, passed all 107 .NET regressions, 22 offline yt-dlp 2026.08.19 checks and 22 binary checks per host against 12.3.0.0 and 12.4.0.0, and prepared 817 plugin methods. Relative symlink creation/indexing passed. The host's DLL matched the production build byte for byte. Local packages retained the three root-level runtime files, manual wrapper, source root and checksum asset; CRCs, SHA-256 values and SDK/cache/build-output exclusions were independently verified.

The unchanged handoff DLL also passed the original 17 checks against the published [Jellyfin 12.4.0 packages](https://www.nuget.org/packages/Jellyfin.Controller/12.4.0). The local fixes remain compiled against 12.2.0, retaining the minimum ABI. Production version metadata remains 0.4.1.1; the modified DLL and local ZIPs are unreleased development artifacts, not replacements for the published release.

The review reproduced three defects against the original source in an isolated ignored copy: refresh removed expired managed files before a failing catalog save; failed catalog reads were cached as empty; and concurrent launches could reserve overlapping watched stock. New regression fixtures fail against those original implementations and pass with the fixes. No live services or media were used. The read-failure path now aborts refresh while playback skips prerolls; malformed/null catalogs are preserved for repair, and cancellation propagates.

`bash -n` passed for `build.sh` and all four multiline workflow shell blocks. The exact new compatibility shell block also passed locally in Git Bash using the existing production DLL. This verifies the local commands and DLL comparison, not the hosted Ubuntu runner or GitHub release steps.

The first sandboxed run compiled successfully but failed a .NET temporary-file rename with `UnauthorizedAccessException`; the unchanged command passed outside that filesystem sandbox. This was not a symlink failure, and no indexing assertions were skipped or weakened. When using Codex on Windows, rerun with the appropriate process permission if the sandbox prevents a temporary-file operation; do not alter plugin persistence to accommodate the sandbox.

The imported kit refers to a separate `HANDOFF_VERIFICATION.json` that is not part of this checkout. Current local runs write ignored `.codex-local/last-build.json` with exit codes and package hashes; the reviewable summary lives in `PROJECT_STATUS.json`. The updated hosted workflow has not run remotely. Live Jellyfin activation, HTTP streaming, decoding and client playback on the user's Unraid server remain untested. No release or deployment was performed.

## Live validation when a deployment is requested

Confirm plugin activation/version in Jellyfin, existing settings/pools and writable tools/media paths. Run one refresh, inspect genre coverage and source-skip summaries, then start a fresh regular horror feature with Cinema Mode enabled. Check all queued trailers match its profile. Start an Anime Movies feature and verify pool isolation. Repeat with the same and another Jellyfin user to check watched isolation. Check a resume skips prerolls. Distinguish successful intro lookup from actual media playback.
