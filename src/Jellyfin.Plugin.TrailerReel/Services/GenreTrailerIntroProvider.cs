using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TrailerReel.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class GenreTrailerIntroProvider : IIntroProvider
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly CatalogStore _catalogStore;
    private readonly HiddenTrailerLibrary _hiddenLibrary;
    private readonly LocalGenreService _localGenreService;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<GenreTrailerIntroProvider> _logger;
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), CachedSelection> _selectionCache = new();

    public GenreTrailerIntroProvider(
        CatalogStore catalogStore,
        HiddenTrailerLibrary hiddenLibrary,
        LocalGenreService localGenreService,
        IUserDataManager userDataManager,
        ILogger<GenreTrailerIntroProvider> logger)
    {
        _catalogStore = catalogStore;
        _hiddenLibrary = hiddenLibrary;
        _localGenreService = localGenreService;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    public string Name => "Trailer Reel";

    public async Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null
            || !config.Enabled
            || item is not Movie
            || string.IsNullOrWhiteSpace(config.TrailerFolderPath)
            || config.TrailersBeforeMovie <= 0)
        {
            return [];
        }

        if (config.SkipWhenResuming)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData is not null && userData.PlaybackPositionTicks > 0 && !userData.Played)
            {
                _logger.LogInformation(
                    "Trailer Reel skipped prerolls before {MovieName} because the movie is being resumed",
                    item.Name);
                return [];
            }
        }

        var key = (user.Id, item.Id);
        var now = DateTimeOffset.UtcNow;
        if (_selectionCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Intros;
        }

        var folder = Path.GetFullPath(config.TrailerFolderPath.Trim());
        var catalog = await _catalogStore.LoadAsync(folder, CancellationToken.None).ConfigureAwait(false);
        var animeFeature = _localGenreService.IsInLibrary(item, config.AnimeMovieLibraryName);
        if (animeFeature && !config.EnableAnimeMovieTrailers)
        {
            _logger.LogInformation(
                "Trailer Reel skipped prerolls before anime movie {MovieName} because anime trailers are disabled",
                item.Name);
            return [];
        }

        var poolTrailers = TrailerPoolSelector.Select(catalog.Trailers, animeFeature);
        var featureGenres = item.Genres ?? [];
        var anchorGenre = GenreTools.GetAnchorGenre(featureGenres);
        var featureTmdbId = item.ProviderIds.TryGetValue("Tmdb", out var id) ? id : null;

        var evaluated = poolTrailers
            .Where(entry => !string.Equals(entry.TmdbMovieId.ToString(), featureTmdbId, StringComparison.Ordinal))
            .Select(entry => new EvaluatedTrailer(
                entry,
                GenreMatchEvaluator.Evaluate(entry.Genres, featureGenres)))
            .ToList();
        var anchorMatches = evaluated
            .Where(candidate => candidate.Match.AnchorMatched)
            .ToList();
        var genreMatches = anchorMatches
            .Where(candidate => candidate.Match.IsEligible)
            .ToList();
        var candidates = genreMatches
            .Where(candidate => File.Exists(Path.Combine(folder, candidate.Entry.FileName)))
            .OrderByDescending(candidate => candidate.Match.SimilarityScore)
            .ThenByDescending(candidate => candidate.Match.SharedGenreCount)
            .ThenByDescending(candidate => candidate.Match.PrimaryGenreMatched)
            .ThenBy(candidate => candidate.Match.CandidateOnlyGenreCount)
            .ThenBy(_ => Random.Shared.Next())
            .ToList();

        var available = new List<AvailableTrailer>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var entry = candidate.Entry;
            var libraryItem = _hiddenLibrary.FindItem(folder, entry.FileName);
            if (libraryItem is not null)
            {
                var userData = _userDataManager.GetUserData(user, libraryItem);
                available.Add(new AvailableTrailer(
                    entry,
                    candidate.Match,
                    libraryItem,
                    new IntroInfo { Path = libraryItem.Path, ItemId = libraryItem.Id },
                    userData?.Played == true));
            }
            else
            {
                _logger.LogDebug("Trailer Reel has not yet indexed {FileName}; it will not be queued", entry.FileName);
            }
        }

        foreach (var rejected in anchorMatches.Where(candidate => !candidate.Match.IsEligible))
        {
            _logger.LogDebug(
                "Trailer Reel rejected {TrailerName} for {MovieName}: {Reason}; trailer genres [{TrailerGenres}]",
                rejected.Entry.MovieName,
                item.Name,
                rejected.Match.RejectionReason,
                string.Join(", ", rejected.Entry.Genres));
        }

        var selected = TrailerQueueSelector.Select(
            available,
            candidate => candidate.Watched,
            config.TrailersBeforeMovie,
            config.AvoidRepeatTrailersPerUser);
        var intros = new List<IntroInfo>(selected.Count);
        var queued = new List<AvailableTrailer>(selected.Count);
        foreach (var candidate in selected)
        {
            if (config.AvoidRepeatTrailersPerUser)
            {
                try
                {
                    var userData = _userDataManager.GetUserData(user, candidate.LibraryItem);
                    var previousPlayCount = userData?.PlayCount ?? 0;
                    _userDataManager.SaveUserData(
                        user,
                        candidate.LibraryItem,
                        new UpdateUserItemDataDto
                        {
                            Played = true,
                            PlayCount = previousPlayCount == int.MaxValue ? int.MaxValue : previousPlayCount + 1,
                            LastPlayedDate = DateTime.UtcNow,
                            PlaybackPositionTicks = 0,
                        },
                        UserDataSaveReason.TogglePlayed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Trailer Reel could not persist per-user watched state for trailer item {TrailerItemId}; it will not be queued",
                        candidate.LibraryItem.Id);
                    continue;
                }
            }

            intros.Add(candidate.Intro);
            queued.Add(candidate);
        }

        _selectionCache[key] = new CachedSelection(now.Add(CacheLifetime), intros);
        foreach (var expired in _selectionCache.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key))
        {
            _selectionCache.TryRemove(expired, out _);
        }

        _logger.LogInformation(
            "Trailer Reel queued {Count} profile-matched {Pool} trailer(s) before {MovieName}: anchor {AnchorGenre}, feature genres [{FeatureGenres}], {CatalogCount} in pool, {AnchorMatchCount} anchor-matched, {ProfileMatchCount} profile-compatible, {RejectedCount} rejected as off-theme, {PresentCount} present, {IndexedCount} indexed, {WatchedCount} already watched by this user; selected {SelectedTrailers}",
            intros.Count,
            animeFeature ? "anime" : "regular",
            item.Name,
            anchorGenre ?? "(none)",
            string.Join(
                ", ",
                featureGenres
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(GenreTools.Canonicalize)),
            poolTrailers.Count,
            anchorMatches.Count,
            genreMatches.Count,
            anchorMatches.Count - genreMatches.Count,
            candidates.Count,
            available.Count,
            available.Count(candidate => candidate.Watched),
            string.Join(
                "; ",
                queued.Select(candidate =>
                    $"{candidate.Entry.MovieName} [{string.Join(", ", candidate.Entry.Genres)}; similarity {candidate.Match.SimilarityScore / 10.0:F1}%]")));
        if (intros.Count < config.TrailersBeforeMovie)
        {
            _logger.LogWarning(
                "Trailer Reel coverage shortfall before {MovieName} in {Pool} pool: queued {Queued}/{Requested}; {Reason}. Indexed compatible: {Indexed}; unwatched for this user: {Unwatched}",
                item.Name, animeFeature ? "anime" : "regular", intros.Count, config.TrailersBeforeMovie,
                PlaybackStockDiagnostics.ExplainShortage(anchorGenre, anchorMatches.Count, genreMatches.Count,
                    candidates.Count, available.Count, available.Count(candidate => !candidate.Watched),
                    config.TrailersBeforeMovie, config.AvoidRepeatTrailersPerUser),
                available.Count, available.Count(candidate => !candidate.Watched));
        }

        return intros;
    }

    private sealed record EvaluatedTrailer(
        TrailerEntry Entry,
        GenreMatchEvaluation Match);

    private sealed record AvailableTrailer(
        TrailerEntry Entry,
        GenreMatchEvaluation Match,
        BaseItem LibraryItem,
        IntroInfo Intro,
        bool Watched);

    private sealed record CachedSelection(DateTimeOffset ExpiresUtc, IReadOnlyList<IntroInfo> Intros);
}
