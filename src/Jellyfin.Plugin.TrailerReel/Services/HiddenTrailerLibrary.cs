using Jellyfin.Plugin.TrailerReel.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class HiddenTrailerLibrary
{
    public const string LibraryName = "Trailer Reel (Internal)";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<HiddenTrailerLibrary> _logger;

    public HiddenTrailerLibrary(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<HiddenTrailerLibrary> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task EnsureAsync(string folder, CancellationToken cancellationToken)
    {
        var existing = GetExisting();
        if (existing is not null && !LocationsMatch(existing.Locations ?? [], folder))
        {
            await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: false).ConfigureAwait(false);
            existing = null;
        }

        if (existing is null)
        {
            var options = new LibraryOptions
            {
                Enabled = true,
                EnableRealtimeMonitor = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableLUFSScan = false,
                EnableEmbeddedTitles = false,
                EnableEmbeddedExtrasTitles = false,
                EnablePhotos = false,
                SaveLocalMetadata = false,
                AutomaticRefreshIntervalDays = 0,
                PathInfos = [new MediaPathInfo(folder)],
                DisabledLocalMetadataReaders = [],
                DisabledSubtitleFetchers = [],
                SubtitleFetcherOrder = [],
                DisabledMediaSegmentProviders = [],
                MediaSegmentProviderOrder = [],
            };
            await _libraryManager.AddVirtualFolder(
                LibraryName,
                CollectionTypeOptions.homevideos,
                options,
                refreshLibrary: true).ConfigureAwait(false);
            existing = GetExisting();
        }

        if (existing is not null && Guid.TryParse(existing.ItemId, out var folderId))
        {
            await HideFromUsersAsync(folderId).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task RefreshAsync(
        string trailerFolder,
        IReadOnlyCollection<TrailerEntry> trailers,
        CancellationToken cancellationToken)
    {
        var aliasCount = TrailerIndexStore.Synchronize(trailerFolder, trailers);
        _logger.LogInformation(
            "Trailer Reel prepared {AliasCount} index alias(es) under {IndexDirectory}",
            aliasCount,
            TrailerIndexStore.DirectoryName);

        var root = GetRootFolder();
        if (root is CollectionFolder collectionFolder)
        {
            // CollectionFolder.ValidateChildren is intentionally a no-op in Jellyfin 12.1.
            // Scan its physical folders so files downloaded after the virtual library's
            // initial scan are created as playable Jellyfin items.
            _libraryManager.ClearIgnoreRuleCache();
            try
            {
                foreach (var physicalFolder in collectionFolder.GetPhysicalFolders())
                {
                    await physicalFolder.RefreshMetadata(cancellationToken).ConfigureAwait(false);
                    await physicalFolder.ValidateChildren(new Progress<double>(), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _libraryManager.ClearIgnoreRuleCache();
            }
        }
        else if (root is not null)
        {
            await root.RefreshMetadata(cancellationToken).ConfigureAwait(false);
            await root.ValidateChildren(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        }
    }

    public BaseItem? FindItem(string trailerFolder, string sourceFileName)
    {
        var path = TrailerIndexStore.GetPlaybackPath(trailerFolder, sourceFileName);
        var item = _libraryManager.FindByPath(path, isFolder: false);
        if (item is null)
        {
            return null;
        }

        var existing = GetExisting();
        return existing?.Locations?.Any(location => PathIsUnder(item.Path, location)) == true ? item : null;
    }

    private VirtualFolderInfo? GetExisting() =>
        _libraryManager.GetVirtualFolders()
            .FirstOrDefault(folder => string.Equals(folder.Name, LibraryName, StringComparison.OrdinalIgnoreCase));

    private Folder? GetRootFolder()
    {
        var existing = GetExisting();
        return existing is not null && Guid.TryParse(existing.ItemId, out var id)
            ? _libraryManager.GetItemById(id) as Folder
            : null;
    }

    private async Task HideFromUsersAsync(Guid folderId)
    {
        foreach (var user in _userManager.GetUsers())
        {
            try
            {
                var dto = _userManager.GetUserDto(user);
                var configuration = dto.Configuration;
                configuration.MyMediaExcludes = Add(configuration.MyMediaExcludes, folderId);
                configuration.LatestItemsExcludes = Add(configuration.LatestItemsExcludes, folderId);
                configuration.OrderedViews = Remove(configuration.OrderedViews, folderId);
                await _userManager.UpdateConfigurationAsync(user.Id, configuration).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trailer Reel could not hide its internal library from user {Username}", user.Username);
            }
        }
    }

    private static Guid[] Add(IEnumerable<Guid>? values, Guid value) =>
        (values ?? []).Append(value).Distinct().ToArray();

    private static Guid[] Remove(IEnumerable<Guid>? values, Guid value) =>
        (values ?? []).Where(existing => existing != value).ToArray();

    private static bool LocationsMatch(IEnumerable<string> locations, string expected) =>
        locations.Count() == 1
        && string.Equals(NormalizePath(locations.First()), NormalizePath(expected), StringComparison.OrdinalIgnoreCase);

    private static bool PathIsUnder(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
