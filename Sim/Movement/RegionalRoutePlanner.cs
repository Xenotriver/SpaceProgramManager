using H3;
using H3.Algorithms;
using Sim.Biomes;
using Sim.Hex;
using Sim.World;

namespace Sim.Movement;

/// <summary>One adjacent fine-hex crossing in a route that may span regions.</summary>
public readonly record struct RegionalRouteLeg(
    ulong FromRegion,
    ulong FromHex,
    ulong ToRegion,
    ulong ToHex,
    double DistanceKm,
    double Hours);

/// <summary>A planned route across any number of region tiles.</summary>
public sealed record RegionalRoutePlan(
    IReadOnlyList<RegionalRouteLeg> Legs,
    double TotalDistanceKm,
    double TotalHours)
{
    public int LegCount => Legs.Count;

    public ulong StartRegion => LegCount == 0 ? 0UL : Legs[0].FromRegion;

    public ulong StartHex => LegCount == 0 ? 0UL : Legs[0].FromHex;

    public ulong GoalRegion => LegCount == 0 ? 0UL : Legs[^1].ToRegion;

    public ulong GoalHex => LegCount == 0 ? 0UL : Legs[^1].ToHex;

    /// <summary>Number of fine-hex steps that cross a region boundary.</summary>
    public int RegionCrossings => Legs.Count(leg => leg.FromRegion != leg.ToRegion);
}

/// <summary>
/// Fastest fine-hex route between two cells anywhere on a body.
///
/// Region tiles stay opaque and are loaded on demand through <see cref="BodyWorld"/>.
/// The planner first A*-s over the coarse H3 region graph, then reuses
/// <see cref="RoutePlanner"/> inside each region between the chosen border hexes.
/// Edge costs use the same <see cref="MovementModel"/> as the existing local
/// preview, including the destination crater and biome rank change.
/// </summary>
public sealed class RegionalRoutePlanner
{
    private readonly BodyWorld _world;
    private readonly double _speedKmh;
    private readonly Dictionary<ulong, RegionTile> _tiles = new();
    private readonly Dictionary<ulong, Dictionary<ulong, int>> _ordinals = new();
    private readonly Dictionary<ulong, int[][]> _adjacency = new();
    private readonly Dictionary<(ulong From, ulong To), BorderCrossing> _crossings = new();
    private readonly Dictionary<ulong, H3Index> _centers = new();

    private RegionalRoutePlanner(BodyWorld world, double speedKmh)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (speedKmh <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(speedKmh), "Base speed must be positive.");

