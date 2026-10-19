using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Jellyfin.Plugin.TrailerReel.Services;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

var failures = new List<string>();
var checks = 0;

Check(
    "assembly version is 0.4.1.0",
    typeof(PluginConfiguration).Assembly.GetName().Version == new Version(0, 4, 1, 0));

Check(
    "genre aliases overlap",
    GenreTools.HasOverlap(["Sci-Fi", "Drama"], ["Science Fiction"]));
Check(
    "different genres do not overlap",
    !GenreTools.HasOverlap(["Comedy"], ["Horror"]));
Check(
    "anime maps to TMDb animation",
    GenreTools.HasOverlap(["Anime"], ["Animation"]));
Check(
    "first listed feature genre becomes the anchor genre",
    GenreTools.GetAnchorGenre(["Horror", "Fantasy", "Comedy"]) == "Horror");
Check(
    "anchor genre matching accepts the feature's primary genre",
    GenreTools.MatchesAnchorGenre(["Horror", "Mystery"], ["Horror", "Fantasy"]));
Check(
    "anchor genre matching rejects a secondary-only overlap",
    !GenreTools.MatchesAnchorGenre(["Fantasy", "Family"], ["Horror", "Fantasy"]));
Check(
    "anchor genre matching applies aliases",
    GenreTools.MatchesAnchorGenre(["Science Fiction"], ["Sci-Fi", "Adventure"]));

var closeHorrorMatch = GenreMatchEvaluator.Evaluate(
    ["Horror", "Thriller"],
    ["Horror", "Thriller", "Fantasy"]);
var pureHorrorMatch = GenreMatchEvaluator.Evaluate(
    ["Horror"],
    ["Horror", "Thriller", "Fantasy"]);
Check(
    "close horror profile is eligible and outranks an anchor-only profile",
    closeHorrorMatch.IsEligible
        && pureHorrorMatch.IsEligible
        && closeHorrorMatch.SimilarityScore > pureHorrorMatch.SimilarityScore);
Check(
    "animated horror is rejected for a live-action horror feature",
    !GenreMatchEvaluator.Evaluate(
        ["Horror", "Animation"],
        ["Horror", "Thriller"]).IsEligible);
Check(
    "horror comedy is rejected when the feature is not a comedy",
    !GenreMatchEvaluator.Evaluate(
        ["Horror", "Comedy"],
        ["Horror", "Thriller"]).IsEligible);
Check(
    "animation remains eligible when the feature also has animation",
    GenreMatchEvaluator.Evaluate(
        ["Horror", "Animation"],
        ["Horror", "Animation", "Fantasy"]).IsEligible);
Check(
    "a lone secondary anchor overlap is rejected",
    !GenreMatchEvaluator.Evaluate(
        ["Science Fiction", "Horror"],
        ["Horror", "Thriller", "Fantasy"]).IsEligible);
Check(
    "two shared genres can qualify when the candidate lists another primary genre",
    GenreMatchEvaluator.Evaluate(
        ["Mystery", "Horror", "Thriller"],
        ["Horror", "Thriller", "Fantasy"]).IsEligible);
Check(
    "a mostly unrelated candidate profile is rejected",
    !GenreMatchEvaluator.Evaluate(
        ["Horror", "Science Fiction", "Adventure"],
        ["Horror", "Thriller", "Fantasy"]).IsEligible);

await GenreCoverageTests.RunAsync(Check);

var defaultConfig = new PluginConfiguration();
Check("anime trailer pool is enabled by default", defaultConfig.EnableAnimeMovieTrailers);
Check("anime movie library defaults to Anime Movies", defaultConfig.AnimeMovieLibraryName == "Anime Movies");
Check("anime trailer pool defaults to a 30-file cap", defaultConfig.MaxAnimeTrailers == 30);
Check("anime library name matching ignores case", AnimeMovieRules.LibraryNameMatches("anime movies", "Anime Movies"));

var windowStart = new DateOnly(2026, 7, 18);
var windowEnd = new DateOnly(2027, 3, 18);
var regularDiscovery = TmdbClient.CreateRegularDiscoveryParameters(28, windowStart, windowEnd, 1, defaultConfig);
Check(
    "regular discovery filters primary releases instead of admitting old rereleases",
    regularDiscovery["primary_release_date.gte"] == "2026-07-18"
        && regularDiscovery["primary_release_date.lte"] == "2027-03-18"
        && !regularDiscovery.ContainsKey("release_date.gte")
        && !regularDiscovery.ContainsKey("release_date.lte"));
Check(
    "regular discovery keeps the US locale and requested release types",
    regularDiscovery["region"] == "US"
        && regularDiscovery["language"] == "en-US"
        && regularDiscovery["with_release_type"] == "2|3|4");
