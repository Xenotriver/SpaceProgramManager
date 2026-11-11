namespace Sim.Biomes;

/// <summary>
/// A biome definition for a specific celestial body.
///
/// Biomes are <b>per body</b>: a biome with the same name on two different bodies is a
/// different definition with its own rank and build rules (design doc §4.3).
/// </summary>
public sealed record Biome
{
    /// <summary>Stable machine id, e.g. "luna.mare".</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name, e.g. "Mare".</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Elevation rank used by the movement model. Higher = higher terrain.
    /// Moving to a higher rank is uphill; to a lower rank is downhill.
    /// </summary>
    public int ElevationRank { get; init; }

    /// <summary>Whether installations may be placed on this biome.</summary>
    public bool Buildable { get; init; } = true;
}
