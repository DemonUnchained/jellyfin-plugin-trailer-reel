using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class TmdbClient
{
    private static readonly Uri ApiRoot = new("https://api.themoviedb.org/3/");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public TmdbClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = ApiRoot;
        _httpClient.Timeout = TimeSpan.FromSeconds(45);
    }

    public async Task<IReadOnlyDictionary<int, string>> GetMovieGenresAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var path = $"genre/movie/list?language={Uri.EscapeDataString(config.Language)}";
        var response = await GetAsync<GenreListResponse>(path, config, cancellationToken).ConfigureAwait(false);
        return response.Genres.ToDictionary(genre => genre.Id, genre => genre.Name);
    }

    public async Task<IReadOnlyList<MovieCandidate>> DiscoverMoviesAsync(
        int genreId,
        DateOnly start,
        DateOnly end,
        int page,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["language"] = config.Language,
            ["region"] = config.Region,
            ["sort_by"] = "popularity.desc",
            ["include_adult"] = "false",
            ["include_video"] = "true",
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["with_genres"] = genreId.ToString(CultureInfo.InvariantCulture),
            ["with_release_type"] = "2|3|4",
            ["release_date.gte"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["release_date.lte"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var path = "discover/movie?" + string.Join(
            "&",
            parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var response = await GetAsync<DiscoverResponse>(path, config, cancellationToken).ConfigureAwait(false);

        var results = new List<MovieCandidate>();
        foreach (var movie in response.Results)
        {
            if (movie.Id <= 0
                || string.IsNullOrWhiteSpace(movie.Title)
                || !DateOnly.TryParseExact(
                    movie.ReleaseDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var releaseDate))
            {
                continue;
            }

            results.Add(new MovieCandidate(movie.Id, movie.Title, releaseDate, movie.GenreIds, movie.Popularity));
        }

        return results;
    }

    public async Task<TrailerVideo?> GetBestTrailerAsync(
        int movieId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var localizedPath = $"movie/{movieId.ToString(CultureInfo.InvariantCulture)}/videos?language={Uri.EscapeDataString(config.Language)}";
        var localized = await GetAsync<VideoListResponse>(localizedPath, config, cancellationToken).ConfigureAwait(false);
        var all = localized.Results.ToList();

        if (!all.Any(IsTrailerCandidate))
        {
            var fallbackPath = $"movie/{movieId.ToString(CultureInfo.InvariantCulture)}/videos";
            var fallback = await GetAsync<VideoListResponse>(fallbackPath, config, cancellationToken).ConfigureAwait(false);
            all.AddRange(fallback.Results);
        }

        return all
            .Where(IsTrailerCandidate)
            .Where(video => !config.OfficialTrailersOnly || video.Official)
            .GroupBy(video => video.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(video => video.Size == 1080)
            .ThenByDescending(video => video.Official)
            .ThenByDescending(video => video.PublishedUtc)
            .Select(video => new TrailerVideo(
                video.Key,
                video.Name,
                video.Official,
                video.Site,
                video.Type,
                video.Size,
                video.PublishedUtc))
            .FirstOrDefault();
    }

    private static bool IsTrailerCandidate(VideoDto video) =>
        string.Equals(video.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
        && string.Equals(video.Type, "Trailer", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(video.Key);

    private async Task<T> GetAsync<T>(
        string relativePath,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.TmdbReadAccessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("TMDb returned an empty response.");
    }

    private sealed class GenreListResponse
    {
        [JsonPropertyName("genres")]
        public List<GenreDto> Genres { get; set; } = [];
    }

    private sealed class GenreDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class DiscoverResponse
    {
        [JsonPropertyName("results")]
        public List<MovieDto> Results { get; set; } = [];
    }

    private sealed class MovieDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("genre_ids")]
        public List<int> GenreIds { get; set; } = [];

        [JsonPropertyName("popularity")]
        public double Popularity { get; set; }
    }

    private sealed class VideoListResponse
    {
        [JsonPropertyName("results")]
        public List<VideoDto> Results { get; set; } = [];
    }

    private sealed class VideoDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("official")]
        public bool Official { get; set; }

        [JsonPropertyName("site")]
        public string Site { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedUtc { get; set; }
    }
}
