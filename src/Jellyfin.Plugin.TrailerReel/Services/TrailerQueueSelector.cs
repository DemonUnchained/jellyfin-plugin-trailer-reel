namespace Jellyfin.Plugin.TrailerReel.Services;

public static class TrailerQueueSelector
{
    public static IReadOnlyList<T> Select<T>(
        IEnumerable<T> candidates,
        Func<T, bool> isWatched,
        int maximum,
        bool avoidRepeats)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(isWatched);

        if (maximum <= 0)
        {
            return [];
        }

        return candidates
            .Where(candidate => !avoidRepeats || !isWatched(candidate))
            .Take(maximum)
            .ToList();
    }
}
