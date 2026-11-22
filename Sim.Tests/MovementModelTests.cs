using Sim.Biomes;
using Sim.Movement;
using Xunit;

namespace Sim.Tests;

public class MovementModelTests
{
    private static readonly Biome Ocean = new()
    {
        Id = "earth.ocean", DisplayName = "Ocean", ElevationRank = 0, WaterKind = WaterKind.Ocean
    };
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

    [Fact]
    public void LandMode_CannotEnterWater()
    {
        Assert.False(MovementModel.CanEnter(Ocean, TerrainMode.Land));
        Assert.Equal(0.0, MovementModel.SpeedMultiplier(Shore, Ocean));
        Assert.Equal(
            double.PositiveInfinity,
            MovementModel.TravelTimeHours(2.0, 8.0, MovementModel.SpeedMultiplier(Shore, Ocean)));
    }

    [Fact]
    public void AmphibiousMode_PaysWaterPenalty()
    {
        Assert.True(MovementModel.CanEnter(Ocean, TerrainMode.Amphibious));
        Assert.Equal(
            MovementModel.AmphibiousWaterSpeedMultiplier,
            MovementModel.SpeedMultiplier(Shore, Ocean, CraterKind.None, TerrainMode.Amphibious));
    }

    [Fact]
    public void WaterborneMode_TraversesWaterButNotLand()
    {
        Assert.True(MovementModel.CanEnter(Ocean, TerrainMode.Waterborne));
        Assert.False(MovementModel.CanEnter(Plains, TerrainMode.Waterborne));
        Assert.Equal(
            MovementModel.WaterborneWaterSpeedMultiplier,
            MovementModel.SpeedMultiplier(Ocean, Ocean, CraterKind.None, TerrainMode.Waterborne));
        Assert.Equal(0.0, MovementModel.SpeedMultiplier(Ocean, Plains, CraterKind.None, TerrainMode.Waterborne));
    }
}