var animeDiscovery = TmdbClient.CreateAnimeDiscoveryParameters(windowStart, windowEnd, 2, defaultConfig);
Check(
    "anime discovery uses the same primary release window",
    animeDiscovery.ContainsKey("primary_release_date.gte")
        && animeDiscovery.ContainsKey("primary_release_date.lte")
        && !animeDiscovery.ContainsKey("release_date.gte")
        && animeDiscovery["with_original_language"] == "ja"
        && animeDiscovery["with_origin_country"] == "JP");
Check(
    "returned US release dates are constrained to the configured window",
    TmdbClient.IsWithinReleaseWindow(windowStart, windowStart, windowEnd)
        && TmdbClient.IsWithinReleaseWindow(windowEnd, windowStart, windowEnd)
        && !TmdbClient.IsWithinReleaseWindow(new DateOnly(2022, 5, 27), windowStart, windowEnd));

var japaneseAnimation = new MovieCandidate(90, "Anime", new DateOnly(2026, 10, 1), [16, 28], 50)
{
    OriginalLanguage = "ja",
    OriginCountryCodes = ["JP"],
};
var japaneseLiveAction = new MovieCandidate(91, "Live Action", new DateOnly(2026, 10, 1), [28], 50)
{
    OriginalLanguage = "ja",
    OriginCountryCodes = ["JP"],
};
var westernAnimation = new MovieCandidate(92, "Western Animation", new DateOnly(2026, 10, 1), [16], 50)
{
    OriginalLanguage = "en",
    OriginCountryCodes = ["US"],
};
Check("Japanese animation is classified as anime", AnimeMovieRules.IsAnimeCandidate(japaneseAnimation));
Check("Japanese live action is not classified as anime", !AnimeMovieRules.IsAnimeCandidate(japaneseLiveAction));
Check("Western animation is not classified as anime", !AnimeMovieRules.IsAnimeCandidate(westernAnimation));

var poolEntries = new[]
{
    new TrailerEntry { TmdbMovieId = 1, Pool = TrailerPool.Regular },
    new TrailerEntry { TmdbMovieId = 2, Pool = TrailerPool.Anime },
};
Check(
    "regular playback selects only the regular trailer pool",
    TrailerPoolSelector.Select(poolEntries, animeFeature: false).Select(entry => entry.TmdbMovieId).SequenceEqual([1]));
Check(
    "anime playback selects only the anime trailer pool",
    TrailerPoolSelector.Select(poolEntries, animeFeature: true).Select(entry => entry.TmdbMovieId).SequenceEqual([2]));

var catalogMigrationFolder = Path.Combine(Path.GetTempPath(), $"trailer-reel-catalog-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(catalogMigrationFolder);
    File.WriteAllText(
        Path.Combine(catalogMigrationFolder, CatalogStore.CatalogFileName),
        """{"schemaVersion":1,"trailers":[{"tmdbMovieId":77,"movieName":"Legacy"}]}""");
    var catalogStore = new CatalogStore(NullLogger<CatalogStore>.Instance);
    var migratedCatalog = await catalogStore.LoadAsync(catalogMigrationFolder, CancellationToken.None);
    Check(
        "version 0.2 catalog entries migrate into the regular pool",
        migratedCatalog.Trailers.Count == 1 && migratedCatalog.Trailers[0].Pool == TrailerPool.Regular);

    var expectedReleaseDate = new DateOnly(2026, 12, 25);
    migratedCatalog.Trailers[0].ReleaseDate = expectedReleaseDate;
    await catalogStore.SaveAsync(catalogMigrationFolder, migratedCatalog, CancellationToken.None);
    var reloadedCatalogStore = new CatalogStore(NullLogger<CatalogStore>.Instance);
    var reloadedCatalog = await reloadedCatalogStore.LoadAsync(catalogMigrationFolder, CancellationToken.None);
    Check(
        "catalog release dates survive a save and reload",
        reloadedCatalog.Trailers[0].ReleaseDate == expectedReleaseDate);
}
finally
{
    if (Directory.Exists(catalogMigrationFolder))
    {
        Directory.Delete(catalogMigrationFolder, recursive: true);
    }
}

var localMovies = new LocalMovieInventory(
    ["Action", "Sci-Fi"],
    [1234],
    [("Amélie: The Movie", 2001)]);
Check(
    "local movie inventory matches TMDb id",
    localMovies.Contains(1234, "Different title", 2026));
Check(
    "local movie inventory falls back to normalized title and year",
    localMovies.Contains(9999, "AMELIE - THE MOVIE", 2001));
Check(
    "local movie inventory does not conflate remakes",
    !localMovies.Contains(9999, "Amelie The Movie", 2026));
