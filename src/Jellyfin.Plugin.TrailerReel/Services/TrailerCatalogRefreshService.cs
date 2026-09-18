using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class TrailerCatalogRefreshService
{
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
        var localMovies = _localGenreService.GetCurrentMovieInventory(folder);
        var localGenres = localMovies.Genres;
        if (localGenres.Count == 0)
        {
            throw new InvalidOperationException("No genres were found on movies in the current Jellyfin library.");
        }

        _logger.LogInformation(
            "Trailer Reel will exclude locally hosted movies using {TmdbIdCount} TMDb IDs and {FallbackCount} title/year fallbacks",
            localMovies.TmdbIdCount,
            localMovies.FallbackTitleYearCount);

        progress.Report(3);
        var tmdbGenres = await _tmdbClient.GetMovieGenresAsync(config, cancellationToken).ConfigureAwait(false);
        var localSet = GenreTools.CanonicalSet(localGenres);
        var mappedGenres = tmdbGenres
            .Where(pair => localSet.Contains(GenreTools.Canonicalize(pair.Value)))
            .ToDictionary(pair => pair.Key, pair => GenreTools.Canonicalize(pair.Value));
        if (mappedGenres.Count == 0)
        {
            throw new InvalidOperationException("None of the local Jellyfin genres could be mapped to TMDb movie genres.");
        }

        var mappedNames = mappedGenres.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmapped = localSet.Where(genre => !mappedNames.Contains(genre)).OrderBy(genre => genre).ToArray();
        if (unmapped.Length > 0)
        {
            _logger.LogWarning(
                "Trailer Reel found local genres that TMDb does not expose as movie genres: {Genres}",
                string.Join(", ", unmapped));
        }

        var catalog = await _catalogStore.LoadAsync(folder, cancellationToken).ConfigureAwait(false);
        var retained = catalog.Trailers
            .Where(entry => !localMovies.Contains(entry.TmdbMovieId, entry.MovieName, entry.Year))
            .Where(entry => entry.ReleaseDate >= start && entry.ReleaseDate <= end)
            .Where(entry => GenreTools.HasOverlap(entry.Genres, localGenres))
            .Where(entry => IsSafeManagedFile(folder, entry.FileName) && File.Exists(Path.Combine(folder, entry.FileName)))
            .Take(config.MaxTrailers)
            .ToList();

        if (config.DeleteExpiredManagedTrailers)
        {
            DeleteNoLongerRetained(folder, catalog.Trailers, retained);
        }

        catalog = new TrailerCatalog
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            WindowStart = start,
            WindowEnd = end,
            LocalGenres = localGenres.ToList(),
            Trailers = retained,
        };
        await _catalogStore.SaveAsync(folder, catalog, cancellationToken).ConfigureAwait(false);
        progress.Report(7);

        var candidatesByGenre = new Dictionary<int, IReadOnlyList<MovieCandidate>>();
        var genreNumber = 0;
        foreach (var genre in mappedGenres)
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
            progress.Report(7 + (13d * genreNumber / mappedGenres.Count));
        }

        var existingIds = catalog.Trailers.Select(entry => entry.TmdbMovieId).ToHashSet();
        var candidateLimit = Math.Max(config.MaxTrailers * 5, config.MaxTrailers);
        var candidates = CandidateSelector.RoundRobin(candidatesByGenre, candidateLimit)
            .Where(candidate => !existingIds.Contains(candidate.Id))
            .Where(candidate => !localMovies.Contains(candidate.Id, candidate.Title, candidate.ReleaseDate.Year))
            .ToList();
        var attempted = 0;

        foreach (var candidate in candidates)
        {
            if (catalog.Trailers.Count >= config.MaxTrailers)
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
                    .Where(mappedGenres.ContainsKey)
                    .Select(id => mappedGenres[id])
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
                    "Trailer Reel could not acquire a 1080p trailer for {MovieName} (TMDb {TmdbId})",
                    candidate.Title,
                    candidate.Id);
            }

            progress.Report(20 + (70d * attempted / Math.Max(1, candidates.Count)));
        }

        await _hiddenLibrary.RefreshAsync(folder, catalog.Trailers, cancellationToken).ConfigureAwait(false);
        var indexedCount = catalog.Trailers.Count(entry =>
            _hiddenLibrary.FindItem(folder, entry.FileName) is not null);
        progress.Report(100);
        _logger.LogInformation(
            "Trailer Reel refresh complete with {Count} trailers for {GenreCount} local genres; Jellyfin indexed {IndexedCount}",
            catalog.Trailers.Count,
            localGenres.Count,
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
            throw new InvalidOperationException("Maximum trailers must be between 1 and 100.");
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
