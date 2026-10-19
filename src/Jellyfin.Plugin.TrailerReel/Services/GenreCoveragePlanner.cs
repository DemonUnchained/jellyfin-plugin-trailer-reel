using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed record LocalGenreProfile(IReadOnlyList<string> Genres, int MovieCount)
{
    public string AnchorGenre => Genres[0];

    public static IReadOnlyList<LocalGenreProfile> Create(IEnumerable<IEnumerable<string>> movies) =>
        movies.Select(genres => genres
                .Where(genre => !string.IsNullOrWhiteSpace(genre))
                .Select(GenreTools.Canonicalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .Where(genres => genres.Length > 0)
            // Only the first genre's order affects matching; merge equivalent secondary profiles.
            .GroupBy(genres => string.Join("\u001f", genres.Take(1).Concat(
                genres.Skip(1).OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase))),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new LocalGenreProfile(group.First(), group.Count()))
            .OrderBy(profile => profile.AnchorGenre, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => string.Join(",", profile.Genres), StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>Uses playback's strict matcher to reserve stock for actual local movie profiles.</summary>
public sealed class GenreCoveragePlanner
{
    private readonly Dictionary<string, bool[]> _matches = new(StringComparer.OrdinalIgnoreCase);
    private readonly double[] _weights;

    public GenreCoveragePlanner(IReadOnlyList<LocalGenreProfile> profiles, int maximum, int minimum, int queueLength)
    {
        Profiles = profiles;
        Maximum = maximum;
        var anchorCounts = profiles.GroupBy(profile => profile.AnchorGenre, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        // A reserve is a best-effort target, never permission to exceed either pool's cap.
        Target = Math.Min(Math.Max(minimum, queueLength), Math.Max(1, maximum / Math.Max(1, anchorCounts.Count)));
        _weights = profiles.Select(profile => 1d / anchorCounts[profile.AnchorGenre]).ToArray();
    }

    public IReadOnlyList<LocalGenreProfile> Profiles { get; }

    public int Maximum { get; }

    public int Target { get; }

    public int[] CountCompatible(IEnumerable<TrailerEntry> entries)
    {
        var counts = new int[Profiles.Count];
        foreach (var entry in entries)
        {
            var matches = Matches(entry);
            for (var i = 0; i < counts.Length; i++)
            {
                if (matches[i])
                {
                    counts[i]++;
                }
            }
        }

        return counts;
    }

    public IReadOnlySet<string> DeficientAnchors(IEnumerable<TrailerEntry> entries)
    {
        var counts = CountCompatible(entries);
        return Profiles.Where((_, i) => counts[i] < Target)
            .Select(profile => profile.AnchorGenre).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public double Gain(TrailerEntry candidate, IReadOnlyList<int> counts)
    {
        var matches = Matches(candidate);
        return Enumerable.Range(0, Profiles.Count)
            .Where(i => matches[i] && counts[i] < Target)
            .Sum(i => (Target - counts[i]) * _weights[i]);
    }

    public bool IsUseful(TrailerEntry candidate) => Matches(candidate).Any(match => match);

    public TrailerAdditionPlan? PlanAddition(
        IReadOnlyList<TrailerEntry> current,
        TrailerEntry candidate,
        bool allowReplacement = true)
    {
        if (!IsUseful(candidate) || current.Any(entry => entry.TmdbMovieId == candidate.TmdbMovieId))
        {
            return null;
        }

        if (current.Count < Maximum)
        {
            return new TrailerAdditionPlan(null);
        }

        var counts = CountCompatible(current);
        if (!allowReplacement || Gain(candidate, counts) <= 0)
        {
            return null;
        }

        var added = Matches(candidate);
        foreach (var victim in current.OrderBy(entry => entry.DownloadedUtc).ThenBy(entry => entry.TmdbMovieId))
        {
            var removed = Matches(victim);
            var improves = false;
            var safe = true;
            for (var i = 0; i < counts.Length; i++)
            {
                var after = counts[i] + (added[i] ? 1 : 0) - (removed[i] ? 1 : 0);
                // A replacement must preserve every profile's existing reserve, including scarce ones.
                if (after < Math.Min(counts[i], Target))
                {
                    safe = false;
                    break;
                }

                improves |= Math.Min(after, Target) > Math.Min(counts[i], Target);
            }

            if (safe && improves)
            {
                return new TrailerAdditionPlan(victim);
            }
        }

        return null;
    }

    public IReadOnlyList<TrailerEntry> RetainWithinCap(IEnumerable<TrailerEntry> entries)
    {
        var remaining = entries.GroupBy(entry => entry.TmdbMovieId).Select(group => group.First()).ToList();
        if (remaining.Count <= Maximum)
        {
            return remaining;
        }

        var selected = new List<TrailerEntry>();
        while (selected.Count < Maximum && remaining.Count > 0)
        {
            var counts = CountCompatible(selected);
            var next = remaining.OrderByDescending(entry => Gain(entry, counts))
                .ThenByDescending(entry => entry.DownloadedUtc).ThenBy(entry => entry.TmdbMovieId).First();
            selected.Add(next);
            remaining.Remove(next);
        }

        return selected;
    }

    private bool[] Matches(TrailerEntry entry)
    {
        var key = string.Join("\u001f", entry.Genres);
        if (!_matches.TryGetValue(key, out var matches))
        {
            matches = Profiles.Select(profile => GenreMatchEvaluator.Evaluate(entry.Genres, profile.Genres).IsEligible).ToArray();
            _matches[key] = matches;
        }

        return matches;
    }
}

public sealed record TrailerAdditionPlan(TrailerEntry? Replaced);
