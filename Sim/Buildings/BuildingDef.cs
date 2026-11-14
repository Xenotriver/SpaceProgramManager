using System.Collections.Generic;
using System.Linq;
using H3;
using H3.Algorithms;
using Sim.Movement;
using Sim.Site;
using Sim.World;

namespace Sim.Buildings;

/// <summary>
/// Category of a building for UI grouping and tech tree prerequisites.
/// </summary>
public enum BuildingCategory
{
    Power,
    LifeSupport,
    Habitat,
    Storage,
    Production,
    Science,
    Logistics,
    Defense,
    Special
}

/// <summary>
/// Defines a building type: footprint, cost, constraints, outputs, and requirements.
/// BuildingDefs are static data (ScriptableObject-like), loaded once per game.
/// </summary>
public sealed record BuildingDef
{
    /// <summary>Unique machine id, e.g. "solar_array_small".</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Category for UI/tech tree.</summary>
    public BuildingCategory Category { get; init; } = BuildingCategory.Special;

    /// <summary>Description shown in UI.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Footprint radius in hex rings.
    /// 0 = single hex, 1 = 7 hex (center + ring), 2 = 19 hex, etc.
    /// </summary>
    public int FootprintRadius { get; init; } = 0;

    /// <summary>Minimum site composite score required to place this building.</summary>
    public double MinSiteScore { get; init; } = 0.0;

    /// <summary>Biome IDs where this building may be placed. Empty = any buildable biome.</summary>
    public IReadOnlyList<string> AllowedBiomeIds { get; init; } = Array.Empty<string>();

    /// <summary>Biome IDs where this building may NOT be placed.</summary>
    public IReadOnlyList<string> ForbiddenBiomeIds { get; init; } = Array.Empty<string>();

    /// <summary>Maximum elevation difference (meters) across the footprint.</summary>
    public double MaxSlopeM { get; init; } = 500.0;

    /// <summary>Whether the footprint must be entirely crater-free.</summary>
    public bool RequireCraterFree { get; init; } = false;

    /// <summary>Whether the building can be placed on single-cell crater rims.</summary>
    public bool AllowSingleCellCrater { get; init; } = true;

    /// <summary>Whether the building can be placed on multi-cell crater areas.</summary>
    public bool AllowMultiCellCrater { get; init; } = false;

    /// <summary>Construction cost by resource type.</summary>
    public required IReadOnlyDictionary<string, double> Cost { get; init; }

    /// <summary>Build time in hours at 1× construction speed.</summary>
    public double BuildTimeHours { get; init; } = 0.0;

    /// <summary>Prerequisite building IDs (must exist in same region or be researched).</summary>
    public IReadOnlyList<string> Prerequisites { get; init; } = Array.Empty<string>();

    /// <summary>Technology required to unlock (empty = available from start).</summary>
    public string RequiredTech { get; init; } = string.Empty;

    /// <summary>Continuous power consumption (kW). Negative = generation.</summary>
    public double PowerKw { get; init; } = 0.0;

    /// <summary>Continuous resource production/consumption per hour.
    /// Positive = production, negative = consumption.</summary>
    public required IReadOnlyDictionary<string, double> ThroughputPerHour { get; init; }

    /// <summary>Storage capacity by resource type.</summary>
    public IReadOnlyDictionary<string, double> StorageCapacity { get; init; } = new Dictionary<string, double>();

    /// <summary>Crew capacity (for habitats).</summary>
    public int CrewCapacity { get; init; } = 0;

    /// <summary>Science output per hour.</summary>
    public double SciencePerHour { get; init; } = 0.0;

    /// <summary>Maintenance cost per hour (credits or resources).</summary>
    public IReadOnlyDictionary<string, double> MaintenancePerHour { get; init; } = new Dictionary<string, double>();

    /// <summary>Maximum health / durability.</summary>
    public double MaxHealth { get; init; } = 100.0;

    /// <summary>Visual prefab key for Godot (mesh + material).</summary>
    public string VisualKey { get; init; } = string.Empty;

    /// <summary>Footprint visualization color (hex RGBA, #RRGGBBAA).</summary>
    public string FootprintColor { get; init; } = "#4488FF88";

    /// <summary>
    /// Validates that a site meets this building's placement requirements.
    /// </summary>
    /// <param name="tile">The region tile containing the candidate center.</param>
    /// <param name="centerOrdinal">Ordinal of the candidate center hex within <paramref name="tile"/>.</param>
    /// <param name="world">The body world (biome table + metadata).</param>
    /// <param name="siteConfig">Site-scoring configuration (weights, body radius, etc.).</param>
    /// <param name="existingBuildings">
    /// Occupancy map keyed by H3 index (as <see cref="ulong"/>), used to reject overlaps.
    /// </param>
    public PlacementResult CanPlaceAt(
        RegionTile tile,
        int centerOrdinal,
        BodyWorld world,
        SiteScorerConfig siteConfig,
        IReadOnlyDictionary<ulong, BuildingInstance>? existingBuildings = null)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(siteConfig);

