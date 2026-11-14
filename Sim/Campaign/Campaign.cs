using H3;
using Sim.Buildings;
using Sim.Site;
using Sim.World;

namespace Sim.Campaign;

/// <summary>
/// The single source of truth for what the player has built. Views render it and
/// call <see cref="TryPlace"/> / <see cref="TryRemove"/>; they never own building
/// state themselves, so the region overlay and the close-up management view can
/// both place without duplicating logic.
///
/// Engine-free: no Godot types, no file I/O (that is <c>CampaignStore</c>).
/// </summary>
public sealed class CampaignState
{
    private readonly BodyWorld _world;
    private readonly BuildingRegistry _registry;
    private readonly SiteScorerConfig _siteConfig;
    private readonly Dictionary<ulong, RegionState> _regions = new();
    private readonly HashSet<ulong> _loaded = new();

    public CampaignState(BodyWorld world, BuildingRegistry registry, SiteScorerConfig? siteConfig = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        _world = world;
        _registry = registry;
        _siteConfig = siteConfig ?? new SiteScorerConfig { BodyRadiusKm = world.Manifest.Body.RadiusKm };
    }

    /// <summary>Campaign-wide state (clock, treasury, visited regions).</summary>
    public CampaignMeta Meta { get; } = new();

    /// <summary>Scoring configuration used for placement validation.</summary>
    public SiteScorerConfig SiteConfig => _siteConfig;

    /// <summary>Building catalogue.</summary>
    public BuildingRegistry Registry => _registry;

    /// <summary>Raised after a region's buildings change (placement, removal, clear).</summary>
    public event Action<ulong>? RegionChanged;

    /// <summary>Regions whose stored buildings have been installed or modified this session.</summary>
    public IReadOnlyCollection<ulong> LoadedRegions => _loaded;

    /// <summary>True once a region has been installed from storage or mutated.</summary>
    public bool IsLoaded(ulong regionIndex) => _loaded.Contains(regionIndex);

    /// <summary>Returns the region's state, creating an empty one on first access.</summary>
    public RegionState GetRegion(ulong regionIndex)
    {
        if (!_regions.TryGetValue(regionIndex, out var state))
            _regions[regionIndex] = state = new RegionState(regionIndex);

        return state;
    }

    /// <summary>Buildings in a region (empty for regions not opened yet).</summary>
    public IReadOnlyList<BuildingInstance> Buildings(ulong regionIndex)
        => _regions.TryGetValue(regionIndex, out var state)
            ? state.Buildings
            : Array.Empty<BuildingInstance>();

    /// <summary>Number of buildings in a region.</summary>
    public int BuildingCount(ulong regionIndex) => Buildings(regionIndex).Count;

    /// <summary>Records that the player has opened a region.</summary>
    public void MarkVisited(ulong regionIndex)
    {
        if (Meta.VisitedRegions.Add(regionIndex))
            Meta.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Validates a placement and returns the footprint to preview. Always includes
    /// a footprint, so a rejected ghost can still be drawn in red.
    /// </summary>
    public PlacementResult Preview(BuildingDef def, RegionTile tile, int centerOrdinal)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(tile);

        var occupancy = GetRegion(tile.Entry.Index).Occupancy;
        var result = def.CanPlaceAt(tile, centerOrdinal, _world, _siteConfig, occupancy);

        if (result.Success || result.Footprint.Count > 0)
            return result;

        return new PlacementResult
        {
            Success = false,
            Error = result.Error,
            Footprint = def.BuildFootprint(tile, centerOrdinal),
            Warnings = result.Warnings,
        };
    }

    /// <summary>Validates and, on success, places a building.</summary>
    public PlacementResult TryPlace(BuildingDef def, RegionTile tile, int centerOrdinal)
    {
        var result = Preview(def, tile, centerOrdinal);
        if (!result.Success)
            return result;

        var regionIndex = tile.Entry.Index;
        GetRegion(regionIndex).Add(new BuildingInstance
        {
            DefId = def.Id,
            CenterHex = tile.Cells[centerOrdinal],
            Footprint = result.Footprint,
        });

        Touch(regionIndex);
        return result;
    }