Check(
    "local movie inventory leaves unrelated candidates eligible",
    !localMovies.Contains(5678, "Another Movie", 2001));

var indexTestFolder = Path.Combine(Path.GetTempPath(), $"trailer-reel-index-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(indexTestFolder);
    const string sourceFileName = "Movie Name-trailer.mp4";
    File.WriteAllText(Path.Combine(indexTestFolder, sourceFileName), "test");
    var indexEntry = new TrailerEntry { FileName = sourceFileName };
    var indexCount = TrailerIndexStore.Synchronize(indexTestFolder, [indexEntry]);
    var playbackPath = TrailerIndexStore.GetPlaybackPath(indexTestFolder, sourceFileName);
    Check("trailer index creates one alias", indexCount == 1 && File.Exists(playbackPath));
    Check(
        "trailer index alias avoids Jellyfin trailer-extra suffix",
        !Path.GetFileNameWithoutExtension(playbackPath).EndsWith("-trailer", StringComparison.OrdinalIgnoreCase));
    Check(
        "trailer index alias points relatively to the original",
        string.Equals(
            new FileInfo(playbackPath).LinkTarget,
            Path.Combine("..", sourceFileName),
            StringComparison.Ordinal));

    var namingOptions = new NamingOptions();
    var sourceNaming = VideoResolver.Resolve(sourceFileName, isDirectory: false, namingOptions);
    var aliasNaming = VideoResolver.Resolve(Path.GetFileName(playbackPath), isDirectory: false, namingOptions);
    Check(
        "Jellyfin 12.2 classifies required source name as trailer extra",
        sourceNaming?.ExtraType == ExtraType.Trailer);
    Check(
        "Jellyfin 12.2 classifies index alias as standalone video",
        aliasNaming?.ExtraType is null);

    TrailerIndexStore.Synchronize(indexTestFolder, []);
    Check("trailer index removes stale managed alias", !File.Exists(playbackPath));
}
finally
{
    if (Directory.Exists(indexTestFolder))
    {
        Directory.Delete(indexTestFolder, recursive: true);
    }
}

var action = new MovieCandidate(1, "Action One", new DateOnly(2026, 9, 1), [28], 100);
var drama = new MovieCandidate(2, "Drama One", new DateOnly(2026, 9, 2), [18], 90);
var duplicate = new MovieCandidate(1, "Action One", new DateOnly(2026, 9, 1), [28, 18], 100);
var selected = CandidateSelector.RoundRobin(
    new Dictionary<int, IReadOnlyList<MovieCandidate>>
    {
        [28] = [action],
        [18] = [duplicate, drama],
    },
    3);
Check("round robin de-duplicates movies", selected.Select(movie => movie.Id).SequenceEqual([drama.Id, action.Id])
    || selected.Select(movie => movie.Id).SequenceEqual([action.Id, drama.Id]));

Check(
    "preferred trailer filename",
    YtDlpDownloader.ChooseFileName("Movie: Name", 2026, 10, ".mp4", "/tmp", []) == "Movie Name-trailer.mp4");
Check(
    "year disambiguates duplicate title",
    YtDlpDownloader.ChooseFileName("Movie", 2026, 10, ".mp4", "/tmp", ["Movie-trailer.mp4"])
        == "Movie (2026)-trailer.mp4");
Check(
    "TMDb id disambiguates duplicate title and year",
    YtDlpDownloader.ChooseFileName(
        "Movie",
        2026,
        10,
        ".mp4",
        "/tmp",
        ["Movie-trailer.mp4", "Movie (2026)-trailer.mp4"])
        == "Movie [tmdb-10]-trailer.mp4");

var queueCandidates = new[]
{
    new QueueCandidate("watched", true),
    new QueueCandidate("new-1", false),
    new QueueCandidate("new-2", false),
    new QueueCandidate("new-3", false),
};
var unseenQueue = TrailerQueueSelector.Select(
    queueCandidates,
    candidate => candidate.Watched,
    2,
    avoidRepeats: true);
Check(
    "per-user queue excludes watched trailers",
    unseenQueue.Select(candidate => candidate.Name).SequenceEqual(["new-1", "new-2"]));
var repeatableQueue = TrailerQueueSelector.Select(
    queueCandidates,
    candidate => candidate.Watched,
    2,
    avoidRepeats: false);
Check(
    "repeat filtering can be disabled",
    repeatableQueue.Select(candidate => candidate.Name).SequenceEqual(["watched", "new-1"]));

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine($"All Trailer Reel tests passed ({checks} checks).");
return 0;

void Check(string name, bool condition)
{
    checks++;
    if (!condition)
    {
        failures.Add("FAILED: " + name);
    }
}

file sealed record QueueCandidate(string Name, bool Watched);
