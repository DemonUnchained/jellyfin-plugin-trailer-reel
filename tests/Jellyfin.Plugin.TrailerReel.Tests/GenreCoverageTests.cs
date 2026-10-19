using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Jellyfin.Plugin.TrailerReel.Services;

internal static class GenreCoverageTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var profiles = LocalGenreProfile.Create([["Action"], ["Horror", "Thriller"]]);
        var planner = new GenreCoveragePlanner(profiles, 100, 6, 3);
        check("coverage default reserve is six", new PluginConfiguration().MinimumGenreTrailers == 6 && planner.Target == 6);
        check("small pools scale reserves within their cap", new GenreCoveragePlanner(profiles, 4, 6, 3).Target == 2);
        check("reserve accounts for the requested queue length", new GenreCoveragePlanner(profiles, 100, 1, 3).Target == 3);
        var grouped = LocalGenreProfile.Create([
            ["Horror", "Thriller", "Fantasy"], ["Horror", "Fantasy", "Thriller"],
            ["Thriller", "Horror", "Fantasy"], ["Sci-Fi"], ["Science Fiction"], [""]]);
        check("profile grouping merges aliases and secondary order but preserves anchors",
            grouped.Count == 3 && grouped.Single(profile => profile.AnchorGenre == "Horror").MovieCount == 2
                && grouped.Single(profile => profile.AnchorGenre == "Science Fiction").MovieCount == 2);

        var skewed = Enumerable.Range(1, 100).Select(id => Entry(id, "Action")).ToList();
        var horror = Entry(200, "Horror", "Thriller");
        check("full skewed pool identifies horror shortage", planner.DeficientAnchors(skewed).SetEquals(["Horror"]));
        check("full pool can replace surplus action with horror", planner.PlanAddition(skewed, horror)?.Replaced?.Genres.Contains("Action") == true);
        check("disabled cleanup prevents full-pool replacements", planner.PlanAddition(skewed, horror, allowReplacement: false) is null);
        check("horror animation does not count toward straight horror coverage", !planner.IsUseful(Entry(201, "Horror", "Animation")));
        check("lone secondary horror cannot inflate reserve", !planner.IsUseful(Entry(202, "Science Fiction", "Horror")));

        var scarce = skewed.Take(99).Append(Entry(300, "Horror", "Thriller")).ToList();
        var threeProfiles = LocalGenreProfile.Create([["Action"], ["Horror", "Thriller"], ["Drama"]]);
        var threePlanner = new GenreCoveragePlanner(threeProfiles, 100, 6, 3);
        check("scarce horror stock is protected when replenishing drama",
            threePlanner.PlanAddition(scarce, Entry(400, "Drama"))?.Replaced?.Genres.Contains("Action") == true);
        var capped = planner.RetainWithinCap(skewed.Concat([horror, Entry(203, "Horror", "Thriller")]));
        check("lowering a cap retains scarce compatible stock", capped.Count == 100 && capped.Count(entry => entry.Genres.Contains("Horror")) == 2);

        var catalog = new TrailerCatalog { Trailers = skewed.ToList() };
        var acquired = new List<int>();
        var deleted = new List<int>();
        var replacements = new List<int>();
        var saves = 0;
        var candidates = Enumerable.Range(200, 10).Select(id => new TrailerAcquisitionCandidate(
            Entry(id, "Horror", "Thriller"), 1000 - id)).ToList();
        // The higher-popularity animation candidate must never be acquired for a straight feature.
        candidates.Insert(0, new TrailerAcquisitionCandidate(Entry(500, "Horror", "Animation"), 99999));
        await TrailerPoolReplenisher.ReplenishAsync(catalog, TrailerPool.Regular, planner, candidates, true,
            (entry, _) => { acquired.Add(entry.TmdbMovieId); return Task.FromResult(true); },
            _ =>
            {
                saves++;
                check("catalog cap holds at every replacement commit", catalog.Trailers.Count == 100);
                check("replacement is saved before old media is deleted", deleted.Count == saves - 1);
                return Task.CompletedTask;
            },
            entry => deleted.Add(entry.TmdbMovieId),
            (_, _) => check("successful replenishment has no acquisition errors", false),
            (_, entry) => replacements.Add(entry.TmdbMovieId), CancellationToken.None);
        check("full action-heavy pool replenishes six compatible horror trailers",
            catalog.Trailers.Count == 100 && catalog.Trailers.Count(entry => entry.Genres.Contains("Horror")) == 6
                && acquired.Count == 6 && deleted.Count == 6 && replacements.Count == 6 && !acquired.Contains(500));
        check("balanced full pool stops replacing once reserve is met",
            planner.PlanAddition(catalog.Trailers, Entry(999, "Horror", "Thriller")) is null);

        var failedCatalog = new TrailerCatalog { Trailers = skewed.ToList() };
        var failedCount = 0;
        await TrailerPoolReplenisher.ReplenishAsync(failedCatalog, TrailerPool.Regular, planner, candidates, true,
            (_, _) => throw new IOException("simulated 1080p download failure"),
            _ => { check("failed acquisition cannot save a changed catalog", false); return Task.CompletedTask; },
            _ => check("failed acquisition cannot delete existing trailers", false),
            (_, _) => failedCount++, (_, _) => check("failed acquisition cannot replace", false), CancellationToken.None);
        check("all failed replacements leave full original stock intact",
            failedCount == 10 && failedCatalog.Trailers.SequenceEqual(skewed));

        var saveFailedCatalog = new TrailerCatalog { Trailers = skewed.ToList() };
        var discarded = new List<int>();
        try
        {
            await TrailerPoolReplenisher.ReplenishAsync(saveFailedCatalog, TrailerPool.Regular, planner, candidates.Take(2).ToList(), true,
                (_, _) => Task.FromResult(true), _ => throw new IOException("simulated catalog write failure"),
                entry => discarded.Add(entry.TmdbMovieId), (_, _) => { }, (_, _) => { }, CancellationToken.None);
            check("catalog save failure must propagate", false);
        }
        catch (IOException)
        {
            check("catalog write failure restores old stock and discards only the new file",
                saveFailedCatalog.Trailers.SequenceEqual(skewed) && discarded.SequenceEqual([200]));
        }

        var cancelCatalog = new TrailerCatalog { Trailers = skewed.ToList() };
        try
        {
            await TrailerPoolReplenisher.ReplenishAsync(cancelCatalog, TrailerPool.Regular, planner, candidates, true,
                (_, _) => throw new OperationCanceledException(), _ => Task.CompletedTask,
                _ => check("cancellation before acquisition cannot delete old files", false),
                (_, _) => check("cancellation is not treated as a failed candidate", false), (_, _) => { }, CancellationToken.None);
            check("acquisition cancellation must propagate", false);
        }
        catch (OperationCanceledException)
        {
            check("cancellation preserves full original stock", cancelCatalog.Trailers.SequenceEqual(skewed));
        }

        var isolatedCatalog = new TrailerCatalog { Trailers = [Entry(1, "Horror", "Thriller")] };
        var animeCandidate = Entry(600, "Horror", "Thriller");
        animeCandidate.Pool = TrailerPool.Anime;
        var acquiredIsolated = new List<int>();
        await TrailerPoolReplenisher.ReplenishAsync(isolatedCatalog, TrailerPool.Regular, planner,
            [new(animeCandidate, 10), new(Entry(1, "Horror", "Thriller"), 20), new(Entry(2, "Horror", "Thriller"), 5)], true,
            (entry, _) => { acquiredIsolated.Add(entry.TmdbMovieId); return Task.FromResult(true); },
            _ => Task.CompletedTask, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        check("replenishment excludes other pools and duplicate movies", acquiredIsolated.SequenceEqual([2]));

        var coverage = GenreCoverageReport.Assess(TrailerPool.Regular, planner,
            [Entry(700, "Horror", "Thriller"), Entry(701, "Horror", "Thriller"), Entry(702, "Horror", "Animation"), animeCandidate],
            entry => entry.TmdbMovieId != 701, _ => false);
        var horrorRow = coverage.Profiles.Single(row => row.AnchorGenre == "Horror");
        check("coverage distinguishes compatible, present and indexed without counting anime or off-theme stock",
            horrorRow.Compatible == 2 && horrorRow.Present == 1 && horrorRow.Indexed == 0 && horrorRow.Shortage);
        var reportFolder = Path.Combine(Path.GetTempPath(), $"trailer-coverage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(reportFolder);
            await new GenreCoverageReport(DateTimeOffset.UtcNow, [coverage]).SaveAsync(reportFolder, CancellationToken.None);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(reportFolder, GenreCoverageReport.FileName)));
            check("coverage report persists pool and profile diagnostics", report.RootElement.GetProperty("pools")[0].GetProperty("pool").GetString() == "Regular"
                && !File.Exists(Path.Combine(reportFolder, GenreCoverageReport.FileName + ".tmp")));
        }
        finally
        {
            Directory.Delete(reportFolder, recursive: true);
        }

        check("playback explains exhausted per-user stock",
            PlaybackStockDiagnostics.ExplainShortage("Horror", 6, 6, 6, 6, 0, 3, true).Contains("already watched"));
        check("playback explains missing indexing separately",
            PlaybackStockDiagnostics.ExplainShortage("Horror", 6, 6, 6, 0, 0, 3, true).Contains("not been indexed"));
        check("playback explains rejected profiles separately",
            PlaybackStockDiagnostics.ExplainShortage("Horror", 6, 0, 0, 0, 0, 3, true).Contains("strict genre-profile"));

        var config = new PluginConfiguration { TmdbReadAccessToken = "test-only" };
        var handler = new DiscoveryHandler();
        using var client = new HttpClient(handler);
        var tmdb = new TmdbClient(client);
        var start = new DateOnly(2026, 8, 1);
        var end = new DateOnly(2027, 4, 1);
        var first = await tmdb.DiscoverGenrePageAsync(27, TrailerPool.Regular, start, end, 1, config, CancellationToken.None);
        var second = await tmdb.DiscoverGenrePageAsync(27, TrailerPool.Regular, start, end, 2, config, CancellationToken.None);
        check("filtered empty page still allows deeper discovery", first.Movies.Count == 0 && first.HasMore);
        check("deeper page returns eligible movie and stops at total_pages", second.Movies.Single().Id == 800 && !second.HasMore);
        var animeParams = TmdbClient.CreateAnimeDiscoveryParameters(start, end, 3, config, 27);
        check("anime shortage search requires both animation and target genre",
            animeParams["with_genres"] == "16,27" && animeParams["with_original_language"] == "ja"
                && animeParams["with_origin_country"] == "JP" && animeParams["region"] == "US"
                && animeParams["primary_release_date.gte"] == "2026-08-01");
    }

    private static TrailerEntry Entry(int id, params string[] genres) => new()
    {
        TmdbMovieId = id,
        MovieName = $"Movie {id}",
        Genres = genres.ToList(),
        FileName = $"Movie {id}-trailer.mp4",
    };

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isSecond = request.RequestUri!.Query.Contains("page=2");
            var date = isSecond ? "2026-10-01" : "1980-01-01";
            var json = $$"""{"total_pages":2,"results":[{"id":800,"title":"Horror","release_date":"{{date}}","genre_ids":[27],"popularity":5}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
