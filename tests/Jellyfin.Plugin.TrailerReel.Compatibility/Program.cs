using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TrailerReel;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Jellyfin.Plugin.TrailerReel.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

// An isolated contract host with synthetic media and in-memory Jellyfin services.
// It does not run a real Jellyfin server or validate media streaming/decoding.
var checks = 0;
void Check(string name, bool result)
{
    if (!result) throw new InvalidOperationException("FAILED: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin-meta.json")));
var expectedVersion = Version.Parse(manifest.RootElement.GetProperty("version").GetString()!);
var hostVersion = Version.Parse(args.SingleOrDefault() ?? "12.4.0.0");
var assembly = typeof(Plugin).Assembly;
Console.WriteLine($"Plugin DLL SHA-256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");
Check("plugin assembly matches its manifest", assembly.GetName().Version == expectedVersion);
Check("host Controller matches requested Jellyfin version", typeof(IIntroProvider).Assembly.GetName().Version == hostVersion);
Check("host Model matches requested Jellyfin version", typeof(UserItemData).Assembly.GetName().Version == hostVersion);
Check("host Naming matches requested Jellyfin version", typeof(VideoResolver).Assembly.GetName().Version == hostVersion);
var prepared = 0;
foreach (var type in assembly.GetTypes())
{
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
        .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
    {
        if (method.ContainsGenericParameters || method.GetMethodBody() is null) continue;
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
        prepared++;
    }
}
Check($"all {prepared} closed plugin methods JIT against the host", prepared > 100);