        // 1. Bounds check
        if ((uint)centerOrdinal >= (uint)tile.HexCount)
            return PlacementResult.Fail($"Center ordinal {centerOrdinal} out of range (0..{tile.HexCount - 1})");

        // 2. Build footprint hexes
        var footprint = BuildFootprint(tile, centerOrdinal);
        if (footprint.Count == 0)
            return PlacementResult.Fail("Footprint is empty");

        // 3. All hexes must be in this region
        var tileCellSet = new HashSet<ulong>(tile.Cells.Select(c => (ulong)c));
        foreach (var hex in footprint)
        {
            if (!tileCellSet.Contains((ulong)hex))
                return PlacementResult.Fail($"Footprint hex {hex} is outside this region");
        }

        // 3b. A multi-hex footprint must be complete: clipping at a region edge
        // would silently shrink the building (disk size = 3r(r+1) + 1).
        if (FootprintRadius > 0)
        {
            var expected = 3 * FootprintRadius * (FootprintRadius + 1) + 1;
            if (footprint.Count != expected)
                return PlacementResult.Fail(
                    $"Footprint needs {expected} hexes but only {footprint.Count} lie inside this region");
        }

        // 4. Check biome constraints
        var biomeResult = CheckBiomes(tile, footprint, world);
        if (!biomeResult.Success) return biomeResult;

        // 5. Check slope / elevation
        var slopeResult = CheckSlope(tile, footprint);
        if (!slopeResult.Success) return slopeResult;

        // 6. Check crater constraints
        var craterResult = CheckCraters(tile, footprint);
        if (!craterResult.Success) return craterResult;

        // 7. Check site score against the caller's weights
        var score = tile.ScoreSite(centerOrdinal, world, siteConfig);
        var weights = siteConfig.Weights;
        var composite = score.Factors.Composite(
            weights.Sunlight, weights.Comms, weights.Terrain, weights.Resources, weights.CraterClearance);

        if (composite < MinSiteScore)
            return PlacementResult.Fail($"Site score {composite:P0} below minimum {MinSiteScore:P0}");

        // 8. Check overlap with existing buildings
        if (existingBuildings is not null)
        {
            foreach (var hex in footprint)
            {
                if (existingBuildings.TryGetValue((ulong)hex, out var occupant))
                    return PlacementResult.Fail($"Hex {hex} already occupied by {occupant.DefId}");
            }
        }

        // 9. Check prerequisites (warn only; the caller decides whether to block)
        var warnings = new List<string>();
        if (Prerequisites.Count > 0 && existingBuildings is not null)
        {
            var present = new HashSet<string>(existingBuildings.Values.Select(b => b.DefId), StringComparer.Ordinal);
            var missing = Prerequisites.Where(p => !present.Contains(p)).ToList();
            if (missing.Count > 0)
                warnings.Add($"Missing prerequisites: {string.Join(", ", missing)}");
        }

