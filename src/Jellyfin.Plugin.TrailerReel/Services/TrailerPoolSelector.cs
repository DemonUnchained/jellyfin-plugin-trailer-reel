using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public static class TrailerPoolSelector
{
    public static IReadOnlyList<TrailerEntry> Select(
        IEnumerable<TrailerEntry> trailers,
        bool animeFeature) =>
        trailers
            .Where(entry => entry.Pool == (animeFeature ? TrailerPool.Anime : TrailerPool.Regular))
            .ToList();
}
