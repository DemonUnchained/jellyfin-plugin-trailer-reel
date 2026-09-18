using Jellyfin.Plugin.TrailerReel.Models;

namespace Jellyfin.Plugin.TrailerReel.Services;

public static class CandidateSelector
{
    public static IReadOnlyList<MovieCandidate> RoundRobin(
        IReadOnlyDictionary<int, IReadOnlyList<MovieCandidate>> candidatesByGenre,
        int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var queues = candidatesByGenre
            .OrderBy(pair => pair.Key)
            .Select(pair => new Queue<MovieCandidate>(
                pair.Value.OrderByDescending(candidate => candidate.Popularity)))
            .ToList();
        var seen = new HashSet<int>();
        var selected = new List<MovieCandidate>(limit);

        while (selected.Count < limit && queues.Any(queue => queue.Count > 0))
        {
            foreach (var queue in queues)
            {
                while (queue.Count > 0)
                {
                    var candidate = queue.Dequeue();
                    if (!seen.Add(candidate.Id))
                    {
                        continue;
                    }

                    selected.Add(candidate);
                    break;
                }

                if (selected.Count >= limit)
                {
                    break;
                }
            }
        }

        return selected;
    }
}