        return PlacementResult.Ok(footprint, warnings);
    }

    /// <summary>
    /// Builds the footprint hex list for a given center: the center plus every hex
    /// within <see cref="FootprintRadius"/> rings that lies inside this region.
    /// </summary>
    public IReadOnlyList<H3Index> BuildFootprint(RegionTile tile, int centerOrdinal)
    {
        ArgumentNullException.ThrowIfNull(tile);

        if ((uint)centerOrdinal >= (uint)tile.HexCount)
            throw new ArgumentOutOfRangeException(nameof(centerOrdinal), centerOrdinal, $"Region has {tile.HexCount} hexes.");

        var centerHex = tile.Cells[centerOrdinal];
        var footprint = new List<H3Index> { centerHex };

        if (FootprintRadius > 0)
        {
            var tileCellSet = new HashSet<ulong>(tile.Cells.Select(c => (ulong)c));

            var neighbours = centerHex.GridDiskDistances(FootprintRadius)
                .Where(cell => cell.Distance >= 1 && cell.Distance <= FootprintRadius)
                .Select(cell => cell.Index)
                .Where(hex => tileCellSet.Contains((ulong)hex));

            footprint.AddRange(neighbours);
        }

        return footprint;
    }

    private PlacementResult CheckBiomes(RegionTile tile, IReadOnlyList<H3Index> footprint, BodyWorld world)
    {
        foreach (var hex in footprint)
        {
            var ordinal = FindOrdinal(tile, hex);
            if (ordinal < 0) return PlacementResult.Fail($"Hex {hex} not found in tile");

            var biomeCode = tile.BiomeCode(ordinal);
            if (!world.Biomes.TryGet(biomeCode, out var biome))
                return PlacementResult.Fail($"Unknown biome code {biomeCode}");

            if (!biome.Buildable)
                return PlacementResult.Fail($"Biome {biome.Id} is not buildable");

            if (AllowedBiomeIds.Count > 0 && !AllowedBiomeIds.Contains(biome.Id))
                return PlacementResult.Fail($"Biome {biome.Id} not in allowed list");

            if (ForbiddenBiomeIds.Contains(biome.Id))
                return PlacementResult.Fail($"Biome {biome.Id} is forbidden");
        }

        return PlacementResult.Ok();
    }

    private PlacementResult CheckSlope(RegionTile tile, IReadOnlyList<H3Index> footprint)
    {
        if (footprint.Count <= 1) return PlacementResult.Ok();

        var centerOrdinal = FindOrdinal(tile, footprint[0]);
        if (centerOrdinal < 0) return PlacementResult.Fail("Center hex not found");

        var centerElev = tile.ElevationM(centerOrdinal);
        double maxDiff = 0.0;

        foreach (var hex in footprint.Skip(1))
        {
            var ordinal = FindOrdinal(tile, hex);
            if (ordinal < 0) continue;

            var diff = Math.Abs(tile.ElevationM(ordinal) - centerElev);
            if (diff > maxDiff) maxDiff = diff;
        }

        if (maxDiff > MaxSlopeM)
            return PlacementResult.Fail($"Max slope {maxDiff:F0}m exceeds limit {MaxSlopeM:F0}m");

        return PlacementResult.Ok();
    }

    private PlacementResult CheckCraters(RegionTile tile, IReadOnlyList<H3Index> footprint)
    {
        foreach (var hex in footprint)
        {
            var ordinal = FindOrdinal(tile, hex);
            if (ordinal < 0) continue;

            var crater = tile.Crater(ordinal);
            if (crater == CraterKind.None) continue;

            if (RequireCraterFree)
                return PlacementResult.Fail($"Crater at {hex} violates crater-free requirement");

            if (crater == CraterKind.SingleCell && !AllowSingleCellCrater)
                return PlacementResult.Fail($"Single-cell crater at {hex} not allowed");

            if (crater == CraterKind.MultiCell && !AllowMultiCellCrater)
                return PlacementResult.Fail($"Multi-cell crater at {hex} not allowed");
        }

        return PlacementResult.Ok();
    }

    private static int FindOrdinal(RegionTile tile, H3Index hex)
    {
        for (var i = 0; i < tile.Cells.Count; i++)
            if ((ulong)tile.Cells[i] == (ulong)hex) return i;

        return -1;
    }
}

/// <summary>
/// Result of a placement validation check.
/// </summary>
public readonly record struct PlacementResult
{
    public PlacementResult()
    {
    }

    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public IReadOnlyList<H3Index> Footprint { get; init; } = Array.Empty<H3Index>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static PlacementResult Ok(IReadOnlyList<H3Index>? footprint = null, IReadOnlyList<string>? warnings = null)
        => new()
        {
            Success = true,
            Footprint = footprint ?? Array.Empty<H3Index>(),
            Warnings = warnings ?? Array.Empty<string>(),
        };

    public static PlacementResult Fail(string error)
        => new() { Success = false, Error = error };

    public static implicit operator bool(PlacementResult result) => result.Success;
}

/// <summary>
/// A placed building instance in a region.
/// </summary>
public sealed record BuildingInstance
{
    /// <summary>Reference to the building definition.</summary>
    public required string DefId { get; init; }

    /// <summary>Center hex of the building.</summary>
    public required H3Index CenterHex { get; init; }

    /// <summary>All hexes occupied by this building.</summary>
    public required IReadOnlyList<H3Index> Footprint { get; init; }

    /// <summary>Construction progress 0..1.</summary>
    public double Progress { get; set; } = 0.0;

    /// <summary>Current health (0 = destroyed).</summary>
    public double Health { get; set; } = 100.0;

    /// <summary>Current stored resources.</summary>
    public Dictionary<string, double> StoredResources { get; init; } = new();

    /// <summary>Operational status.</summary>
    public BuildingStatus Status { get; set; } = BuildingStatus.Planned;

    /// <summary>Game time (hours) when construction started.</summary>
    public double ConstructionStartTime { get; init; }

    /// <summary>Game time (hours) when construction completed.</summary>
    public double? ConstructionCompleteTime { get; set; }

    /// <summary>Custom state data (for complex buildings).</summary>
    public Dictionary<string, object> State { get; init; } = new();
}

public enum BuildingStatus
{
    Planned,
    UnderConstruction,
    Operational,
    Idle,
    Damaged,
    Destroyed
}