# Trailer Reel — project handoff

Snapshot: October 31, 2026. Read the current source and Git state when continuing.

## Ownership and source of truth

| Item | Value |
| --- | --- |
| Plugin repository | https://github.com/DemonUnchained/jellyfin-plugin-trailer-reel |
| Default branch | `main` |
| Verified main commit | `4ed6988bbd258a8640bd69c4009616b664a57de3` |
| Verified tree | `26165e876870390a4e01356656e469cfb0497bdb` |
| Latest published version | `0.4.1.1`, tag `v0.4.1.1`, published October 19, 2026 |
| Shared plugin catalog | https://github.com/DemonUnchained/jellyfin-plugin-repository |
| Jellyfin repository URL | https://raw.githubusercontent.com/DemonUnchained/jellyfin-plugin-repository/main/manifest.json |
| Plugin GUID | `c124b60d-fb89-47bb-9c93-fc2649152a75` |
| User's server | Jellyfin 12.3 in an Unraid container |
| Development machine | Windows PC; separate local Codex project |

`PROJECT_STATUS.json` records source/release identifiers and known checksums. These are a dated baseline, not an instruction to undo later work. The source already resides on GitHub; no media or production secrets are needed to take over development.

## Why this plugin exists

Maintain local recent/upcoming movie trailer stock and supply genre-appropriate prerolls through Jellyfin's native `IIntroProvider`. The user wants three suitable trailers before a movie when sufficient unwatched stock exists, with a separate Anime Movies pool. The user has explicitly rejected unrelated trailers merely sharing a loose secondary genre.

## Established requirements

| Area | Behavior to preserve |
| --- | --- |
| Pool caps | 100 regular movie files, separate 30 anime movie files by default |
| Anime routing | Feature library `Anime Movies` selects only anime stock; every other movie selects regular stock |
| Locale | US region / en-US language |
| Release window | Two months before today through six months after today |
| Queue | Up to three unique trailers; no forced filler |
| Genre | First listed Jellyfin genre is mandatory anchor; whole-profile similarity also required |
| Watched history | Per Jellyfin user; suppress previously selected trailers for that user |
| Hosted movie exclusion | Match local movie TMDb IDs; normalized title/year fallback where IDs are absent |
| Quality | Strict Full HD by default, including cropped widescreen; audio required |
| Files | `Movie Name-trailer.ext`; disambiguate title collisions with year then TMDb ID |
| Persistence | Reuse existing settings, pools, catalog and watched state through upgrades |
| Scheduling | Refresh task defaults to 04:00 server time daily |

### Genre matching and previous user reports

Watching Hellraiser revealed off-theme choices, including stop-motion/animated material and a second poorly matched trailer. Version 0.4.0.0 strengthened matching: candidates must match the anchor and sufficiently resemble the whole feature profile. Animation, Comedy, Family, Documentary and Music are distinctive shifts rejected when absent from the feature. A lone secondary anchor or mostly unrelated genres also fails. Similarity, shared genres, primary alignment and fewer unmatched genres determine ranking; randomness breaks equivalent ties.

Hellraiser 3 later produced no trailers. Strict matching plus a full but uneven pool can leave no usable horror stock. Version 0.4.1.0 added coverage validation and acquisition balancing; relaxing genre rules was not the chosen solution.

Genre aliases include Sci-Fi/Science-Fiction -> Science Fiction, Anime -> Animation, Kids -> Family, Musical -> Music and Suspense -> Thriller. Unknown custom genres are logged rather than fabricated as TMDb categories.

### Coverage balancing and transactional replacement

Build actual local anchor/profile sets separately for regular and anime libraries. Default reserve is six compatible trailers per profile, adjusted to fit pool caps and requested queue length. Existing stock can satisfy several profiles. Prioritize remaining deficits fairly across anchors, search deeper when necessary, and report unresolved shortages honestly.

At capacity a replacement must improve coverage while preserving every other profile's existing coverage up to its target. Acquire the new file first, save the changed catalog, then remove old managed media. Acquisition failure retains the old entry/file; save failure restores the old state and discards the new download. Disabling managed-file deletion prevents replacement at capacity. Do not reset watched history to manufacture stock.

`.trailer-reel-coverage.json` reports target, compatible, present and indexed counts. At playback distinguish absent anchor stock, off-theme rejections, missing files, unindexed items and watched exhaustion. A successful refresh does not guarantee every profile could be filled.

### yt-dlp fixes in 0.4.1.1

The supplied YouTube listing for `FwoYYKyJo1g` contained 1920x960 video plus audio. A literal `height=1080` filter incorrectly rejected it. Strict quality now admits video 1080 pixels high or 1920 pixels wide, within 1920x1080, with audio. This accepts 1920x960 and 1920x800 and excludes 720p, 1440p and 4K. Read the exact production selectors in `YtDlpDownloader.cs`; tests extract those literal constants.

`EfluEyQ5QIA` returned an age-verification error. Age/format rejections are expected source limitations, classified by `TrailerSourceRejectedException`; try up to three unique eligible uploads per movie in ranked order, sharing the existing per-pool acquisition budget. Unknown errors remain errors and cancellation propagates. The plugin does not bypass authentication. A user may supply optional cookies through their existing yt-dlp config.

Expected skips get per-pool summary counts and individual Debug details. Extraction/runtime hints remain visible in a separate warning. Do not restore blanket `--no-warnings`, log config/cookie contents, silently reduce quality, or treat every format failure as proof that 1080p is absent.

## Architecture map

