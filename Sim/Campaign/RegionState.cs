using H3;
using Sim.Buildings;
using Sim.Site;

namespace Sim.Campaign;

/// <summary>
/// Sites and installations placed in one region. Installations are grouped by the
/// fine hex that owns their site; the flattened <see cref="Buildings"/> view keeps
/// the simulation and economy code independent of site layout.
/// </summary>
public sealed class RegionState
{
    private readonly List<BuildingInstance> _buildings = new();
    private readonly Dictionary<ulong, SiteState> _sites = new();

    public RegionState(ulong regionIndex)
    {
        RegionIndex = regionIndex;
    }

    /// <summary>H3 index of the coarse region cell.</summary>
    public ulong RegionIndex { get; }

    /// <summary>All installations in the region, in placement order.</summary>
    public IReadOnlyList<BuildingInstance> Buildings => _buildings;

    /// <summary>Sites keyed by parent fine hex.</summary>
    public IReadOnlyDictionary<ulong, SiteState> Sites => _sites;

    /// <summary>Buildings in one site, or empty when the hex has no site yet.</summary>
    public IReadOnlyList<BuildingInstance> BuildingsAt(H3Index parentHex)
        => _sites.TryGetValue((ulong)parentHex, out var site)
            ? site.Installations
            : Array.Empty<BuildingInstance>();

    /// <summary>Returns the installation occupying a local cell of a site, if any.</summary>
    public bool TryGetAt(H3Index parentHex, LocalCell cell, out BuildingInstance? building)
    {
        if (_sites.TryGetValue((ulong)parentHex, out var site) && site.TryGetAt(cell, out var found))
        {
            building = found;
            return true;
        }

        building = null;
        return false;
    }

    internal SiteState GetOrAddSite(H3Index parentHex)
    {
        if (!_sites.TryGetValue((ulong)parentHex, out var site))
            _sites[(ulong)parentHex] = site = new SiteState(parentHex);

        return site;
    }

    internal bool TryGetSite(H3Index parentHex, out SiteState? site)
        => _sites.TryGetValue((ulong)parentHex, out site);

    internal void Add(BuildingInstance building)
    {
        ArgumentNullException.ThrowIfNull(building);

        _buildings.Add(building);
        GetOrAddSite(building.ParentHex).Add(building);
    }

    internal bool Remove(BuildingInstance building)
    {
        if (!_buildings.Remove(building))
            return false;

        if (_sites.TryGetValue((ulong)building.ParentHex, out var site))
        {
            site.Remove(building);
            if (site.Installations.Count == 0)
                _sites.Remove((ulong)building.ParentHex);
        }

        return true;
    }

    internal int Clear()
    {
        var count = _buildings.Count;
        _buildings.Clear();
        _sites.Clear();
        return count;
    }

    internal void ReplaceAll(IEnumerable<BuildingInstance> buildings)
    {
        _buildings.Clear();
        _sites.Clear();
        _buildings.AddRange(buildings);

        foreach (var building in _buildings)
            GetOrAddSite(building.ParentHex).Add(building);
    }
}

/// <summary>
/// Result of reading one region from storage: installations with derived fields
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
