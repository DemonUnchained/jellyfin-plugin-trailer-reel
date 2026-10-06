namespace Jellyfin.Plugin.TrailerReel.Services;

public static class GenreMatchEvaluator
{
    private static readonly HashSet<string> DistinctiveGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "Animation",
        "Comedy",
        "Documentary",
        "Family",
        "Music",
    };

    public static GenreMatchEvaluation Evaluate(
        IEnumerable<string> candidateGenres,
        IEnumerable<string> featureGenres)
    {
        ArgumentNullException.ThrowIfNull(candidateGenres);
        ArgumentNullException.ThrowIfNull(featureGenres);

        var candidate = CanonicalList(candidateGenres);
        var feature = CanonicalList(featureGenres);
        var anchorGenre = feature.FirstOrDefault();
        var candidatePrimaryGenre = candidate.FirstOrDefault();
        var featureSet = feature.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateSet = candidate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedGenreCount = candidateSet.Count(featureSet.Contains);
        var candidateOnlyGenres = candidate
            .Where(genre => !featureSet.Contains(genre))
            .ToArray();
        var conflictingGenres = candidateOnlyGenres
            .Where(DistinctiveGenres.Contains)
            .ToArray();
        var anchorMatched = anchorGenre is not null && candidateSet.Contains(anchorGenre);
        var primaryGenreMatched = anchorGenre is not null
            && string.Equals(candidatePrimaryGenre, anchorGenre, StringComparison.OrdinalIgnoreCase);
        var similarityScore = feature.Count + candidate.Count == 0
            ? 0
            : (2 * sharedGenreCount * 1000) / (feature.Count + candidate.Count);

        string? rejectionReason = null;
        if (anchorGenre is null)
        {
            rejectionReason = "the feature has no usable anchor genre";
        }
        else if (!anchorMatched)
        {
            rejectionReason = $"does not contain the anchor genre {anchorGenre}";
        }
        else if (conflictingGenres.Length > 0)
        {
            rejectionReason = $"introduces distinctive genre(s) absent from the feature: {string.Join(", ", conflictingGenres)}";
        }
        else if (candidateOnlyGenres.Length > sharedGenreCount)
        {
            rejectionReason = "has more unmatched genres than shared genres";
        }
        else if (!primaryGenreMatched && sharedGenreCount < 2)
        {
            rejectionReason = $"uses {anchorGenre} only as a lone secondary overlap";
        }

        return new GenreMatchEvaluation(
            rejectionReason is null,
            anchorMatched,
            primaryGenreMatched,
            anchorGenre,
            candidatePrimaryGenre,
            sharedGenreCount,
            feature.Count,
            candidate.Count,
            candidateOnlyGenres.Length,
            similarityScore,
            conflictingGenres,
            rejectionReason);
    }

    private static List<string> CanonicalList(IEnumerable<string> genres) =>
        genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(GenreTools.Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed record GenreMatchEvaluation(
    bool IsEligible,
    bool AnchorMatched,
    bool PrimaryGenreMatched,
    string? AnchorGenre,
    string? CandidatePrimaryGenre,
    int SharedGenreCount,
    int FeatureGenreCount,
    int CandidateGenreCount,
    int CandidateOnlyGenreCount,
    int SimilarityScore,
    IReadOnlyList<string> ConflictingGenres,
    string? RejectionReason);
