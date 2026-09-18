using System.Diagnostics;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed class YtDlpDownloader
{
    private readonly ILogger<YtDlpDownloader> _logger;

    public YtDlpDownloader(ILogger<YtDlpDownloader> logger)
    {
        _logger = logger;
    }

    public async Task<string> DownloadAsync(
        int tmdbMovieId,
        string movieName,
        int? year,
        string youtubeKey,
        string destinationFolder,
        PluginConfiguration config,
        IReadOnlyCollection<string> reservedFileNames,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(config.YtDlpPath))
        {
            throw new FileNotFoundException("The configured yt-dlp executable was not found.", config.YtDlpPath);
        }

        Directory.CreateDirectory(destinationFolder);
        var incomingRoot = Path.Combine(destinationFolder, ".incoming");
        var workFolder = Path.Combine(incomingRoot, $"{tmdbMovieId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workFolder);

        try
        {
            var outputTemplate = Path.Combine(workFolder, "download.%(ext)s");
            var startInfo = new ProcessStartInfo
            {
                FileName = config.YtDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(config.YtDlpConfigPath))
            {
                startInfo.ArgumentList.Add("--config-locations");
                startInfo.ArgumentList.Add(config.YtDlpConfigPath);
            }

            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("after_move:filepath");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add(config.RequireExact1080p
                ? "bv*[height=1080]+ba/b[height=1080]"
                : "bv*[height<=1080]+ba/b[height<=1080]");
            startInfo.ArgumentList.Add("--merge-output-format");
            startInfo.ArgumentList.Add("mp4");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputTemplate);
            startInfo.ArgumentList.Add($"https://www.youtube.com/watch?v={youtubeKey}");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"yt-dlp exited with code {process.ExitCode}: {LastUsefulLine(stderr)}");
            }

            var reportedPath = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(File.Exists);
            var downloadedPath = reportedPath ?? Directory.EnumerateFiles(workFolder).FirstOrDefault();
            if (downloadedPath is null)
            {
                throw new InvalidDataException("yt-dlp completed but did not produce a media file.");
            }

            var extension = Path.GetExtension(downloadedPath);
            var fileName = ChooseFileName(movieName, year, tmdbMovieId, extension, destinationFolder, reservedFileNames);
            var finalPath = Path.Combine(destinationFolder, fileName);
            File.Move(downloadedPath, finalPath, false);
            _logger.LogInformation("Trailer Reel downloaded trailer for {MovieName} as {FileName}", movieName, fileName);
            return fileName;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workFolder))
                {
                    Directory.Delete(workFolder, recursive: true);
                }

                if (Directory.Exists(incomingRoot) && !Directory.EnumerateFileSystemEntries(incomingRoot).Any())
                {
                    Directory.Delete(incomingRoot);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Trailer Reel could not remove an incoming download directory");
            }
        }
    }

    public static string ChooseFileName(
        string movieName,
        int? year,
        int tmdbMovieId,
        string extension,
        string destinationFolder,
        IReadOnlyCollection<string> reservedFileNames)
    {
        var safeName = SanitizeFileName(movieName);
        var preferred = $"{safeName}-trailer{extension}";
        if (IsAvailable(preferred, destinationFolder, reservedFileNames))
        {
            return preferred;
        }

        if (year.HasValue)
        {
            var withYear = $"{safeName} ({year.Value})-trailer{extension}";
            if (IsAvailable(withYear, destinationFolder, reservedFileNames))
            {
                return withYear;
            }
        }

        return $"{safeName} [tmdb-{tmdbMovieId}]-trailer{extension}";
    }

    public static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? ' ' : character).ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        cleaned = cleaned.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown Movie" : cleaned;
    }

    private static bool IsAvailable(
        string fileName,
        string destinationFolder,
        IReadOnlyCollection<string> reservedFileNames) =>
        !reservedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(destinationFolder, fileName));

    private static string LastUsefulLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "no error details were returned";
}