    /// <summary>Removes the building occupying a hex ordinal.</summary>
    public bool TryRemove(RegionTile tile, int ordinal, out BuildingInstance? removed)
    {
        ArgumentNullException.ThrowIfNull(tile);

        removed = null;
        if ((uint)ordinal >= (uint)tile.HexCount)
            return false;

        var regionIndex = tile.Entry.Index;
        var state = GetRegion(regionIndex);

        if (!state.TryGetAt((ulong)tile.Cells[ordinal], out removed))
            return false;

        state.Remove(removed!);
        Touch(regionIndex);
        return true;
    }

    /// <summary>Removes every building in the region and returns how many were removed.</summary>
    public int Clear(RegionTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        var regionIndex = tile.Entry.Index;
        var removed = GetRegion(regionIndex).Clear();

        if (removed > 0)
            Touch(regionIndex);

        return removed;
    }

    /// <summary>Returns the building occupying a hex ordinal, if any.</summary>
    public bool TryGetAt(RegionTile tile, int ordinal, out BuildingInstance? building)
    {
        ArgumentNullException.ThrowIfNull(tile);

        building = null;
        if ((uint)ordinal >= (uint)tile.HexCount)
            return false;

        return GetRegion(tile.Entry.Index).TryGetAt((ulong)tile.Cells[ordinal], out building);
    }

    /// <summary>
    /// Validates stored buildings against the live tile and installs them. The
    /// footprint is rebuilt from the definition, so a save only needs the center
    /// hex; records that no longer fit are dropped and reported.
    /// </summary>
    public RegionLoadReport InstallRegion(RegionLoadResult stored, RegionTile tile)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(tile);

        var regionIndex = tile.Entry.Index;
        if (stored.RegionIndex != regionIndex)
            throw new ArgumentException(
                $"Stored region {stored.RegionIndex} does not match tile {regionIndex}.", nameof(stored));

        var warnings = new List<string>(stored.Warnings);
        var accepted = new List<BuildingInstance>(stored.Buildings.Count);
        var claimed = new Dictionary<ulong, BuildingInstance>();
        var ordinals = BuildOrdinalMap(tile);

        foreach (var candidate in stored.Buildings)
        {
            if (!_registry.TryGet(candidate.DefId, out var def) || def is null)
            {
                warnings.Add($"{candidate.DefId}: unknown building definition");
                continue;
            }

            var centerHex = (ulong)candidate.CenterHex;
            if (!ordinals.TryGetValue(centerHex, out var centerOrdinal))
            {
                warnings.Add($"{def.Id}: center hex {centerHex} is not in region {regionIndex}");
                continue;
            }

            var footprint = def.BuildFootprint(tile, centerOrdinal);

            if (def.FootprintRadius > 0)
            {
                var expected = 3 * def.FootprintRadius * (def.FootprintRadius + 1) + 1;
                if (footprint.Count != expected)
                {
                    warnings.Add($"{def.Id} at {centerHex}: footprint clipped by the region boundary");
                    continue;
                }
            }

            BuildingInstance? clash = null;
            foreach (var hex in footprint)
            {
                if (claimed.TryGetValue((ulong)hex, out var other))
                {
                    clash = other;
                    break;
                }
            }

            if (clash is not null)
            {
                warnings.Add($"{def.Id} at {centerHex}: overlaps {clash.DefId}");
                continue;
            }

            var building = candidate with
            {
                Id = candidate.Id == Guid.Empty ? Guid.NewGuid() : candidate.Id,
                CenterHex = tile.Cells[centerOrdinal],
                Footprint = footprint,
            };

            accepted.Add(building);
            foreach (var hex in footprint)
                claimed[(ulong)hex] = building;
        }

        GetRegion(regionIndex).ReplaceAll(accepted);
        _loaded.Add(regionIndex);
        MarkVisited(regionIndex);

        return new RegionLoadReport(
            regionIndex,
            accepted.Count,
            stored.Buildings.Count - accepted.Count,
            warnings);
    }

    private void Touch(ulong regionIndex)
    {
        _loaded.Add(regionIndex);
        Meta.VisitedRegions.Add(regionIndex);
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        RegionChanged?.Invoke(regionIndex);
    }

    private static Dictionary<ulong, int> BuildOrdinalMap(RegionTile tile)
    {
        var ordinals = new Dictionary<ulong, int>(tile.HexCount);
        for (var i = 0; i < tile.HexCount; i++)
            ordinals[(ulong)tile.Cells[i]] = i;

        return ordinals;
    }
}
