# Trailer Reel development

Work on the existing C# Jellyfin plugin. Read `docs/development/CODEX_HANDOFF.md`, `VALIDATION.md`, and `RELEASE.md` for context, checks, and publication. Inspect current Git state and source before relying on dated handoff metadata. Preserve unrelated local work and keep changes scoped to the user's request.

## Preserve behavior

- Keep plugin GUID `c124b60d-fb89-47bb-9c93-fc2649152a75`, saved settings and catalog compatibility. Retain the `RequireExact1080p` key even though its label now describes cropped Full HD.
- Defaults: 100 regular / 30 anime trailers, US/en-US, two months back / six months forward, up to three prerolls. Anime Movies uses only its separate pool. Exclude movies already hosted locally.
- Every queued trailer must match the feature's first/anchor genre and whole genre profile. Preserve distinctive-tone exclusions, similarity ordering, per-user watched filtering, and resume suppression. Return fewer trailers if necessary; do not fill a short queue with off-theme or watched stock.
- Preserve genre coverage reserves and existing caps. Download replacements, save the catalog successfully, then delete old managed files. Failed acquisition/save must preserve old stock.
- Strict Full HD requires 1080-high or 1920-wide video within 1920x1080, with audio. Keep cropped 1920x960/800 valid, and reject 720p/1440p/4K. Expected age/format rejection can try up to three eligible uploads within pool budgets. Other failures and cancellation must propagate appropriately.
- Keep indexed item IDs and relative index aliases. Source filenames retain `-trailer`; aliases avoid Jellyfin classifying them as extras. Never replace IDs with arbitrary filesystem-only intros.

## Engineering and verification

- Target `net10.0`; use the SDK selected by `global.json`. Production Jellyfin references/ABI are 12.2.0/12.2.0.0 at handoff. The separate compatibility host validates 12.3.0 and 12.4.0 without changing the production minimum; local commands default to 12.4.0.
- Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` on Windows or `python3 tools/build-local.py` on Linux. `-NoPackage` / `--no-package` omits ZIP creation. These commands execute the console tests; `dotnet test` alone is insufficient.
- For runtime changes, add a focused regression where it protects actual behavior. Use synthetic fixtures and temporary storage for tests. Distinguish source compilation, binary contract checks, and actual server/client playback.
- Windows needs symbolic-link capability for indexing tests. If blocked, explain Developer Mode or use an existing WSL environment; don't skip indexing assertions.
- Keep README and relevant development status current with the final behavior, versions, commands, results, and limitations. Avoid making a release solely for a documentation handoff.
- Keep TMDb tokens, cookies, private server configuration, media, local SDKs and caches out of Git and source ZIPs. Cookies remain optional yt-dlp config owned by the user; do not collect or log them.

## Workflow

Proceed autonomously with authorized edits, builds, diagnostics and review preparation. Don't repeatedly seek confirmation for routine work already requested. Respect the scope of each task: this handoff prepares local development; a future publish request includes verifying release assets and synchronizing the companion catalog. Preserve other catalog plugins and older target ABI mappings. Never claim a server rollout or playback test that was not performed.
