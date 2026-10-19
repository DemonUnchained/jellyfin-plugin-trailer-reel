using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed record GenreCoverageRow(
    string AnchorGenre,
    IReadOnlyList<string> FeatureGenres,
    int LocalMovieCount,
    int Target,
    int Compatible,
    int Present,
    int Indexed)
{
    public bool Shortage => Indexed < Target;
}

public sealed record PoolCoverageReport(
    TrailerPool Pool,
    int Maximum,
    int Stored,
    IReadOnlyList<GenreCoverageRow> Profiles);

public sealed record GenreCoverageReport(DateTimeOffset GeneratedUtc, IReadOnlyList<PoolCoverageReport> Pools)
{
    public const string FileName = ".trailer-reel-coverage.json";

    public static PoolCoverageReport Assess(
        TrailerPool pool,
        GenreCoveragePlanner planner,
        IEnumerable<TrailerEntry> catalog,
        Func<TrailerEntry, bool> present,
        Func<TrailerEntry, bool> indexed)
    {
        var entries = catalog.Where(entry => entry.Pool == pool).ToList();
        var compatibleCounts = planner.CountCompatible(entries);
        var presentEntries = entries.Where(present).ToList();
        var presentCounts = planner.CountCompatible(presentEntries);
        var indexedCounts = planner.CountCompatible(presentEntries.Where(indexed));
        return new PoolCoverageReport(pool, planner.Maximum, entries.Count,
            planner.Profiles.Select((profile, i) => new GenreCoverageRow(
                profile.AnchorGenre, profile.Genres, profile.MovieCount, planner.Target,
                compatibleCounts[i], presentCounts[i], indexedCounts[i])).ToList());
    }

    public async Task SaveAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }
}
