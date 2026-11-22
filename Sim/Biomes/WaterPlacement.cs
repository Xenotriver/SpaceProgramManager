namespace Sim.Biomes;

/// <summary>
/// Per-building permission for water hexes. The zero value forbids water, so
/// every existing definition stays land-only without being edited.
/// </summary>
public enum WaterPlacement
{
    Forbidden = 0,
    Allowed = 1,
    Required = 2,
}

/// <summary>Shared rules for applying <see cref="WaterPlacement"/> to a biome.</summary>
public static class WaterPlacementRules
{
    public static bool Allows(WaterPlacement placement, Biome biome)
    {
        ArgumentNullException.ThrowIfNull(biome);

        return placement switch
        {
            WaterPlacement.Forbidden => !biome.IsWater,
            WaterPlacement.Allowed => true,
            WaterPlacement.Required => biome.IsWater,
            _ => false,
        };
    }

    public static string Describe(Biome biome) => biome.WaterKind switch
    {
        WaterKind.Ocean => "ocean",
        WaterKind.Lake => "lake",
        WaterKind.River => "river",
        _ => "land",
    };
}
