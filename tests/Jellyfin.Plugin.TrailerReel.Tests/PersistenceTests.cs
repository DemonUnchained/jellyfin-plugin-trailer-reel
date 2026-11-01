using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.TrailerReel;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Jellyfin.Plugin.TrailerReel.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PersistenceTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "trailer-reel-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckCatalogReadsAsync(Path.Combine(root, "reads"), check);
            await CheckRefreshCommitAsync(Path.Combine(root, "refresh"), check);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckCatalogReadsAsync(string folder, Action<string, bool> check)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, CatalogStore.CatalogFileName);
        var store = new CatalogStore(NullLogger<CatalogStore>.Instance);
        foreach (var invalid in new[] { "{truncated", "null", """{"trailers":null}""" })
        {
            await File.WriteAllTextAsync(path, invalid);
            var rejected = false;
            try
            {
                await store.LoadAsync(folder, CancellationToken.None);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                rejected = true;
            }

            check("invalid catalog reads fail without replacing the stored catalog",
                rejected && await File.ReadAllTextAsync(path) == invalid);
        }

        await File.WriteAllTextAsync(path, """{"trailers":[{"tmdbMovieId":77,"movieName":"Recovered"}]}""");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rejected = false;
            try
            {
                await store.LoadAsync(folder, CancellationToken.None);
            }
            catch (IOException)
            {
                rejected = true;
            }

            check("catalog read I/O failures propagate instead of caching empty stock", rejected);
        }

        var recovered = await store.LoadAsync(folder, CancellationToken.None);
        check("a repaired catalog is retried instead of returning a cached empty catalog",
            recovered.Trailers.Single().TmdbMovieId == 77);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var propagated = false;
        try
        {
            await store.LoadAsync(folder, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            propagated = true;
        }

        check("catalog load cancellation propagates and leaves cached stock intact",
            propagated && (await store.LoadAsync(folder, CancellationToken.None)).Trailers.Single().TmdbMovieId == 77);
        var empty = await new CatalogStore(NullLogger<CatalogStore>.Instance)
            .LoadAsync(Path.Combine(folder, "new-install"), CancellationToken.None);
        check("a genuinely missing catalog still supports a new installation", empty.Trailers.Count == 0);
    }

    private static async Task CheckRefreshCommitAsync(string folder, Action<string, bool> check)
    {
        Directory.CreateDirectory(folder);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expired = new TrailerEntry
        {
            TmdbMovieId = 1, MovieName = "Expired", FileName = "Expired-trailer.mp4",
            ReleaseDate = today.AddYears(-1), Genres = ["Horror"],
        };
        var retained = new TrailerEntry
        {
            TmdbMovieId = 2, MovieName = "Retained", FileName = "Retained-trailer.mp4",
            ReleaseDate = today, Genres = ["Horror"],
        };
        foreach (var entry in new[] { expired, retained })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, entry.FileName), "synthetic media fixture");
        }

        var config = new PluginConfiguration { TrailerFolderPath = folder, TmdbReadAccessToken = "synthetic-test-token" };
        var paths = PersistenceProxy.Create<IApplicationPaths>((method, _) => method.ReturnType == typeof(string) ? folder : null);
        var serializer = PersistenceProxy.Create<IXmlSerializer>((method, _) => method.Name == "DeserializeFromFile" ? config : null);
        _ = new Plugin(paths, serializer);
        var feature = new Movie { Name = "Feature", Genres = ["Horror"], Path = Path.Combine(Path.GetDirectoryName(folder)!, "feature.mkv") };
        var virtualFolder = new VirtualFolderInfo
        {
            Name = HiddenTrailerLibrary.LibraryName, ItemId = Guid.NewGuid().ToString(), Locations = [folder],
        };
        var library = PersistenceProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            "GetVirtualFolders" => PersistenceProxy.Collection(method.ReturnType, [virtualFolder]),
            "GetItemList" => PersistenceProxy.Collection(method.ReturnType, [feature]),
            "GetCollectionFolders" => PersistenceProxy.Collection(method.ReturnType, []),
            _ => null,
        });
        var users = PersistenceProxy.Create<IUserManager>((method, _) => method.Name == "GetUsers"
            ? PersistenceProxy.Collection(method.ReturnType, []) : null);
        var hidden = new HiddenTrailerLibrary(library, users, NullLogger<HiddenTrailerLibrary>.Instance);
        var store = new CatalogStore(NullLogger<CatalogStore>.Instance);
        await store.SaveAsync(folder, new TrailerCatalog { Trailers = [expired, retained] }, CancellationToken.None);
        var path = Path.Combine(folder, CatalogStore.CatalogFileName);
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var client = new HttpClient(new EmptyDiscoveryHandler());
        var service = new TrailerCatalogRefreshService(new LocalGenreService(library), new TmdbClient(client),
            new YtDlpDownloader(NullLogger<YtDlpDownloader>.Instance), store, hidden,
            NullLogger<TrailerCatalogRefreshService>.Instance);

        // A directory at the temporary file path forces a real cross-platform save failure.
        var blockedWrite = path + ".tmp";
        Directory.CreateDirectory(blockedWrite);
        var failed = false;
        try
        {
            await service.RefreshAsync(new Progress<double>(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = true;
        }

        check("refresh save failure preserves both removed and retained managed media",
            failed && File.Exists(Path.Combine(folder, expired.FileName)) && File.Exists(Path.Combine(folder, retained.FileName)));
        var afterFailureBytes = await File.ReadAllBytesAsync(path);
        check("refresh save failure preserves the persisted catalog bytes",
            originalBytes.SequenceEqual(afterFailureBytes));
        check("refresh save failure preserves the cached catalog stock",
            (await store.LoadAsync(folder, CancellationToken.None)).Trailers.Count == 2);

        Directory.Delete(blockedWrite);
        await service.RefreshAsync(new Progress<double>(), CancellationToken.None);
        var reloaded = await new CatalogStore(NullLogger<CatalogStore>.Instance).LoadAsync(folder, CancellationToken.None);
        check("successful refresh commits cleanup while retaining eligible media",
            reloaded.Trailers.Single().TmdbMovieId == retained.TmdbMovieId
                && !File.Exists(Path.Combine(folder, expired.FileName))
                && File.Exists(Path.Combine(folder, retained.FileName)));
    }

    private sealed class EmptyDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("genre/movie/list", StringComparison.Ordinal)
                ? """{"genres":[{"id":27,"name":"Horror"}]}""" : """{"total_pages":1,"results":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}

public class PersistenceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var result = Create<T, PersistenceProxy>();
        ((PersistenceProxy)(object)result).Handler = handler;
        return result;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);

    public static object Collection(Type type, object[] items)
    {
        var element = type.IsArray ? type.GetElementType()! : type.GenericTypeArguments[0];
        var array = Array.CreateInstance(element, items.Length);
        for (var i = 0; i < items.Length; i++) array.SetValue(items[i], i);
        if (type.IsAssignableFrom(array.GetType())) return array;
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        foreach (var item in items) list.Add(item);
        return list;
    }
}