        _world = world;
        _speedKmh = speedKmh;
    }

    /// <summary>Plans a route, or returns <see langword="null"/> when unreachable.</summary>
    public static RegionalRoutePlan? Plan(
        BodyWorld world,
        ulong fromRegion,
        ulong fromHex,
        ulong toRegion,
        ulong toHex,
        double baseSpeedKmh)
        => new RegionalRoutePlanner(world, baseSpeedKmh)
            .Plan(fromRegion, fromHex, toRegion, toHex);

    private RegionalRoutePlan? Plan(ulong fromRegion, ulong fromHex, ulong toRegion, ulong toHex)
    {
        if (!_world.TryGetRegionEntry(fromRegion, out _)
            || !_world.TryGetRegionEntry(toRegion, out _))
            return null;

        if (!TryOrdinal(fromRegion, fromHex, out var fromOrdinal)
            || !TryOrdinal(toRegion, toHex, out var toOrdinal))
            return null;

        var legs = new List<RegionalRouteLeg>();

        if (fromRegion == toRegion)
        {
            var direct = LocalPlan(fromRegion, fromOrdinal, toOrdinal);
            if (direct is null)
                return null;

            AddLocal(legs, fromRegion, direct);
            return Finish(legs);
        }

        var regionPath = PlanRegionPath(fromRegion, toRegion);
        if (regionPath is null)
            return null;

        var entryRegion = fromRegion;
        var entryOrdinal = fromOrdinal;

        for (var i = 0; i < regionPath.Count - 1; i++)
        {
            var current = regionPath[i];
            var next = regionPath[i + 1];
            var crossing = Crossing(current, next);
            if (crossing is null)
                return null;

            var edge = crossing.Value;
            var local = LocalPlan(entryRegion, entryOrdinal, edge.FromOrdinal);
            if (local is null)
                return null;

            AddLocal(legs, entryRegion, local);
            legs.Add(new RegionalRouteLeg(
                current,
                (ulong)Tile(current).Cells[edge.FromOrdinal],
                next,
                (ulong)Tile(next).Cells[edge.ToOrdinal],
                edge.DistanceKm,
                edge.Hours));

            entryRegion = next;
            entryOrdinal = edge.ToOrdinal;
        }

        var last = LocalPlan(toRegion, entryOrdinal, toOrdinal);
        if (last is null)
            return null;

        AddLocal(legs, toRegion, last);
        return Finish(legs);
    }

    private static RegionalRoutePlan Finish(List<RegionalRouteLeg> legs)
        => new(
            legs,
            legs.Sum(leg => leg.DistanceKm),
            legs.Sum(leg => leg.Hours));

    private static void AddLocal(
        List<RegionalRouteLeg> legs,
        ulong region,
        RoutePlan local)
    {
        foreach (var leg in local.Legs)
        {
            legs.Add(new RegionalRouteLeg(
                region, (ulong)leg.From, region, (ulong)leg.To,
                leg.DistanceKm, leg.Hours));
        }
    }

    private RoutePlan? LocalPlan(ulong region, int from, int to)
    {
        var tile = Tile(region);
        if (from == to)
            return new RoutePlan(Array.Empty<RouteLeg>(), 0.0, 0.0);

        var adjacency = Adjacency(region);
        return RoutePlanner.Plan(
            from,
            to,
            adjacency,
            ordinal => BiomeAt(region, ordinal),
            ordinal => tile.Crater(ordinal),
            (a, b) => HexGrid.DistanceKm(tile.Cells[a], tile.Cells[b], _world.Manifest.Body.RadiusKm),
            _speedKmh);
    }

    private Biome BiomeAt(ulong region, int ordinal)
        => _world.Biomes[Tile(region).BiomeCode(ordinal)];

    private List<ulong>? PlanRegionPath(ulong start, ulong goal)
    {
        var best = new Dictionary<ulong, double> { [start] = 0.0 };
        var cameFrom = new Dictionary<ulong, ulong>();
        var closed = new HashSet<ulong>();
        var open = new PriorityQueue<ulong, double>();
        open.Enqueue(start, Heuristic(start));

        while (open.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current))
                continue;

            if (current == goal)
            {
                var path = new List<ulong> { goal };
                for (var node = goal; node != start; node = cameFrom[node])
                {
                    if (!cameFrom.TryGetValue(node, out var previous))
                        return null;

                    path.Add(previous);
                }

                path.Reverse();
                return path;
            }

            foreach (var neighbour in HexGrid.Neighbors(new H3Index(current)))
            {
                var next = (ulong)neighbour;
                if (closed.Contains(next) || !_world.TryGetRegionEntry(next, out _))
                    continue;

                var crossing = Crossing(current, next);
                if (crossing is null)
                    continue;

                var tentative = best[current] + crossing.Value.Hours;
                if (tentative >= best.GetValueOrDefault(next, double.PositiveInfinity))
                    continue;

                best[next] = tentative;
                cameFrom[next] = current;
                open.Enqueue(next, tentative + Heuristic(next));
            }
        }

        return null;

        double Heuristic(ulong region)
            => HexGrid.DistanceKm(Center(region), Center(goal), _world.Manifest.Body.RadiusKm)
               / (_speedKmh * MovementModel.DownhillSpeedMultiplier);
    }

    private BorderCrossing? Crossing(ulong fromRegion, ulong toRegion)
    {
        var key = (fromRegion, toRegion);
        if (_crossings.TryGetValue(key, out var cached))
            return cached;

        var from = Tile(fromRegion);
        var to = Tile(toRegion);
        var toOrdinals = Ordinals(toRegion);
        var radiusKm = _world.Manifest.Body.RadiusKm;

        BorderCrossing? best = null;

        for (var i = 0; i < from.Cells.Count; i++)
        {
            foreach (var neighbour in from.Cells[i].GridDiskDistances(1))
            {
                if (neighbour.Distance != 1
                    || (ulong)HexGrid.Region(neighbour.Index, _world.RegionResolution) != toRegion
                    || !toOrdinals.TryGetValue((ulong)neighbour.Index, out var destination))
                    continue;

                var km = HexGrid.DistanceKm(from.Cells[i], neighbour.Index, radiusKm);
                var multiplier = MovementModel.SpeedMultiplier(
                    BiomeAt(fromRegion, i), BiomeAt(toRegion, destination), to.Crater(destination));
                var hours = MovementModel.TravelTimeHours(km, _speedKmh, multiplier);

                if (best is not null && hours >= best.Value.Hours)
                    continue;

                best = new BorderCrossing(i, destination, km, hours);
            }
        }

        if (best is not null)
            _crossings[key] = best.Value;

        return best;
    }

    private readonly record struct BorderCrossing(
        int FromOrdinal,
        int ToOrdinal,
        double DistanceKm,
        double Hours);

    private bool TryOrdinal(ulong region, ulong hex, out int ordinal)
        => Ordinals(region).TryGetValue(hex, out ordinal);

    private Dictionary<ulong, int> Ordinals(ulong region)
    {
        if (!_ordinals.TryGetValue(region, out var map))
        {
            var tile = Tile(region);
            map = new Dictionary<ulong, int>(tile.HexCount);
            for (var i = 0; i < tile.Cells.Count; i++)
                map[(ulong)tile.Cells[i]] = i;

            _ordinals[region] = map;
        }

        return map;
    }

    private int[][] Adjacency(ulong region)
    {
        if (!_adjacency.TryGetValue(region, out var adjacency))
        {
            var cells = Tile(region).Cells;
            var ordinals = Ordinals(region);
            adjacency = new int[cells.Count][];

            for (var i = 0; i < cells.Count; i++)
            {
                var neighbours = new List<int>(6);
                foreach (var neighbour in cells[i].GridDiskDistances(1))
                {
                    if (neighbour.Distance == 1
                        && ordinals.TryGetValue((ulong)neighbour.Index, out var ordinal))
                        neighbours.Add(ordinal);
                }

                adjacency[i] = neighbours.ToArray();
            }

            _adjacency[region] = adjacency;
        }

        return adjacency;
    }

    private RegionTile Tile(ulong region)
    {
        if (!_tiles.TryGetValue(region, out var tile))
        {
            tile = _world.GetRegion(region);
            _tiles[region] = tile;
        }

        return tile;
    }

    private H3Index Center(ulong region)
    {
        if (!_centers.TryGetValue(region, out var center))
        {
            center = new H3Index(region);
            _centers[region] = center;
        }

        return center;
    }
}
