using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class TrailerCatalogRefreshService
{
    private const int DiscoveryCandidateMultiplier = 5;
    private const int MaximumDiscoveryPages = 10;

    private readonly LocalGenreService _localGenreService;
    private readonly TmdbClient _tmdbClient;
    private readonly YtDlpDownloader _downloader;
    private readonly CatalogStore _catalogStore;
    private readonly HiddenTrailerLibrary _hiddenLibrary;
    private readonly ILogger<TrailerCatalogRefreshService> _logger;

    public TrailerCatalogRefreshService(
        LocalGenreService localGenreService,
        TmdbClient tmdbClient,
        YtDlpDownloader downloader,
        CatalogStore catalogStore,
        HiddenTrailerLibrary hiddenLibrary,
        ILogger<TrailerCatalogRefreshService> logger)
    {
        _localGenreService = localGenreService;
        _tmdbClient = tmdbClient;
        _downloader = downloader;
        _catalogStore = catalogStore;
        _hiddenLibrary = hiddenLibrary;
        _logger = logger;
    }

    public async Task RefreshAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Trailer Reel configuration is unavailable.");
        ValidateConfiguration(config);

        var folder = Path.GetFullPath(config.TrailerFolderPath.Trim());
        Directory.CreateDirectory(folder);
        await _hiddenLibrary.EnsureAsync(folder, cancellationToken).ConfigureAwait(false);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-config.MonthsBack);
        var end = today.AddMonths(config.MonthsAhead);
        var movieContext = _localGenreService.GetCurrentMovieContext(folder, config.AnimeMovieLibraryName);
        var localMovies = movieContext.AllMovies;
        var regularGenres = movieContext.RegularGenres;
        var animePoolActive = config.EnableAnimeMovieTrailers && movieContext.AnimeLibraryFound;
        if (regularGenres.Count == 0 && !animePoolActive)
        {
            throw new InvalidOperationException("No genres were found on movies in the current Jellyfin library.");
        }

        if (config.EnableAnimeMovieTrailers && !movieContext.AnimeLibraryFound)
        {
            _logger.LogWarning(
                "Trailer Reel did not find a Jellyfin library named {LibraryName}; the anime trailer pool will not download until the configured library exists",
                config.AnimeMovieLibraryName);
        }

        _logger.LogInformation(
            "Trailer Reel will exclude locally hosted movies using {TmdbIdCount} TMDb IDs and {FallbackCount} title/year fallbacks; anime library {AnimeLibraryName} found: {AnimeLibraryFound}",
            localMovies.TmdbIdCount,
            localMovies.FallbackTitleYearCount,
            config.AnimeMovieLibraryName,
            movieContext.AnimeLibraryFound);

        progress.Report(3);
        var tmdbGenres = await _tmdbClient.GetMovieGenresAsync(config, cancellationToken).ConfigureAwait(false);
        var regularGenreSet = GenreTools.CanonicalSet(regularGenres);
        var mappedRegularGenres = tmdbGenres
            .Where(pair => regularGenreSet.Contains(GenreTools.Canonicalize(pair.Value)))
            .ToDictionary(pair => pair.Key, pair => GenreTools.Canonicalize(pair.Value));
        if (regularGenres.Count > 0 && mappedRegularGenres.Count == 0 && !animePoolActive)
        {
            throw new InvalidOperationException("None of the regular-movie Jellyfin genres could be mapped to TMDb movie genres.");
        }

        var mappedNames = mappedRegularGenres.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmapped = regularGenreSet.Where(genre => !mappedNames.Contains(genre)).OrderBy(genre => genre).ToArray();
        if (unmapped.Length > 0)
        {
            _logger.LogWarning(
                "Trailer Reel found local genres that TMDb does not expose as movie genres: {Genres}",
                string.Join(", ", unmapped));
        }

        var regularPlanner = new GenreCoveragePlanner(
            movieContext.RegularProfiles, config.MaxTrailers, config.MinimumGenreTrailers, config.TrailersBeforeMovie);
        var animePlanner = new GenreCoveragePlanner(
            movieContext.AnimeProfiles, config.MaxAnimeTrailers, config.MinimumGenreTrailers, config.TrailersBeforeMovie);
        var previousCatalog = await _catalogStore.LoadAsync(folder, cancellationToken).ConfigureAwait(false);
        var retainedRegular = regularPlanner.RetainWithinCap(previousCatalog.Trailers
            .Where(entry => entry.Pool == TrailerPool.Regular)
            .Where(entry => !localMovies.Contains(entry.TmdbMovieId, entry.MovieName, entry.Year))
            .Where(entry => entry.ReleaseDate >= start && entry.ReleaseDate <= end)
            .Where(entry => GenreTools.HasOverlap(entry.Genres, regularGenres))
            .Where(entry => IsSafeManagedFile(folder, entry.FileName) && File.Exists(Path.Combine(folder, entry.FileName)))).ToList();
        var retainedAnime = config.EnableAnimeMovieTrailers
            ? animePlanner.RetainWithinCap(previousCatalog.Trailers
                .Where(entry => entry.Pool == TrailerPool.Anime)
                .Where(entry => !localMovies.Contains(entry.TmdbMovieId, entry.MovieName, entry.Year))
                .Where(entry => entry.ReleaseDate >= start && entry.ReleaseDate <= end)
                .Where(entry => IsSafeManagedFile(folder, entry.FileName) && File.Exists(Path.Combine(folder, entry.FileName)))).ToList()
            : [];
        var retained = retainedRegular.Concat(retainedAnime).ToList();

        if (config.DeleteExpiredManagedTrailers)
        {
            DeleteNoLongerRetained(folder, previousCatalog.Trailers, retained);
        }

        var catalog = new TrailerCatalog
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            WindowStart = start,
            WindowEnd = end,
            LocalGenres = regularGenres.ToList(),
            AnimeLocalGenres = movieContext.AnimeGenres.ToList(),
            Trailers = retained,
        };
        await _catalogStore.SaveAsync(folder, catalog, cancellationToken).ConfigureAwait(false);
        progress.Report(7);

        await ReplenishPoolAsync(
            catalog, TrailerPool.Regular, regularPlanner, mappedRegularGenres,
            localMovies, folder, tmdbGenres, config, progress, 7, 72, cancellationToken).ConfigureAwait(false);

        if (animePoolActive)
        {
            var animeGenreSet = GenreTools.CanonicalSet(movieContext.AnimeGenres);
            var mappedAnimeGenres = tmdbGenres
                .Where(pair => animeGenreSet.Contains(GenreTools.Canonicalize(pair.Value)))
                .ToDictionary(pair => pair.Key, pair => GenreTools.Canonicalize(pair.Value));
            await ReplenishPoolAsync(
                catalog, TrailerPool.Anime, animePlanner, mappedAnimeGenres,
                localMovies, folder, tmdbGenres, config, progress, 72, 96, cancellationToken).ConfigureAwait(false);
        }

        await _hiddenLibrary.RefreshAsync(folder, catalog.Trailers, cancellationToken).ConfigureAwait(false);
        var indexedCount = catalog.Trailers.Count(entry =>
            _hiddenLibrary.FindItem(folder, entry.FileName) is not null);
        var coverage = new GenreCoverageReport(DateTimeOffset.UtcNow,
        [
            GenreCoverageReport.Assess(TrailerPool.Regular, regularPlanner, catalog.Trailers,
                entry => File.Exists(Path.Combine(folder, entry.FileName)),
                entry => _hiddenLibrary.FindItem(folder, entry.FileName) is not null),
            GenreCoverageReport.Assess(TrailerPool.Anime, animePlanner, catalog.Trailers,
                entry => File.Exists(Path.Combine(folder, entry.FileName)),
                entry => _hiddenLibrary.FindItem(folder, entry.FileName) is not null),
        ]);
        await coverage.SaveAsync(folder, cancellationToken).ConfigureAwait(false);
        LogCoverage(coverage, config);
        progress.Report(100);
        var regularCount = catalog.Trailers.Count(entry => entry.Pool == TrailerPool.Regular);
        var animeCount = catalog.Trailers.Count(entry => entry.Pool == TrailerPool.Anime);
        _logger.LogInformation(
            "Trailer Reel refresh complete with {RegularCount} regular trailers and {AnimeCount} anime trailers for {GenreCount} regular local genres; Jellyfin indexed {IndexedCount}",
            regularCount,
            animeCount,
            regularGenres.Count,
            indexedCount);
    }

    private static void ValidateConfiguration(PluginConfiguration config)
    {
        if (!config.Enabled)
        {
            throw new InvalidOperationException("Trailer Reel is disabled in plugin settings.");
        }

        if (string.IsNullOrWhiteSpace(config.TmdbReadAccessToken))
        {
            throw new InvalidOperationException("A TMDb API read access token is required.");
        }

        if (string.IsNullOrWhiteSpace(config.TrailerFolderPath))
        {
            throw new InvalidOperationException("A trailer folder path is required.");
        }

        if (config.MaxTrailers is < 1 or > 100)
        {
            throw new InvalidOperationException("Maximum regular-movie trailers must be between 1 and 100.");
        }

        if (config.EnableAnimeMovieTrailers && string.IsNullOrWhiteSpace(config.AnimeMovieLibraryName))
        {
            throw new InvalidOperationException("An anime movie library name is required when anime trailers are enabled.");
        }

        if (config.EnableAnimeMovieTrailers && (config.MaxAnimeTrailers is < 1 or > 30))
        {
            throw new InvalidOperationException("Maximum anime-movie trailers must be between 1 and 30.");
        }

        if (config.MinimumGenreTrailers is < 1 or > 30)
        {
            throw new InvalidOperationException("Minimum compatible trailers per genre must be between 1 and 30.");
        }

        if (config.TrailersBeforeMovie is < 1 or > 10)
        {
            throw new InvalidOperationException("Trailers before a movie must be between 1 and 10.");
        }

        if (config.MonthsBack is < 0 or > 24 || config.MonthsAhead is < 0 or > 24)
        {
            throw new InvalidOperationException("The date-window month values must be between 0 and 24.");
        }
    }

    private async Task ReplenishPoolAsync(
        TrailerCatalog catalog,
        TrailerPool pool,
        GenreCoveragePlanner planner,
        IReadOnlyDictionary<int, string> discoveryGenres,
        LocalMovieInventory localMovies,
        string folder,
        IReadOnlyDictionary<int, string> tmdbGenres,
        PluginConfiguration config,
        IProgress<double> progress,
        double progressStart,
        double progressEnd,
        CancellationToken cancellationToken)
    {
        var seen = catalog.Trailers.Select(entry => entry.TmdbMovieId).ToHashSet();
        var exhaustedGenres = new HashSet<int>();
        var attempts = 0;
        var uploadAttempts = 0;
        var formatSkips = 0;
        var ageRestrictedSkips = 0;
        var extractionWarnings = 0;
        var attemptLimit = planner.Maximum * DiscoveryCandidateMultiplier;
        var initial = catalog.Trailers.Where(entry => entry.Pool == pool).ToList();
        _logger.LogInformation(
            "Trailer Reel validating {Pool} coverage: {Stored}/{Maximum} stored, target {Target} compatible trailers per local profile; deficient anchors [{Anchors}]",
            pool, initial.Count, planner.Maximum, planner.Target,
            string.Join(", ", planner.DeficientAnchors(initial).OrderBy(name => name)));

        // Initial discovery includes every local genre. Later pages focus on remaining shortages.
        for (var page = 1; page <= MaximumDiscoveryPages && attempts < attemptLimit && uploadAttempts < attemptLimit; page++)
        {
            var current = catalog.Trailers.Where(entry => entry.Pool == pool).ToList();
            var deficientAnchors = planner.DeficientAnchors(current);
            var genresToSearch = discoveryGenres
                .Where(pair => !exhaustedGenres.Contains(pair.Key))
                .Where(pair => page == 1 || current.Count < planner.Maximum || deficientAnchors.Contains(pair.Value))
                .OrderByDescending(pair => deficientAnchors.Contains(pair.Value))
                .ThenBy(pair => pair.Key).ToList();
            if (genresToSearch.Count == 0 || (page > 1 && current.Count >= planner.Maximum && deficientAnchors.Count == 0))
            {
                break;
            }

            var candidates = new List<TrailerAcquisitionCandidate>();
            foreach (var genre in genresToSearch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var discovered = await _tmdbClient.DiscoverGenrePageAsync(
                    genre.Key, pool, catalog.WindowStart, catalog.WindowEnd, page, config, cancellationToken)
                    .ConfigureAwait(false);
                if (!discovered.HasMore)
                {
                    exhaustedGenres.Add(genre.Key);
                }

                foreach (var movie in discovered.Movies)
                {
                    if (!seen.Add(movie.Id)
                        || AnimeMovieRules.IsAnimeCandidate(movie) != (pool == TrailerPool.Anime)
                        || localMovies.Contains(movie.Id, movie.Title, movie.ReleaseDate.Year))
                    {
                        continue;
                    }

                    var entry = new TrailerEntry
                    {
                        Pool = pool,
                        TmdbMovieId = movie.Id,
                        MovieName = movie.Title,
                        Year = movie.ReleaseDate.Year,
                        ReleaseDate = movie.ReleaseDate,
                        Genres = movie.GenreIds.Where(tmdbGenres.ContainsKey)
                            .Select(id => GenreTools.Canonicalize(tmdbGenres[id]))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    };
                    candidates.Add(new TrailerAcquisitionCandidate(entry, movie.Popularity));
                }
            }

            await TrailerPoolReplenisher.ReplenishAsync(
                catalog, pool, planner, candidates, config.DeleteExpiredManagedTrailers,
                async (entry, token) =>
                {
                    if (attempts >= attemptLimit || uploadAttempts >= attemptLimit)
                    {
                        return false;
                    }

                    attempts++;
                    var trailers = await _tmdbClient.GetTrailerCandidatesAsync(entry.TmdbMovieId, config, token).ConfigureAwait(false);
                    var acquired = await TrailerSourceAcquirer.TryAcquireAsync(
                        trailers.Take(attemptLimit - uploadAttempts).ToArray(),
                        (video, sourceToken) =>
                        {
                            uploadAttempts++;
                            return _downloader.DownloadAsync(
                                entry.TmdbMovieId, entry.MovieName, entry.Year, video.Key, folder, config,
                                catalog.Trailers.Select(existing => existing.FileName).ToArray(), sourceToken);
                        },
                        (video, rejection) =>
                        {
                            if (rejection.Reason == TrailerSourceRejection.AgeRestricted)
                            {
                                ageRestrictedSkips++;
                            }
                            else
                            {
                                formatSkips++;
                            }

                            if (rejection.ExtractionWarning)
                            {
                                extractionWarnings++;
                            }

                            _logger.LogDebug(
                                "Trailer Reel skipped upload {YoutubeKey} for {MovieName}: {Reason}; trying another eligible trailer when available",
                                video.Key, entry.MovieName, rejection.Message);
                        }, token).ConfigureAwait(false);
                    if (acquired is null)
                    {
                        return false;
                    }

                    entry.YoutubeKey = acquired.Video.Key;
                    entry.FileName = acquired.FileName;
                    entry.DownloadedUtc = DateTimeOffset.UtcNow;
                    return true;
                },
                token => _catalogStore.SaveAsync(folder, catalog, token),
                entry => DeleteManagedFile(folder, entry),
                (entry, ex) => _logger.LogWarning(ex,
                    "Trailer Reel could not acquire a trailer for {Pool} candidate {MovieName} (TMDb {TmdbId})",
                    pool, entry.MovieName, entry.TmdbMovieId),
                (old, entry) => _logger.LogInformation(
                    "Trailer Reel rebalanced {Pool}: replaced surplus {OldMovie} with {MovieName} [{Genres}]",
                    pool, old.MovieName, entry.MovieName, string.Join(", ", entry.Genres)),
                cancellationToken).ConfigureAwait(false);
            progress.Report(progressStart + ((progressEnd - progressStart) * page / MaximumDiscoveryPages));
        }

        _logger.LogInformation(
            "Trailer Reel {Pool} discovery finished after {Attempts}/{Limit} movie acquisition attempts and {UploadAttempts}/{UploadLimit} upload attempts (up to {Pages} pages per genre)",
            pool, attempts, attemptLimit, uploadAttempts, attemptLimit, MaximumDiscoveryPages);
        _logger.LogInformation(
            "Trailer Reel {Pool} upload skips: {FormatSkips} requested-format unavailable, {AgeSkips} age-restricted; tried up to {SourceLimit} eligible uploads per movie without changing the quality policy",
            pool, formatSkips, ageRestrictedSkips, TrailerSourceAcquirer.MaximumUploadsPerMovie);
        if (extractionWarnings > 0)
        {
            _logger.LogWarning(
                "Trailer Reel {Pool}: {Count} format failures also reported YouTube extraction/runtime warnings. Check the yt-dlp version, configured Deno runtime and yt-dlp config path; missing formats may be an extraction problem",
                pool, extractionWarnings);
        }
        progress.Report(progressEnd);
    }

    private void LogCoverage(GenreCoverageReport report, PluginConfiguration config)
    {
        foreach (var pool in report.Pools)
        {
            foreach (var group in pool.Profiles.GroupBy(row => row.AnchorGenre))
            {
                _logger.LogInformation(
                    "Trailer Reel {Pool} coverage for {Genre}: target {Target}, minimum across {ProfileCount} local profile(s): {Compatible} compatible, {Present} present, {Indexed} indexed",
                    pool.Pool, group.Key, group.First().Target, group.Count(),
                    group.Min(row => row.Compatible), group.Min(row => row.Present), group.Min(row => row.Indexed));
            }

            var shortages = pool.Profiles.Count(row => row.Shortage);
            if (shortages > 0 && (pool.Pool == TrailerPool.Regular || config.EnableAnimeMovieTrailers))
            {
                _logger.LogWarning(
                    "Trailer Reel {Pool} coverage remains below target for {Shortages} local profile(s); see {Report}. Discovery respects the pool cap, release window, local exclusions, Full HD quality policy and strict matching. Managed-file deletion enabled: {DeletionEnabled}",
                    pool.Pool, shortages, GenreCoverageReport.FileName, config.DeleteExpiredManagedTrailers);
            }
        }
    }

    private void DeleteNoLongerRetained(
        string folder,
        IEnumerable<TrailerEntry> previous,
        IReadOnlyCollection<TrailerEntry> retained)
    {
        var retainedIds = retained.Select(entry => entry.TmdbMovieId).ToHashSet();
        foreach (var entry in previous.Where(entry => !retainedIds.Contains(entry.TmdbMovieId)))
        {
            DeleteManagedFile(folder, entry);
        }
    }

    private void DeleteManagedFile(string folder, TrailerEntry entry)
    {
        if (!IsSafeManagedFile(folder, entry.FileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(folder, entry.FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Trailer Reel removed managed trailer {FileName}", entry.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Trailer Reel could not remove managed trailer {FileName}", entry.FileName);
        }
    }

    private static bool IsSafeManagedFile(string folder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(folder, fileName));
        return path.StartsWith(root, StringComparison.Ordinal);
    }
}
