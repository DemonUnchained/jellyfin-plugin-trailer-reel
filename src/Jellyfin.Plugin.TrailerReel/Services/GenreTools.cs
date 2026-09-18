namespace Jellyfin.Plugin.TrailerReel.Services;

public static class GenreTools
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sci-Fi"] = "Science Fiction",
        ["Sci Fi"] = "Science Fiction",
        ["Science-Fiction"] = "Science Fiction",
        ["Anime"] = "Animation",
        ["Action & Adventure"] = "Action",
        ["Kids"] = "Family",
        ["Children"] = "Family",
        ["Musical"] = "Music",
        ["Suspense"] = "Thriller",
        ["War & Politics"] = "War",
    };

    public static string Canonicalize(string genre)
    {
        var trimmed = genre.Trim();
        return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    public static HashSet<string> CanonicalSet(IEnumerable<string> genres) =>
        genres
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(Canonicalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool HasOverlap(IEnumerable<string> first, IEnumerable<string> second)
    {
        var set = CanonicalSet(first);
        return second.Any(g => set.Contains(Canonicalize(g)));
    }
}
