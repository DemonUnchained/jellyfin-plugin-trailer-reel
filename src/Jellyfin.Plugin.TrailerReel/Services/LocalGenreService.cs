using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class LocalGenreService
{
    private readonly ILibraryManager _libraryManager;

    public LocalGenreService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public LocalMovieInventory GetCurrentMovieInventory(string trailerFolder)
        => GetCurrentMovieContext(trailerFolder, string.Empty).AllMovies;

    public LocalMovieContext GetCurrentMovieContext(string trailerFolder, string animeMovieLibraryName)
    {
        var trailerRoot = NormalizePath(trailerFolder);
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Movie>()
            .Where(movie => !PathIsUnder(movie.Path, trailerRoot))
            .ToArray();

        var animeLibraryFound = _libraryManager.GetVirtualFolders()
            .Any(folder => AnimeMovieRules.LibraryNameMatches(folder.Name, animeMovieLibraryName));
        var animeMovies = movies
            .Where(movie => IsInLibrary(movie, animeMovieLibraryName))
            .ToHashSet();

        var tmdbMovieIds = new List<int>();
        var fallbackTitleYears = new List<(string Title, int Year)>();
        foreach (var movie in movies)
        {
            var tmdbIdText = movie.ProviderIds
                .FirstOrDefault(pair => string.Equals(pair.Key, "Tmdb", StringComparison.OrdinalIgnoreCase))
                .Value;
            if (int.TryParse(tmdbIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId)
                && tmdbId > 0)
            {
                tmdbMovieIds.Add(tmdbId);
            }
            else if (movie.ProductionYear is > 0 && !string.IsNullOrWhiteSpace(movie.Name))
            {
                fallbackTitleYears.Add((movie.Name, movie.ProductionYear.Value));
            }
        }

        var genres = movies
            .SelectMany(movie => movie.Genres ?? [])
            .ToArray();

        var regularGenres = movies
            .Where(movie => !animeMovies.Contains(movie))
            .SelectMany(movie => movie.Genres ?? [])
            .ToArray();
        var animeGenres = animeMovies
            .SelectMany(movie => movie.Genres ?? [])
            .ToArray();

        return new LocalMovieContext(
            new LocalMovieInventory(genres, tmdbMovieIds, fallbackTitleYears),
            CanonicalGenres(regularGenres),
            CanonicalGenres(animeGenres),
            animeLibraryFound);
    }

    public bool IsInLibrary(BaseItem item, string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        return _libraryManager.GetCollectionFolders(item)
            .Any(folder => AnimeMovieRules.LibraryNameMatches(folder.Name, libraryName));
    }

    private static IReadOnlyList<string> CanonicalGenres(IEnumerable<string> genres) =>
        genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(GenreTools.Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool PathIsUnder(string? path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        var normalized = NormalizePath(path);
        return string.Equals(normalized, parent, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
}

public sealed record LocalMovieContext(
    LocalMovieInventory AllMovies,
    IReadOnlyList<string> RegularGenres,
    IReadOnlyList<string> AnimeGenres,
    bool AnimeLibraryFound);
