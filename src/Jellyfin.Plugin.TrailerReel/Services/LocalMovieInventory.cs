using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class LocalMovieInventory
{
    private readonly HashSet<int> _tmdbMovieIds;
    private readonly HashSet<string> _fallbackTitleYearKeys;

    public LocalMovieInventory(
        IEnumerable<string> genres,
        IEnumerable<int> tmdbMovieIds,
        IEnumerable<(string Title, int Year)> fallbackTitleYears)
    {
        Genres = genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(GenreTools.Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _tmdbMovieIds = tmdbMovieIds.Where(id => id > 0).ToHashSet();
        _fallbackTitleYearKeys = fallbackTitleYears
            .Where(value => !string.IsNullOrWhiteSpace(value.Title) && value.Year > 0)
            .Select(value => CreateTitleYearKey(value.Title, value.Year))
            .ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Genres { get; }

    public int TmdbIdCount => _tmdbMovieIds.Count;

    public int FallbackTitleYearCount => _fallbackTitleYearKeys.Count;

    public bool Contains(int tmdbMovieId, string title, int? year)
    {
        if (tmdbMovieId > 0 && _tmdbMovieIds.Contains(tmdbMovieId))
        {
            return true;
        }

        return year is > 0
            && !string.IsNullOrWhiteSpace(title)
            && _fallbackTitleYearKeys.Contains(CreateTitleYearKey(title, year.Value));
    }

    private static string CreateTitleYearKey(string title, int year) =>
        $"{NormalizeTitle(title)}\u001F{year.ToString(CultureInfo.InvariantCulture)}";

    private static string NormalizeTitle(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToUpperInvariant(character));
            }
        }

        return normalized.ToString();
    }
}
