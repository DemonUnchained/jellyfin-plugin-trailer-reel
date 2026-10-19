namespace Jellyfin.Plugin.TrailerReel.Services;

public static class PlaybackStockDiagnostics
{
    public static string ExplainShortage(
        string? anchorGenre, int anchorMatches, int compatible, int present, int indexed,
        int unwatched, int requested, bool avoidRepeats)
    {
        if (anchorGenre is null)
        {
            return "the feature has no usable anchor genre";
        }

        if (anchorMatches == 0)
        {
            return "the selected pool has no trailers carrying the anchor genre";
        }

        if (compatible == 0)
        {
            return "all anchor matches fail the strict genre-profile check";
        }

        if (present == 0)
        {
            return "compatible catalog entries have no media files present";
        }

        if (indexed == 0)
        {
            return "compatible files have not been indexed by Jellyfin";
        }

        if (avoidRepeats && unwatched == 0)
        {
            return "this user has already watched every indexed compatible trailer";
        }

        if ((avoidRepeats ? unwatched : indexed) >= requested)
        {
            return "some selected trailers could not persist their per-user watched state";
        }

        return "fewer usable compatible trailers remain than requested (after file, indexing and watched checks)";
    }
}
