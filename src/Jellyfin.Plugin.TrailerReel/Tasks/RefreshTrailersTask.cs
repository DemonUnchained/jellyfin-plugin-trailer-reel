using Jellyfin.Plugin.TrailerReel.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TrailerReel.Tasks;

public sealed class RefreshTrailersTask : IScheduledTask
{
    private readonly TrailerCatalogRefreshService _refreshService;

    public RefreshTrailersTask(TrailerCatalogRefreshService refreshService)
    {
        _refreshService = refreshService;
    }

    public string Name => "Download/refresh Trailer Reel";

    public string Key => "TrailerReelRefresh";

    public string Description =>
        "Finds genres in the local movie library, discovers recent and upcoming U.S. movies, and maintains the local 1080p trailer catalog.";

    public string Category => "Trailer Reel";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _refreshService.RefreshAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(6).Ticks,
        };
    }
}
