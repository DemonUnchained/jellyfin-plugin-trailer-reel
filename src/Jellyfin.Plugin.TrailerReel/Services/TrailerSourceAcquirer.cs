using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed record AcquiredTrailerSource(TrailerVideo Video, string FileName);

public static class TrailerSourceAcquirer
{
    public const int MaximumUploadsPerMovie = 3;

    public static async Task<AcquiredTrailerSource?> TryAcquireAsync(
        IReadOnlyList<TrailerVideo> candidates,
        Func<TrailerVideo, CancellationToken, Task<string>> download,
        Action<TrailerVideo, TrailerSourceRejectedException> rejected,
        CancellationToken cancellationToken)
    {
        foreach (var video in candidates.DistinctBy(video => video.Key).Take(MaximumUploadsPerMovie))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileName = await download(video, cancellationToken).ConfigureAwait(false);
                return new AcquiredTrailerSource(video, fileName);
            }
            catch (TrailerSourceRejectedException ex)
            {
                rejected(video, ex);
            }
        }

        return null;
    }
}
