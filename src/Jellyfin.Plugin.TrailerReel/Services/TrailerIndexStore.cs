using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public static class TrailerIndexStore
{
    public const string DirectoryName = "TrailerReelIndex";

    private const string FileNameMarker = ".trailer-reel-index";

    public static string GetPlaybackPath(string trailerFolder, string sourceFileName) =>
        Path.Combine(trailerFolder, DirectoryName, GetIndexFileName(sourceFileName));

    public static string GetIndexFileName(string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName)
            || !string.Equals(Path.GetFileName(sourceFileName), sourceFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Trailer filename must be a simple filename.", nameof(sourceFileName));
        }

        var extension = Path.GetExtension(sourceFileName);
        var stem = Path.GetFileNameWithoutExtension(sourceFileName);
        return $"{stem}{FileNameMarker}{extension}";
    }

    public static int Synchronize(string trailerFolder, IReadOnlyCollection<TrailerEntry> trailers)
    {
        var indexFolder = Path.Combine(trailerFolder, DirectoryName);
        Directory.CreateDirectory(indexFolder);
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var trailer in trailers)
        {
            var sourceFileName = trailer.FileName;
            if (string.IsNullOrWhiteSpace(sourceFileName)
                || !string.Equals(Path.GetFileName(sourceFileName), sourceFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var sourcePath = Path.Combine(trailerFolder, sourceFileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var indexPath = GetPlaybackPath(trailerFolder, sourceFileName);
            expectedPaths.Add(indexPath);
            EnsureRelativeSymbolicLink(indexPath, Path.Combine("..", sourceFileName));
        }

        foreach (var path in Directory.EnumerateFiles(indexFolder, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsManagedIndexFile(path) && !expectedPaths.Contains(path))
            {
                File.Delete(path);
            }
        }

        return expectedPaths.Count;
    }

    private static void EnsureRelativeSymbolicLink(string indexPath, string relativeTarget)
    {
        var existingTarget = new FileInfo(indexPath).LinkTarget;
        if (string.Equals(existingTarget, relativeTarget, StringComparison.Ordinal))
        {
            return;
        }

        var temporaryPath = $"{indexPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.CreateSymbolicLink(temporaryPath, relativeTarget);
            File.Move(temporaryPath, indexPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool IsManagedIndexFile(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith(FileNameMarker, StringComparison.Ordinal);
}
