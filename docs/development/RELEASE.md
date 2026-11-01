Publication verified: [v0.4.1.2](https://github.com/DemonUnchained/jellyfin-plugin-trailer-reel/releases/tag/v0.4.1.2), October 31, 2026 (America/New_York). All four assets passed SHA-256 and archive checks; the published DLL passed 22 isolated Jellyfin 12.4 checks. Hosted release CI and catalog synchronization succeeded. Public catalog MD5, ABI, timestamp and URL match; historical entries and other plugins are unchanged. No server deployment or live playback test. Earlier development observations below retain their historical context.

Release preparation: v0.4.1.2 contains the persistence/concurrency fixes and 12.4 compatibility checks. The metadata below retains the original handoff baseline for historical context. Publication is authorized; server deployment is not requested.

# Releases and the shared Jellyfin catalog

Current published baseline: `v0.4.1.1`. The handoff and current 12.4/persistence/concurrency maintenance work remain unreleased; they do not request a release or server deployment. Local ZIPs still use the baseline version and must not overwrite the published assets. Prepare a new version when a release is requested.

## Prepare a real release

Keep the plugin GUID and compatible saved settings. Update the four-part version in the production `.csproj`, `meta.json`, version assertion in the existing .NET `Program.cs`, README/install examples and release notes together. Keep `assemblies` pointed at the actual DLL and `targetAbi` at the true minimum supported server version. A separate host-version test does not require raising the minimum ABI.

Run the relevant regressions and binary compatibility checks. Inspect the resulting catalog ZIP: only `Jellyfin.Plugin.TrailerReel.dll`, `.pdb`, and `meta.json` belong at its root. The manual ZIP has one wrapping directory; do not feed it to Jellyfin's catalog. Check version consistency, archive CRCs and SHA-256 values. Keep tools, SDKs, virtual environments, tokens, cookies, private configuration and user media out of runtime/source assets.

Review final code and docs on a development branch and complete required CI. For an explicitly authorized publish request, carry the requested release through the existing repository workflow without repeatedly asking for the same authorization. Preserve user work and repository protections.

## Existing publication workflow

`.github/workflows/build.yml` runs on PRs, main pushes, manual dispatch and `v*` tags. It uses `global.json`, the existing plugin build, offline selectors and separate Jellyfin 12.3.0 and 12.4.0 binary host runs. Both host runs must pass against the same DLL bytes before artifact upload or release publication. A tag must be exactly `v` plus the project version. Its tag job builds/tests/packages and uploads:

1. `trailer-reel_VERSION.zip`
2. `trailer-reel_VERSION-manual.zip`
3. `jellyfin-trailer-reel-source_VERSION.zip`
4. `SHA256SUMS-VERSION.txt`

Create the tag only at the reviewed/validated final commit. Publishing via a local `gh`/Git command or an available authenticated GitHub integration is acceptable within the user's authorization. Check the remote tag SHA, release, completed action run and all uploaded asset bytes/checksums. Do not present an empty release or an unfinished asset upload as complete. Do not replace an immutable old release to ship a new behavior change.

## Synchronize the shared catalog

Repository: `DemonUnchained/jellyfin-plugin-repository`.

`scripts/build_manifest.py` obtains public stable releases, finds the configured install ZIP, computes its **MD5** (the Jellyfin catalog protocol field), and regenerates `manifest.json`. `plugin-sources.json` owns repository names, asset templates, default minimum ABIs and version-specific overrides. It is safer to use that generator/workflow than manually invent checksums or timestamps.

`.github/workflows/sync-manifest.yml` supports manual dispatch, runs hourly at minute 17, and runs on main changes to its source configuration/generator/workflow. A plugin release alone does not immediately dispatch the separate repository. For an authorized publish, trigger the sync and verify the raw public manifest after the release assets are ready. With an authenticated GitHub CLI:

```bash
gh workflow run sync-manifest.yml --repo DemonUnchained/jellyfin-plugin-repository --ref main
```

Inspect that run's completion and the final raw manifest. New entry version, install asset URL, MD5, timestamp and minimum ABI must match the actual release. Preserve historical versions and the separate Theme Music Finder entry.

At this handoff Trailer Reel's default ABI is `12.2.0.0`. Overrides keep `0.3.0.2`, `0.3.0.1`, `0.3.0.0` and `0.2.1.4` on `12.1.0.0`. Versions `0.4.0.0`, `0.4.1.0` and `0.4.1.1` belong to `12.2.0.0`. If raising the default later, add overrides for every older version whose minimum differs, or add only a specific override for the new version. Never relabel old binaries with a new minimum by accident. Leave Theme Music Finder's metadata untouched unless a separate task requests it.

## Server rollout

Follow the installation guide for a requested deployment: preserve settings/catalog/tools/media, keep old DLL directories outside active plugins, ensure correct numeric UID/GID ownership, restart and confirm activation. Existing `RequireExact1080p`, GUID, pool identities and per-user watched history must survive. Container networking/Compose and external tools should change only when the specific deployment requires it. A server rollout is separate from publishing a GitHub/catalog release.
