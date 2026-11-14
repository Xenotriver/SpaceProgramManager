using Sim.Buildings;

namespace Sim.Campaign;

/// <summary>
/// Buildings placed in one region, plus the hex → building occupancy map used by
/// placement validation. Mutations keep the map in sync.
/// </summary>
public sealed class RegionState
{
    private readonly List<BuildingInstance> _buildings = new();
    private Dictionary<ulong, BuildingInstance> _occupancy = new();

    public RegionState(ulong regionIndex)
    {
        RegionIndex = regionIndex;
    }

    /// <summary>H3 index of the coarse region cell.</summary>
    public ulong RegionIndex { get; }

    /// <summary>Buildings in placement order.</summary>
    public IReadOnlyList<BuildingInstance> Buildings => _buildings;

    /// <summary>Hex index → building occupying it.</summary>
    public IReadOnlyDictionary<ulong, BuildingInstance> Occupancy => _occupancy;

    /// <summary>Returns the building occupying a hex, if any.</summary>
    public bool TryGetAt(ulong hex, out BuildingInstance? building)
    {
        if (_occupancy.TryGetValue(hex, out var found))
        {
            building = found;
            return true;
        }

        building = null;
        return false;
    }

    internal void Add(BuildingInstance building)
    {
        ArgumentNullException.ThrowIfNull(building);

        _buildings.Add(building);
        RebuildOccupancy();
    }

    internal bool Remove(BuildingInstance building)
    {
        if (!_buildings.Remove(building))
            return false;

        RebuildOccupancy();
        return true;
    }

    internal int Clear()
    {
        var count = _buildings.Count;
        _buildings.Clear();
        _occupancy = new Dictionary<ulong, BuildingInstance>();
        return count;
    }

    internal void ReplaceAll(IEnumerable<BuildingInstance> buildings)
    {
        _buildings.Clear();
        _buildings.AddRange(buildings);
        RebuildOccupancy();
    }

    private void RebuildOccupancy()
        => _occupancy = new Dictionary<ulong, BuildingInstance>(BuildingRegistry.BuildOccupancy(_buildings));
}

/// <summary>
/// Result of reading one region from storage: buildings with derived fields
/// (footprints) still missing, plus any non-fatal read warnings.
/// </summary>
public sealed record RegionLoadResult(
    ulong RegionIndex,
    IReadOnlyList<BuildingInstance> Buildings,
    IReadOnlyList<string> Warnings)
{
    public static RegionLoadResult Empty(ulong regionIndex)
        => new(regionIndex, Array.Empty<BuildingInstance>(), Array.Empty<string>());
}

/// <summary>Outcome of validating and installing a stored region into a campaign.</summary>
public sealed record RegionLoadReport(
    ulong RegionIndex,
    int Loaded,
    int Dropped,
    IReadOnlyList<string> Warnings);
