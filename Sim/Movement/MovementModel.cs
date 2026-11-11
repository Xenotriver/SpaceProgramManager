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

    /// <summary>Speed multiplier for moving from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double SpeedMultiplier(Biome from, Biome to, CraterKind crater = CraterKind.None)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

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

        return distanceKm / (baseSpeedKmh * speedMultiplier);
    }
}
