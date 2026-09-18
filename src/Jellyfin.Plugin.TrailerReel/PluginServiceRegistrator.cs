using Jellyfin.Plugin.TrailerReel.Services;
using Jellyfin.Plugin.TrailerReel.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TrailerReel;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost serverApplicationHost)
    {
        services.AddSingleton<CatalogStore>();
        services.AddSingleton<LocalGenreService>();
        services.AddSingleton<YtDlpDownloader>();
        services.AddSingleton<HiddenTrailerLibrary>();
        services.AddHttpClient<TmdbClient>();
        services.AddSingleton<TrailerCatalogRefreshService>();
        services.AddSingleton<IIntroProvider, GenreTrailerIntroProvider>();
        services.AddSingleton<IScheduledTask, RefreshTrailersTask>();
    }
}