var root = Path.Combine(Path.GetTempPath(), "trailer-reel-compatibility-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var folder = Path.Combine(root, "trailers");
    Directory.CreateDirectory(folder);
    var config = new PluginConfiguration { TrailerFolderPath = folder };
    var paths = Stub.Create<IApplicationPaths>((m, _) => m.ReturnType == typeof(string) ? root : Stub.Default(m.ReturnType));
    var serializer = Stub.Create<IXmlSerializer>((m, _) => m.Name == "DeserializeFromFile" ? config : Stub.Default(m.ReturnType));
    var plugin = new Plugin(paths, serializer);
    Check("plugin construction and saved configuration load", ReferenceEquals(plugin.Configuration, config));
    Check("configuration GUID remains stable", plugin.Id == Guid.Parse("c124b60d-fb89-47bb-9c93-fc2649152a75"));
    var page = plugin.GetPages().Single();
    using (var stream = assembly.GetManifestResourceStream(page.EmbeddedResourcePath))
        Check("dashboard configuration resource resolves", stream is not null);

    var feature = new Movie { Id = Guid.NewGuid(), Name = "Feature", Genres = ["Horror", "Thriller"], Path = Path.Combine(root, "feature.mkv") };
    // The fixture reads only user IDs. No database connection or host credentials exist.
    var user = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
    user.Id = Guid.NewGuid();
    var secondUser = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
    secondUser.Id = Guid.NewGuid();
    var libraryItems = new List<BaseItem>();
    var catalog = new TrailerCatalog();
    for (var i = 0; i < 3; i++)
    {
        var entry = new TrailerEntry { TmdbMovieId = i + 1, MovieName = $"Trailer {i}", FileName = $"Trailer {i}-trailer.mp4", Genres = ["Horror", "Thriller"] };
        File.WriteAllText(Path.Combine(folder, entry.FileName), "synthetic media fixture");
        catalog.Trailers.Add(entry);
        libraryItems.Add(new Video { Id = Guid.NewGuid(), Name = entry.MovieName, Path = TrailerIndexStore.GetPlaybackPath(folder, entry.FileName) });
    }
    var virtualFolder = new VirtualFolderInfo { Name = HiddenTrailerLibrary.LibraryName, ItemId = Guid.NewGuid().ToString(), Locations = [folder] };
    var lib = Stub.Create<ILibraryManager>((m, arguments) => m.Name switch
    {
        "GetVirtualFolders" => Stub.Collection(m.ReturnType, [virtualFolder]),
        "GetCollectionFolders" => Stub.Collection(m.ReturnType, []),
        "GetItemList" => Stub.Collection(m.ReturnType, [feature]),
        "FindByPath" => libraryItems.SingleOrDefault(item => item.Path == (string)arguments![0]!),
        _ => Stub.Default(m.ReturnType)
    });
    var users = Stub.Create<IUserManager>((m, _) => m.Name == "GetUsers" ? Stub.Collection(m.ReturnType, []) : Stub.Default(m.ReturnType));
    var state = new ConcurrentDictionary<(Guid, Guid), UserItemData>();
    var saves = 0;
    Guid? blockedUserId = null;
    var blockedReadClaimed = 0;
    var blockedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var releaseRead = new ManualResetEventSlim();
    var userData = Stub.Create<IUserDataManager>((m, arguments) =>
    {
        var targetUser = arguments is { Length: > 0 } ? arguments[0] as User : null;
        var targetItem = arguments is { Length: > 1 } ? arguments[1] as BaseItem : null;
        if (m.Name == "GetUserData")
        {
            var snapshot = state.GetValueOrDefault((targetUser!.Id, targetItem!.Id));
            if (targetUser.Id == blockedUserId && libraryItems.Any(item => item.Id == targetItem.Id)
                && Interlocked.CompareExchange(ref blockedReadClaimed, 1, 0) == 0)
            {
                blockedRead.SetResult();
                if (!releaseRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent lookup fixture was not released.");
            }

            return snapshot;
        }
        if (m.Name == "SaveUserData" && arguments![2] is UpdateUserItemDataDto dto)
        {
            state[(targetUser!.Id, targetItem!.Id)] = new UserItemData { Key = targetItem.Id.ToString(), Played = dto.Played ?? false, PlayCount = dto.PlayCount ?? 0 };
            Interlocked.Increment(ref saves);
            return null;
        }
        return Stub.Default(m.ReturnType);
    });

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(lib);
    services.AddSingleton(users);
    services.AddSingleton(userData);
    new PluginServiceRegistrator().RegisterServices(services, null!);
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    Check("service registrations construct with host contracts", provider.GetRequiredService<IScheduledTask>().Key == "TrailerReelRefresh");
    var store = provider.GetRequiredService<CatalogStore>();
    await store.SaveAsync(folder, catalog, CancellationToken.None);
    await provider.GetRequiredService<HiddenTrailerLibrary>().RefreshAsync(folder, catalog.Trailers, CancellationToken.None);
    Check("index aliases exist and resolve to originals", libraryItems.All(item => File.Exists(item.Path) && new FileInfo(item.Path).LinkTarget is not null));
    var naming = new NamingOptions();
    Check("host recognizes the source as a trailer extra and its index alias as a standalone video",
        VideoResolver.Resolve(catalog.Trailers[0].FileName, isDirectory: false, naming)?.ExtraType == ExtraType.Trailer
            && VideoResolver.Resolve(Path.GetFileName(libraryItems[0].Path), isDirectory: false, naming) is { ExtraType: null });
    var introProvider = provider.GetRequiredService<IIntroProvider>();
    var intros = (await introProvider.GetIntros(feature, user)).ToArray();
    Check("binary returns three indexed intro item IDs", intros.Length == 3 && intros.All(intro => libraryItems.Any(item => item.Id == intro.ItemId && item.Path == intro.Path)));
    Check("watched history commits through host API", saves == 3 && libraryItems.All(item => state[(user.Id, item.Id)].Played));
    var cached = (await introProvider.GetIntros(feature, user)).ToArray();
    Check("duplicate lookup retains queue without duplicate watched writes", saves == 3 && cached.Select(x => x.ItemId).SequenceEqual(intros.Select(x => x.ItemId)));
    feature.Id = Guid.NewGuid();
    Check("watched trailers are excluded from a new launch", !(await introProvider.GetIntros(feature, user)).Any());
    Check("another user's watched history is independent", (await introProvider.GetIntros(feature, secondUser)).Count() == 3 && saves == 6);
    feature.Id = Guid.NewGuid();
    state[(secondUser.Id, feature.Id)] = new UserItemData { Key = feature.Id.ToString(), PlaybackPositionTicks = 1000, Played = false };
    Check("resume suppression uses host user data", !(await introProvider.GetIntros(feature, secondUser)).Any());
    async Task<IntroInfo[][]> ConcurrentLookupsAsync(bool sameFeature)
    {
        var concurrentUser = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
        concurrentUser.Id = Guid.NewGuid();
        blockedUserId = concurrentUser.Id;
        blockedReadClaimed = 0;
        blockedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseRead.Reset();
        Movie NewFeature() => new() { Id = Guid.NewGuid(), Name = "Concurrent feature", Genres = ["Horror", "Thriller"], Path = feature.Path };
        var firstFeature = NewFeature();
        var secondFeature = sameFeature ? firstFeature : NewFeature();
        var first = Task.Run(() => introProvider.GetIntros(firstFeature, concurrentUser));
        var invoked = new TaskCompletionSource<Task<IEnumerable<IntroInfo>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IEnumerable<IntroInfo>> second;
        try
        {
            await blockedRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // With serialization, invocation returns an incomplete Task waiting for the first
            // queue. Without it, the second lookup consumes stock while the first holds a stale read.
            _ = Task.Run(() =>
            {
                try { invoked.SetResult(introProvider.GetIntros(secondFeature, concurrentUser)); }
                catch (Exception ex) { invoked.SetException(ex); }
            });
            second = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseRead.Set();
        }

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        blockedUserId = null;
        return results.Select(queue => queue.ToArray()).ToArray();
    }

    var concurrentLaunches = await ConcurrentLookupsAsync(sameFeature: false);
    Check("concurrent launches for one user do not repeat reserved trailers",
        concurrentLaunches.Sum(queue => queue.Length) == 3
            && concurrentLaunches.SelectMany(queue => queue).Select(intro => intro.ItemId).Distinct().Count() == 3);
    var concurrentDuplicates = await ConcurrentLookupsAsync(sameFeature: true);
    Check("concurrent duplicate lookups retain the same complete queue",
        concurrentDuplicates.All(queue => queue.Length == 3)
            && concurrentDuplicates[0].Select(intro => intro.ItemId).SequenceEqual(concurrentDuplicates[1].Select(intro => intro.ItemId)));
    var corruptFolder = Path.Combine(root, "corrupt-catalog");
    Directory.CreateDirectory(corruptFolder);
    var corruptPath = Path.Combine(corruptFolder, CatalogStore.CatalogFileName);
    await File.WriteAllTextAsync(corruptPath, "{truncated");
    config.TrailerFolderPath = corruptFolder;
    feature.Id = Guid.NewGuid();
    Check("unreadable catalog skips prerolls and preserves the existing file",
        !(await introProvider.GetIntros(feature, user)).Any() && await File.ReadAllTextAsync(corruptPath) == "{truncated");
    config.TrailerFolderPath = folder;
    config.Enabled = false;
    feature.Id = Guid.NewGuid();
    Check("disabled plugin suppresses prerolls", !(await introProvider.GetIntros(feature, user)).Any());
    Check("manifest ABI satisfies the host's minimum-version gate", hostVersion >= Version.Parse(manifest.RootElement.GetProperty("targetAbi").GetString()!));
    Console.WriteLine($"All {checks} binary compatibility checks passed; {prepared} methods prepared.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)result).Handler = handler;
        return result;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static object? Default(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GenericTypeArguments[0]).Invoke(null, [Default(type.GenericTypeArguments[0])]);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
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
