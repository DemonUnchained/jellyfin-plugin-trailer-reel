using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public static class AnimeMovieRules
{
    public const int AnimationGenreId = 16;

    public static bool IsAnimeCandidate(MovieCandidate candidate) =>
        candidate.GenreIds.Contains(AnimationGenreId)
        && (string.Equals(candidate.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase)
            || candidate.OriginCountryCodes.Contains("JP", StringComparer.OrdinalIgnoreCase));

    public static bool LibraryNameMatches(string? actualName, string? configuredName) =>
        !string.IsNullOrWhiteSpace(actualName)
        && !string.IsNullOrWhiteSpace(configuredName)
        && string.Equals(actualName.Trim(), configuredName.Trim(), StringComparison.OrdinalIgnoreCase);
}
