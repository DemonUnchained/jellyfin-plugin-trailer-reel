using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public sealed record TrailerAcquisitionCandidate(TrailerEntry Entry, double Popularity);

/// <summary>Acquires replacements before committing any removal from a full catalog.</summary>
public static class TrailerPoolReplenisher
{
    public static async Task ReplenishAsync(
        TrailerCatalog catalog,
        TrailerPool pool,
        GenreCoveragePlanner planner,
        IReadOnlyList<TrailerAcquisitionCandidate> candidates,
        bool allowReplacement,
        Func<TrailerEntry, CancellationToken, Task<bool>> acquire,
        Func<CancellationToken, Task> save,
        Action<TrailerEntry> delete,
        Action<TrailerEntry, Exception> failed,
        Action<TrailerEntry, TrailerEntry> replaced,
        CancellationToken cancellationToken)
    {
        var remaining = candidates
            .Where(candidate => candidate.Entry.Pool == pool)
            .Where(candidate => !catalog.Trailers.Any(entry => entry.TmdbMovieId == candidate.Entry.TmdbMovieId))
            .GroupBy(candidate => candidate.Entry.TmdbMovieId)
            .Select(group => group.OrderByDescending(candidate => candidate.Popularity).First()).ToList();
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = catalog.Trailers.Where(entry => entry.Pool == pool).ToList();
            var counts = planner.CountCompatible(current);
            var next = remaining.OrderByDescending(candidate => planner.Gain(candidate.Entry, counts))
                .ThenByDescending(candidate => candidate.Popularity)
                .Select(candidate => (Candidate: candidate, Plan: planner.PlanAddition(current, candidate.Entry, allowReplacement)))
                .FirstOrDefault(value => value.Plan is not null);
            if (next.Plan is null)
            {
                break;
            }

            remaining.Remove(next.Candidate);
            var entry = next.Candidate.Entry;
            try
            {
                if (!await acquire(entry, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed(entry, ex);
                continue;
            }

            var victim = next.Plan.Replaced;
            var victimIndex = victim is null ? -1 : catalog.Trailers.IndexOf(victim);
            if (victimIndex >= 0)
            {
                catalog.Trailers[victimIndex] = entry;
            }
            else
            {
                catalog.Trailers.Add(entry);
            }

            var previousUpdated = catalog.UpdatedUtc;
            catalog.UpdatedUtc = DateTimeOffset.UtcNow;
            try
            {
                await save(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (victimIndex >= 0)
                {
                    catalog.Trailers[victimIndex] = victim!;
                }
                else
                {
                    catalog.Trailers.Remove(entry);
                }

                catalog.UpdatedUtc = previousUpdated;
                delete(entry);
                throw;
            }

            if (victim is not null)
            {
                delete(victim);
                replaced(victim, entry);
            }
        }
    }
}
