namespace Sim.Biomes;

/// <summary>
/// Assigns the dominant biome to a hex using the agreed chain (design doc §2.2):
///
///   1. largest area share (a 51% majority is enough)
///   2. majority of the direct neighbours (6, or 5 on a pentagon)
///   3. distance-based: the tied biome whose nearest occurrence is closest
///   4. deterministic fixed priority (then ordinal id as a last resort)
/// </summary>
public static class DominantBiomeResolver
{
    /// <summary>Shares within this tolerance of the maximum are treated as tied.</summary>
    public const double ShareEpsilon = 1e-9;

    /// <param name="shares">Biome -&gt; fraction of the hex area it covers (0..1).</param>
    /// <param name="neighborBiomes">Dominant biomes of the direct neighbours.</param>
    /// <param name="distanceToNearest">
    /// Optional. Given a candidate biome, the distance to the nearest hex whose dominant
    /// biome is that candidate. Used for the distance-based tie-break.
    /// </param>
    /// <param name="priority">Optional deterministic ordering used as the final fallback.</param>
    public static Biome Resolve(
        IReadOnlyDictionary<Biome, double> shares,
        IReadOnlyList<Biome> neighborBiomes,
        Func<Biome, double?>? distanceToNearest = null,
        IReadOnlyList<Biome>? priority = null)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(neighborBiomes);

        if (shares.Count == 0)
            throw new ArgumentException("At least one biome share is required.", nameof(shares));

        var candidates = TopCandidates(shares);
        if (candidates.Count == 1)
            return candidates[0];

        var byNeighbors = WinnerByCount(candidates, neighborBiomes);
        if (byNeighbors is not null)
            return byNeighbors;

        if (distanceToNearest is not null)
        {
            var byDistance = WinnerByDistance(candidates, distanceToNearest);
            if (byDistance is not null)
                return byDistance;
        }

        return WinnerByPriority(candidates, priority);
    }

    private static List<Biome> TopCandidates(IReadOnlyDictionary<Biome, double> shares)
    {
        var max = shares.Values.Max();
        return shares
            .Where(pair => max - pair.Value <= ShareEpsilon)
            .Select(pair => pair.Key)
            .ToList();
    }

    private static Biome? WinnerByCount(IReadOnlyList<Biome> candidates, IReadOnlyList<Biome> neighborBiomes)
    {
        var counts = candidates
            .Select(candidate => (Biome: candidate, Count: neighborBiomes.Count(n => n == candidate)))
            .ToList();

        var max = counts.Max(entry => entry.Count);
        if (max == 0)
            return null;

        var winners = counts.Where(entry => entry.Count == max).Select(entry => entry.Biome).ToList();
        return winners.Count == 1 ? winners[0] : null;
    }

    private static Biome? WinnerByDistance(IReadOnlyList<Biome> candidates, Func<Biome, double?> distanceToNearest)
    {
        var distances = candidates
            .Select(candidate => (Biome: candidate, Distance: distanceToNearest(candidate)))
            .Where(entry => entry.Distance.HasValue)
            .ToList();

        if (distances.Count == 0)
            return null;

        var min = distances.Min(entry => entry.Distance!.Value);
        var winners = distances
            .Where(entry => Math.Abs(entry.Distance!.Value - min) <= ShareEpsilon)
            .Select(entry => entry.Biome)
            .ToList();

        return winners.Count == 1 ? winners[0] : null;
    }

    private static Biome WinnerByPriority(IReadOnlyList<Biome> candidates, IReadOnlyList<Biome>? priority)
    {
        if (priority is not null)
        {
            foreach (var biome in priority)
            {
                if (candidates.Contains(biome))
                    return biome;
            }
        }

        // Deterministic last resort: ordinal ordering by id.
        return candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).First();
    }
}