| Source | Responsibility |
| --- | --- |
| `Plugin.cs`, `Configuration/` | Identity, saved configuration, dashboard page |
| `PluginServiceRegistrator.cs` | Dependency injection, intro provider and scheduled task registration |
| `Tasks/RefreshTrailersTask.cs` | Jellyfin refresh entry point and default schedule |
| `Services/TrailerCatalogRefreshService.cs` | Discovery/acquisition orchestration, stock maintenance and coverage reporting |
| `LocalGenreService.cs`, `LocalMovieInventory.cs`, `AnimeMovieRules.cs` | Local inventory, profiles, exclusions and anime classification |
| `TmdbClient.cs`, `CandidateSelector.cs` | Locale/date-constrained discovery and ranked eligible upload candidates |
| `YtDlpDownloader.cs`, `TrailerSourceAcquirer.cs`, `TrailerSourceRejectedException.cs` | External tool invocation, filenames, rejection classification and alternate uploads |
| `GenreMatchEvaluator.cs`, `GenreTools.cs` | Strict profile eligibility and scoring |
| `GenreCoveragePlanner.cs`, `TrailerPoolReplenisher.cs`, `GenreCoverageReport.cs` | Coverage deficits, safe replacement and reports |
| `CatalogStore.cs`, `Models/TrailerCatalog.cs` | Persistent catalog, caching and compatibility |
| `TrailerIndexStore.cs`, `HiddenTrailerLibrary.cs` | Relative aliases, hidden physical library and real item lookup |
| `GenreTrailerIntroProvider.cs`, `TrailerQueueSelector.cs`, `PlaybackStockDiagnostics.cs` | Preroll selection, per-user marking, cache and shortage explanations |

## Indexing and client behavior

Jellyfin treats names ending `-trailer` as extras. Preserve these source names but maintain relative symlink aliases under `TrailerReelIndex/` that index as standalone videos. Reconcile only managed aliases. Alias deletion must never delete the original file. Scan the hidden `Trailer Reel (Internal)` physical library after refresh, hide it from user My Media/Latest views, and return real indexed item IDs and paths in `IntroInfo`.

Jellyfin clients must request Cinema Mode/prerolls on a fresh movie start. Resumes are deliberately skipped. Selection is marked watched when the queue is created, even if the client later abandons playback. A 30-second per-user/movie cache keeps duplicate lookups from one launch stable. Another user's eligible queue is independent. These are established choices, not bugs to silently reverse.

## Toolchain and server paths

Production uses .NET 10, SDK `10.0.401` with latest-patch roll-forward, Controller/Model/Naming `12.2.0`, minimum ABI `12.2.0.0`. The isolated 12.3/12.4 compatibility checks do not require raising that minimum. Keep package references, assembly/meta versions, GUID and release tags aligned when a real release is made.

Runtime container paths are `/config/trailer-tools/yt-dlp`, `/config/trailer-tools/deno`, optional `/config/trailer-tools/yt-dlp.conf`, and writable `/trailers`. Tools persist through container recreation. The trailer media mapping belongs outside normal Jellyfin configuration backups. `INSTALL-UNRAID.md` and `tools/` describe installation and validation. Host paths in examples must remain placeholders; no production host settings are included in this kit.

## Initial development handoff integration

On October 31, 2026, the local checkout matched the baseline above and already contained the imported overlay and marked README addition. Those local changes were preserved on `codex/development-handoff`. The complete Windows build passed 96 .NET regressions, 22 offline selectors and 17 Jellyfin 12.3.0 binary checks (813 methods prepared), including relative symlinks. At completion of the initial handoff, the runtime source, version 0.4.1.1, production 12.2.0 references and ABI 12.2.0.0 were unchanged; subsequent maintenance is recorded below.

Hosted CI now builds the isolated host with explicit 12.3.0 dependencies using the existing production DLL and verifies its bytes before running it. The release triggers, asset names and catalog ZIP layout are retained. README and Unraid installation wording now distinguish the production minimum from separately tested 12.3 compatibility. The updated workflow needs a hosted run after review and push; no release, catalog synchronization, server deployment or live playback check was performed during this integration. See `VALIDATION.md` and `PROJECT_STATUS.json` for local evidence and remaining work.

Future priorities depend on the user's next request. The original genre/coverage and Full HD/age-fallback fixes are already implemented. Do not present them as pending features. Live Jellyfin/client playback and real source availability remain environment-specific checks; use current redacted diagnostics when investigating new failures.

## Current maintenance: Jellyfin 12.4

The unchanged handoff DLL passed the isolated 12.4.0 host. The subsequent source review fixes three reproduced issues: eligibility/cap cleanup now saves before deleting old media; catalog read errors preserve the existing catalog, propagate cancellation and retry after repair; and intro queue creation plus watched writes are serialized to prevent concurrent launches from reserving duplicate trailers. Playback returns no prerolls if its catalog cannot be read. Genre rules, caps, locale, window, upload budgets, settings and indexed IDs are preserved.

Current local checks: 107 production regressions with 12.2.0 runtime contracts, 22 offline selectors and 22 binary checks per host against 12.3.0 and 12.4.0 (817 methods prepared). The new tests reproduce the defects against the original implementations in an isolated ignored copy. Local commands default to 12.4.0; CI runs both host versions against the same production DLL. Production references/ABI remain 12.2.0/12.2.0.0. These fixes are unreleased; the version is still at the 0.4.1.1 baseline. No hosted CI, publication, server rollout or live playback validation was performed. See PROJECT_STATUS.json for the current working-tree changes and evidence.
