using Sim.Biomes;

namespace Sim.Movement;

/// <summary>One leg of a planned route: the cost of entering <see cref="To"/>.</summary>
public readonly record struct RouteLeg(int From, int To, double DistanceKm, double SpeedMultiplier, double Hours);

/// <summary>A planned route across a grid of cells identified by integer ids.</summary>
public sealed record RoutePlan(IReadOnlyList<RouteLeg> Legs, double TotalDistanceKm, double TotalHours)
{
    public int LegCount => Legs.Count;

    public int Start => Legs.Count == 0 ? -1 : Legs[0].From;

    public int Goal => Legs.Count == 0 ? -1 : Legs[^1].To;
}

/// <summary>
/// Cheapest-time route between two cells (design doc §2.3).
///
/// The grid is passed in as plain integer ids plus delegates, so the planner is
/// engine-free and testable without H3. Edge cost is the travel time of entering
/// a cell: distance / (base speed × speed multiplier), where the multiplier comes
/// from the terrain-rank difference and the <b>destination</b> cell's crater.
/// </summary>
public static class RoutePlanner
{
    /// <summary>
    /// Plans the fastest route from <paramref name="start"/> to <paramref name="goal"/>,
    /// or returns <see langword="null"/> when the goal cannot be reached.
    /// </summary>
    /// <param name="neighbours">Adjacency list, indexed by cell id.</param>
    /// <param name="biomeOf">Biome of a cell (drives the uphill/downhill modifier).</param>
    /// <param name="craterOf">Crater overlay of a cell (penalty applies on entry).</param>
    /// <param name="distanceKm">Centre-to-centre distance between two adjacent cells.</param>
    public static RoutePlan? Plan(
        int start,
        int goal,
        IReadOnlyList<int[]> neighbours,
        Func<int, Biome> biomeOf,
        Func<int, CraterKind> craterOf,
        Func<int, int, double> distanceKm,
        double baseSpeedKmh)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(biomeOf);
        ArgumentNullException.ThrowIfNull(craterOf);
        ArgumentNullException.ThrowIfNull(distanceKm);

        if (baseSpeedKmh <= 0)
            throw new ArgumentOutOfRangeException(nameof(baseSpeedKmh), "Base speed must be positive.");

        var count = neighbours.Count;
        if ((uint)start >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(start), start, $"Grid has {count} cells.");
        if ((uint)goal >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(goal), goal, $"Grid has {count} cells.");

        if (start == goal)
            return new RoutePlan(Array.Empty<RouteLeg>(), 0.0, 0.0);

        // An admissible heuristic: nobody moves faster than a downhill run.
        var bestMultiplier = MovementModel.DownhillSpeedMultiplier;

        var best = new double[count];
        var cameFrom = new int[count];
        var closed = new bool[count];
        Array.Fill(best, double.PositiveInfinity);
        Array.Fill(cameFrom, -1);

        var open = new PriorityQueue<int, double>();
        best[start] = 0.0;
        open.Enqueue(start, Heuristic(start));

        while (open.TryDequeue(out var current, out _))
        {
            if (closed[current])
                continue;

            if (current == goal)
                return Reconstruct(cameFrom, start, goal, biomeOf, craterOf, distanceKm, baseSpeedKmh);

            closed[current] = true;
            var currentBiome = biomeOf(current);

            foreach (var next in neighbours[current])
            {
                if (closed[next])
                    continue;

                var multiplier = MovementModel.SpeedMultiplier(currentBiome, biomeOf(next), craterOf(next));
                var hours = MovementModel.TravelTimeHours(distanceKm(current, next), baseSpeedKmh, multiplier);
                var tentative = best[current] + hours;

                if (tentative >= best[next])
                    continue;

                best[next] = tentative;
                cameFrom[next] = current;
                open.Enqueue(next, tentative + Heuristic(next));
            }
        }

        return null;

        double Heuristic(int cell)
            => distanceKm(cell, goal) / (baseSpeedKmh * bestMultiplier);
    }

    private static RoutePlan Reconstruct(
        int[] cameFrom,
        int start,
        int goal,
        Func<int, Biome> biomeOf,
        Func<int, CraterKind> craterOf,
        Func<int, int, double> distanceKm,
        double baseSpeedKmh)
    {
        var path = new List<int>();
        for (var cell = goal; cell >= 0; cell = cameFrom[cell])
        {
            path.Add(cell);
            if (cell == start)
                break;
        }

        path.Reverse();

        var legs = new RouteLeg[Math.Max(0, path.Count - 1)];
        var totalKm = 0.0;
        var totalHours = 0.0;

        for (var i = 0; i < legs.Length; i++)
        {
            var from = path[i];
            var to = path[i + 1];
            var multiplier = MovementModel.SpeedMultiplier(biomeOf(from), biomeOf(to), craterOf(to));
            var km = distanceKm(from, to);
            var hours = MovementModel.TravelTimeHours(km, baseSpeedKmh, multiplier);

            legs[i] = new RouteLeg(from, to, km, multiplier, hours);
            totalKm += km;
            totalHours += hours;
        }

        return new RoutePlan(legs, totalKm, totalHours);
    }
}
