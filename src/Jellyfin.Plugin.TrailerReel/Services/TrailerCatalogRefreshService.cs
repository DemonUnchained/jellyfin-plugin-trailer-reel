using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class TrailerCatalogRefreshService
{
    private const int DiscoveryCandidateMultiplier = 5;
    private const int TmdbPageSize = 20;
    private const int MaximumAnimeDiscoveryPages = 10;

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

        var previousCatalog = await _catalogStore.LoadAsync(folder, cancellationToken).ConfigureAwait(false);
        var retainedRegular = previousCatalog.Trailers
            .Where(entry => entry.Pool == TrailerPool.Regular)
            .Where(entry => !localMovies.Contains(entry.TmdbMovieId, entry.MovieName, entry.Year))
            .Where(entry => entry.ReleaseDate >= start && entry.ReleaseDate <= end)
            .Where(entry => GenreTools.HasOverlap(entry.Genres, regularGenres))
            .Where(entry => IsSafeManagedFile(folder, entry.FileName) && File.Exists(Path.Combine(folder, entry.FileName)))
            .Take(config.MaxTrailers)
            .ToList();
        var retainedAnime = config.EnableAnimeMovieTrailers
            ? previousCatalog.Trailers
                .Where(entry => entry.Pool == TrailerPool.Anime)
                .Where(entry => !localMovies.Contains(entry.TmdbMovieId, entry.MovieName, entry.Year))
                .Where(entry => entry.ReleaseDate >= start && entry.ReleaseDate <= end)
                .Where(entry => IsSafeManagedFile(folder, entry.FileName) && File.Exists(Path.Combine(folder, entry.FileName)))
                .Take(config.MaxAnimeTrailers)
                .ToList()
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

        if (mappedRegularGenres.Count > 0)
        {
            var candidatesByGenre = new Dictionary<int, IReadOnlyList<MovieCandidate>>();
            var genreNumber = 0;
            foreach (var genre in mappedRegularGenres)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidatesByGenre[genre.Key] = await _tmdbClient.DiscoverMoviesAsync(
                    genre.Key,
                    start,
                    end,
                    page: 1,
                    config,
                    cancellationToken).ConfigureAwait(false);
                genreNumber++;
                progress.Report(7 + (13d * genreNumber / mappedRegularGenres.Count));
            }

            var existingIds = catalog.Trailers.Select(entry => entry.TmdbMovieId).ToHashSet();
            var candidateLimit = Math.Max(config.MaxTrailers * DiscoveryCandidateMultiplier, config.MaxTrailers);
            var regularCandidates = CandidateSelector.RoundRobin(candidatesByGenre, candidateLimit)
                .Where(candidate => !AnimeMovieRules.IsAnimeCandidate(candidate))
                .Where(candidate => !existingIds.Contains(candidate.Id))
                .Where(candidate => !localMovies.Contains(candidate.Id, candidate.Title, candidate.ReleaseDate.Year))
                .ToList();
            await AddTrailersAsync(
                catalog,
                regularCandidates,
                TrailerPool.Regular,
                config.MaxTrailers,
                folder,
                tmdbGenres,
                config,
                progress,
                progressStart: 20,
                progressEnd: 72,
                cancellationToken).ConfigureAwait(false);
        }

        if (animePoolActive)
        {
            var animeCandidateLimit = Math.Max(
                config.MaxAnimeTrailers * DiscoveryCandidateMultiplier,
                config.MaxAnimeTrailers);
            var pageCount = Math.Min(
                MaximumAnimeDiscoveryPages,
                Math.Max(1, (int)Math.Ceiling(animeCandidateLimit / (double)TmdbPageSize)));
            var discoveredAnime = new List<MovieCandidate>();
            for (var page = 1; page <= pageCount && discoveredAnime.Count < animeCandidateLimit; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageCandidates = await _tmdbClient.DiscoverAnimeMoviesAsync(
                    start,
                    end,
                    page,
                    config,
                    cancellationToken).ConfigureAwait(false);
                discoveredAnime.AddRange(pageCandidates);
                progress.Report(72 + (8d * page / pageCount));
                if (pageCandidates.Count < TmdbPageSize)
                {
                    break;
                }
            }

            var existingIds = catalog.Trailers.Select(entry => entry.TmdbMovieId).ToHashSet();
            var animeCandidates = discoveredAnime
                .Where(AnimeMovieRules.IsAnimeCandidate)
                .GroupBy(candidate => candidate.Id)
                .Select(group => group.OrderByDescending(candidate => candidate.Popularity).First())
                .Where(candidate => !existingIds.Contains(candidate.Id))
                .Where(candidate => !localMovies.Contains(candidate.Id, candidate.Title, candidate.ReleaseDate.Year))
                .OrderByDescending(candidate => candidate.Popularity)
                .Take(animeCandidateLimit)
                .ToList();
            await AddTrailersAsync(
                catalog,
                animeCandidates,
                TrailerPool.Anime,
                config.MaxAnimeTrailers,
                folder,
                tmdbGenres,
                config,
                progress,
                progressStart: 80,
                progressEnd: 96,
                cancellationToken).ConfigureAwait(false);
        }

        await _hiddenLibrary.RefreshAsync(folder, catalog.Trailers, cancellationToken).ConfigureAwait(false);
        var indexedCount = catalog.Trailers.Count(entry =>
            _hiddenLibrary.FindItem(folder, entry.FileName) is not null);
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

        if (config.TrailersBeforeMovie is < 1 or > 10)
        {
            throw new InvalidOperationException("Trailers before a movie must be between 1 and 10.");
        }

        if (config.MonthsBack is < 0 or > 24 || config.MonthsAhead is < 0 or > 24)
        {
            throw new InvalidOperationException("The date-window month values must be between 0 and 24.");
        }
    }

    private async Task AddTrailersAsync(
        TrailerCatalog catalog,
        IReadOnlyList<MovieCandidate> candidates,
        TrailerPool pool,
        int maximum,
        string folder,
        IReadOnlyDictionary<int, string> tmdbGenres,
        PluginConfiguration config,
        IProgress<double> progress,
        double progressStart,
        double progressEnd,
        CancellationToken cancellationToken)
    {
        var attempted = 0;
        foreach (var candidate in candidates)
        {
            if (catalog.Trailers.Count(entry => entry.Pool == pool) >= maximum)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                var trailer = await _tmdbClient.GetBestTrailerAsync(candidate.Id, config, cancellationToken)
                    .ConfigureAwait(false);
                if (trailer is null)
                {
                    continue;
                }

                var names = candidate.GenreIds
                    .Where(tmdbGenres.ContainsKey)
                    .Select(id => GenreTools.Canonicalize(tmdbGenres[id]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (names.Count == 0)
                {
                    continue;
                }

                var fileName = await _downloader.DownloadAsync(
                    candidate.Id,
                    candidate.Title,
                    candidate.ReleaseDate.Year,
                    trailer.Key,
                    folder,
                    config,
                    catalog.Trailers.Select(entry => entry.FileName).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                catalog.Trailers.Add(new TrailerEntry
                {
                    Pool = pool,
                    TmdbMovieId = candidate.Id,
                    MovieName = candidate.Title,
                    Year = candidate.ReleaseDate.Year,
                    ReleaseDate = candidate.ReleaseDate,
                    Genres = names,
                    YoutubeKey = trailer.Key,
                    FileName = fileName,
                    DownloadedUtc = DateTimeOffset.UtcNow,
                });
                catalog.UpdatedUtc = DateTimeOffset.UtcNow;
                await _catalogStore.SaveAsync(folder, catalog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Trailer Reel could not acquire a trailer for {Pool} candidate {MovieName} (TMDb {TmdbId})",
                    pool,
                    candidate.Title,
                    candidate.Id);
            }
            finally
            {
                progress.Report(progressStart + ((progressEnd - progressStart) * attempted / Math.Max(1, candidates.Count)));
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
            if (!IsSafeManagedFile(folder, entry.FileName))
            {
                continue;
            }

            try
            {
                var path = Path.Combine(folder, entry.FileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("Trailer Reel removed no-longer-eligible managed trailer {FileName}", entry.FileName);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Trailer Reel could not remove expired managed trailer {FileName}", entry.FileName);
            }
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
