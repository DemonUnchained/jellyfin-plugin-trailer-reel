using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TrailerReel.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class CatalogStore
{
    public const string CatalogFileName = ".trailer-reel-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<CatalogStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TrailerCatalog? _cached;
    private string? _cachedFolder;

    public CatalogStore(ILogger<CatalogStore> logger)
    {
        _logger = logger;
    }

    public async Task<TrailerCatalog> LoadAsync(string folder, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && string.Equals(_cachedFolder, folder, StringComparison.Ordinal))
            {
                return Clone(_cached);
            }

            var path = Path.Combine(folder, CatalogFileName);
            if (!File.Exists(path))
            {
                _cached = new TrailerCatalog();
                _cachedFolder = folder;
                return Clone(_cached);
            }

            await using var stream = File.OpenRead(path);
            _cached = await JsonSerializer.DeserializeAsync<TrailerCatalog>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new TrailerCatalog();
            _cachedFolder = folder;
            return Clone(_cached);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trailer Reel could not read its catalog; starting with an empty catalog");
            _cached = new TrailerCatalog();
            _cachedFolder = folder;
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string folder, TrailerCatalog catalog, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, CatalogFileName);
            var tempPath = path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, true);
            _cached = Clone(catalog);
            _cachedFolder = folder;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TrailerCatalog Clone(TrailerCatalog source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        UpdatedUtc = source.UpdatedUtc,
        WindowStart = source.WindowStart,
        WindowEnd = source.WindowEnd,
        LocalGenres = source.LocalGenres.ToList(),
        Trailers = source.Trailers.Select(entry => new TrailerEntry
        {
            TmdbMovieId = entry.TmdbMovieId,
            MovieName = entry.MovieName,
            Year = entry.Year,
            ReleaseDate = entry.ReleaseDate,
            Genres = entry.Genres.ToList(),
            YoutubeKey = entry.YoutubeKey,
            FileName = entry.FileName,
            DownloadedUtc = entry.DownloadedUtc,
        }).ToList(),
    };
}
