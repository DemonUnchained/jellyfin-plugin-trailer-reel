namespace Jellyfin.Plugin.TrailerReel.Models;

public sealed class TrailerCatalog
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset UpdatedUtc { get; set; }

    public DateOnly WindowStart { get; set; }

    public DateOnly WindowEnd { get; set; }

    public List<string> LocalGenres { get; set; } = [];

    public List<TrailerEntry> Trailers { get; set; } = [];
}

public sealed class TrailerEntry
{
    public int TmdbMovieId { get; set; }

    public string MovieName { get; set; } = string.Empty;

    public int? Year { get; set; }

    public DateOnly ReleaseDate { get; set; }

    public List<string> Genres { get; set; } = [];

    public string YoutubeKey { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public DateTimeOffset DownloadedUtc { get; set; }
}

public sealed record MovieCandidate(
    int Id,
    string Title,
    DateOnly ReleaseDate,
    IReadOnlyList<int> GenreIds,
    double Popularity);

public sealed record TrailerVideo(
    string Key,
    string Name,
    bool Official,
    string Site,
    string Type,
    int Size,
    DateTimeOffset? PublishedUtc);
