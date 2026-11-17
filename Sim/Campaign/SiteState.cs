using H3;
using Sim.Buildings;
using Sim.Site;

namespace Sim.Campaign;

/// <summary>
/// Installations inside one fine hex. Occupancy is local to the site, so several
/// installations can share the parent hex as long as their local cells do not clash.
/// </summary>
public sealed class SiteState
{
    private readonly List<BuildingInstance> _installations = new();
    private Dictionary<LocalCell, BuildingInstance> _occupancy = new();

    public SiteState(H3Index parentHex)
    {
        ParentHex = parentHex;
    }

    /// <summary>H3 index of the fine hex containing this site.</summary>
    public H3Index ParentHex { get; }

    /// <summary>Installations in placement order.</summary>
    public IReadOnlyList<BuildingInstance> Installations => _installations;

    /// <summary>Local cell -> installation occupying it.</summary>
    public IReadOnlyDictionary<LocalCell, BuildingInstance> Occupancy => _occupancy;

    public bool TryGetAt(LocalCell cell, out BuildingInstance? building)
    {
        if (_occupancy.TryGetValue(cell, out var found))
        {
            building = found;
            return true;
        }

        building = null;
        return false;
    }

    internal void Add(BuildingInstance installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        _installations.Add(installation);
        RebuildOccupancy();
    }

    internal bool Remove(BuildingInstance installation)
    {
        if (!_installations.Remove(installation))
            return false;

        RebuildOccupancy();
        return true;
    }

    internal int Clear()
    {
        var count = _installations.Count;
        _installations.Clear();
        _occupancy = new Dictionary<LocalCell, BuildingInstance>();
        return count;
    }

    internal void ReplaceAll(IEnumerable<BuildingInstance> installations)
    {
        _installations.Clear();
        _installations.AddRange(installations);
        RebuildOccupancy();
    }

    private void RebuildOccupancy()
    {
        var occupancy = new Dictionary<LocalCell, BuildingInstance>();
        foreach (var installation in _installations)
        {
            foreach (var cell in installation.LocalFootprint)
                occupancy[cell] = installation;
        }

        _occupancy = occupancy;
    }
}
