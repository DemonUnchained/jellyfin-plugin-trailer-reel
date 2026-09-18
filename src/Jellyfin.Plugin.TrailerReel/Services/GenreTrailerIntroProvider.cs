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
        var featureTmdbId = item.ProviderIds.TryGetValue("Tmdb", out var id) ? id : null;

        var genreMatches = poolTrailers
            .Where(entry => !string.Equals(entry.TmdbMovieId.ToString(), featureTmdbId, StringComparison.Ordinal))
            .Where(entry => GenreTools.HasOverlap(entry.Genres, featureGenres))
            .ToList();
        var candidates = genreMatches
            .Where(entry => File.Exists(Path.Combine(folder, entry.FileName)))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        var available = new List<AvailableTrailer>(candidates.Count);
        foreach (var entry in candidates)
        {
            var libraryItem = _hiddenLibrary.FindItem(folder, entry.FileName);
            if (libraryItem is not null)
            {
                var userData = _userDataManager.GetUserData(user, libraryItem);
                available.Add(new AvailableTrailer(
                    libraryItem,
                    new IntroInfo { Path = libraryItem.Path, ItemId = libraryItem.Id },
                    userData?.Played == true));
            }
            else
            {
                _logger.LogDebug("Trailer Reel has not yet indexed {FileName}; it will not be queued", entry.FileName);
            }
        }

        var selected = TrailerQueueSelector.Select(
            available,
            candidate => candidate.Watched,
            config.TrailersBeforeMovie,
            config.AvoidRepeatTrailersPerUser);
        var intros = new List<IntroInfo>(selected.Count);
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
        }

        _selectionCache[key] = new CachedSelection(now.Add(CacheLifetime), intros);
        foreach (var expired in _selectionCache.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key))
        {
            _selectionCache.TryRemove(expired, out _);
        }

        _logger.LogInformation(
            "Trailer Reel queued {Count} same-genre {Pool} trailer(s) before {MovieName}: {CatalogCount} in pool, {GenreMatchCount} genre-matched, {PresentCount} present, {IndexedCount} indexed, {WatchedCount} already watched by this user",
            intros.Count,
            animeFeature ? "anime" : "regular",
            item.Name,
            poolTrailers.Count,
            genreMatches.Count,
            candidates.Count,
            available.Count,
            available.Count(candidate => candidate.Watched));
        return intros;
    }

    private sealed record AvailableTrailer(BaseItem LibraryItem, IntroInfo Intro, bool Watched);

    private sealed record CachedSelection(DateTimeOffset ExpiresUtc, IReadOnlyList<IntroInfo> Intros);
}
