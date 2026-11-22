using Sim.Biomes;

namespace Sim.Movement;

/// <summary>Whether a hex is affected by a crater, and how many cells it spans.</summary>
public enum CraterKind
{
    None = 0,
    SingleCell = 1,
    MultiCell = 2,
}

/// <summary>
/// Vessel-speed movement model (design doc §2.3).
///
/// Speed is modified by the terrain rank difference between two hexes and by a crater
/// overlay. The crater penalty does <b>not</b> stack with the terrain modifier: on a
/// crater hex only the crater multiplier applies.
/// </summary>
public static class MovementModel
{
    /// <summary>Uphill (moving to a higher rank): 20% speed penalty.</summary>
    public const double UphillSpeedMultiplier = 0.80;

    /// <summary>Downhill (moving to a lower rank): 15% speed bonus.</summary>
    public const double DownhillSpeedMultiplier = 1.15;

    /// <summary>Single-cell crater: 30% speed penalty.</summary>
    public const double SingleCellCraterSpeedMultiplier = 0.70;

    /// <summary>Multi-cell crater: 20% speed penalty.</summary>
    public const double MultiCellCraterSpeedMultiplier = 0.80;

    /// <summary>Land units cannot enter water.</summary>
    public const double LandWaterSpeedMultiplier = 0.0;

    /// <summary>Amphibious units pay a penalty when moving through water.</summary>
    public const double AmphibiousWaterSpeedMultiplier = 0.65;

    /// <summary>Waterborne units travel faster over water than over land.</summary>
    public const double WaterborneWaterSpeedMultiplier = 1.10;

    /// <summary>Waterborne units cannot beach on ordinary land.</summary>
    public const double WaterborneLandSpeedMultiplier = 0.0;

    /// <summary>Whether a unit in <paramref name="mode"/> may occupy <paramref name="biome"/>.</summary>
    public static bool CanEnter(Biome biome, TerrainMode mode = TerrainMode.Land)
    {
        ArgumentNullException.ThrowIfNull(biome);

        return mode switch
        {
            TerrainMode.Land => !biome.IsWater,
            TerrainMode.Amphibious => true,
            TerrainMode.Waterborne => biome.IsWater,
            _ => false,
        };
    }

    /// <summary>Fastest multiplier any edge can have for a terrain mode (A* heuristic bound).</summary>
    public static double MaxSpeedMultiplier(TerrainMode mode) => mode switch
    {
        TerrainMode.Amphibious => Math.Max(DownhillSpeedMultiplier, AmphibiousWaterSpeedMultiplier),
        TerrainMode.Waterborne => Math.Max(DownhillSpeedMultiplier, WaterborneWaterSpeedMultiplier),
        _ => DownhillSpeedMultiplier,
    };

    /// <summary>Speed multiplier for moving from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double SpeedMultiplier(
        Biome from,
        Biome to,
        CraterKind crater = CraterKind.None,
        TerrainMode mode = TerrainMode.Land)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!CanEnter(to, mode))
            return 0.0;

        if (to.IsWater || from.IsWater)
        {
            return mode switch
            {
                TerrainMode.Amphibious => AmphibiousWaterSpeedMultiplier,
                TerrainMode.Waterborne => WaterborneWaterSpeedMultiplier,
                _ => LandWaterSpeedMultiplier,
            };
        }

        if (crater != CraterKind.None)
        {
            return crater == CraterKind.SingleCell
                ? SingleCellCraterSpeedMultiplier
                : MultiCellCraterSpeedMultiplier;
        }

        if (to.ElevationRank > from.ElevationRank)
            return UphillSpeedMultiplier;

        if (to.ElevationRank < from.ElevationRank)
            return DownhillSpeedMultiplier;

        return 1.0;
    }

    /// <summary>
    /// Travel time in hours to cross one hex edge of <paramref name="distanceKm"/> at
    /// <paramref name="baseSpeedKmh"/> with the given speed multiplier.
    /// </summary>
    public static double TravelTimeHours(double distanceKm, double baseSpeedKmh, double speedMultiplier)
    {
        if (baseSpeedKmh <= 0)
            throw new ArgumentOutOfRangeException(nameof(baseSpeedKmh), "Base speed must be positive.");

        if (speedMultiplier <= 0.0)
            return double.PositiveInfinity;

        return distanceKm / (baseSpeedKmh * speedMultiplier);
    }
}
