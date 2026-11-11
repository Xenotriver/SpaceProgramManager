using Sim.Biomes;
using Sim.Movement;
using Xunit;

namespace Sim.Tests;

public class MovementModelTests
{
    private static readonly Biome Shore = new() { Id = "earth.shore", DisplayName = "Shore", ElevationRank = 0 };
    private static readonly Biome Plains = new() { Id = "earth.plains", DisplayName = "Plains", ElevationRank = 1 };
    private static readonly Biome Hills = new() { Id = "earth.hills", DisplayName = "Hills", ElevationRank = 2 };
    private static readonly Biome Mountains = new() { Id = "earth.mountains", DisplayName = "Mountains", ElevationRank = 3 };

    [Fact]
    public void Uphill_AppliesTwentyPercentPenalty()
    {
        Assert.Equal(MovementModel.UphillSpeedMultiplier, MovementModel.SpeedMultiplier(Plains, Hills));
    }

    [Fact]
    public void Downhill_AppliesFifteenPercentBonus()
    {
        Assert.Equal(MovementModel.DownhillSpeedMultiplier, MovementModel.SpeedMultiplier(Hills, Plains));
    }

    [Fact]
    public void SameRank_IsUnmodified()
    {
        Assert.Equal(1.0, MovementModel.SpeedMultiplier(Plains, Plains));
    }

    [Fact]
    public void SingleCellCrater_AppliesThirtyPercentPenalty_AndDoesNotStack()
    {
        // Moving uphill into a single-cell crater: only the crater penalty applies.
        var result = MovementModel.SpeedMultiplier(Plains, Hills, CraterKind.SingleCell);

        Assert.Equal(MovementModel.SingleCellCraterSpeedMultiplier, result);
    }

    [Fact]
    public void MultiCellCrater_AppliesTwentyPercentPenalty()
    {
        var result = MovementModel.SpeedMultiplier(Hills, Plains, CraterKind.MultiCell);

        Assert.Equal(MovementModel.MultiCellCraterSpeedMultiplier, result);
    }

    [Fact]
    public void TravelTime_DownhillLegIsFasterThanUphillReturn()
    {
        const double distanceKm = 2.1;
        const double baseSpeedKmh = 8.0;

        var downhill = MovementModel.SpeedMultiplier(Mountains, Shore);
        var uphill = MovementModel.SpeedMultiplier(Shore, Mountains);

        var downhillTime = MovementModel.TravelTimeHours(distanceKm, baseSpeedKmh, downhill);
        var uphillTime = MovementModel.TravelTimeHours(distanceKm, baseSpeedKmh, uphill);

        Assert.True(downhillTime < uphillTime);
    }
}
