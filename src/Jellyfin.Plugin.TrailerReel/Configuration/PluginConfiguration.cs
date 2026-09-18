using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TrailerReel.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public string TmdbReadAccessToken { get; set; } = string.Empty;

    public string TrailerFolderPath { get; set; } = "/trailers";

    public string YtDlpPath { get; set; } = "/config/trailer-tools/yt-dlp";

    public string? YtDlpConfigPath { get; set; }

    public int MaxTrailers { get; set; } = 100;

    public bool EnableAnimeMovieTrailers { get; set; } = true;

    public string AnimeMovieLibraryName { get; set; } = "Anime Movies";

    public int MaxAnimeTrailers { get; set; } = 30;

    public int MonthsBack { get; set; } = 2;

    public int MonthsAhead { get; set; } = 6;

    public int TrailersBeforeMovie { get; set; } = 3;

    public bool RequireExact1080p { get; set; } = true;

    public bool OfficialTrailersOnly { get; set; } = true;

    public bool DeleteExpiredManagedTrailers { get; set; } = true;

    public bool SkipWhenResuming { get; set; } = true;

    public bool AvoidRepeatTrailersPerUser { get; set; } = true;

    public string Region { get; set; } = "US";

    public string Language { get; set; } = "en-US";
}
