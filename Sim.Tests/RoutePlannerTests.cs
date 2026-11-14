using Sim.Biomes;
using Sim.Movement;

namespace Sim.Tests;

public class RoutePlannerTests
{
    private static readonly Biome Mare = new()
    {
        Id = "test.mare",
        DisplayName = "Mare",
        ElevationRank = 0,
        Buildable = true,
    };

    private static readonly Biome Midlands = new()
    {
        Id = "test.midlands",
        DisplayName = "Midlands",
        ElevationRank = 2,
        Buildable = true,
    };

    private static readonly Biome Highlands = new()
    {
        Id = "test.highlands",
        DisplayName = "Highlands",
        ElevationRank = 3,
        Buildable = true,
    };

    [Fact]
    public void UphillReturn_IsSlowerThanDownhillLeg()
    {
        // 0 = mare, 1 = midlands: one leg up, the same leg back down.
        var neighbours = new[] { new[] { 1 }, new[] { 0 } };
        var biomes = new[] { Mare, Midlands };

        var up = RoutePlanner.Plan(0, 1, neighbours, id => biomes[id], _ => CraterKind.None, (_, _) => 2.0, 10.0);
        var down = RoutePlanner.Plan(1, 0, neighbours, id => biomes[id], _ => CraterKind.None, (_, _) => 2.0, 10.0);

        Assert.NotNull(up);
        Assert.NotNull(down);
        Assert.Equal(MovementModel.UphillSpeedMultiplier, up!.Legs[0].SpeedMultiplier);
        Assert.Equal(MovementModel.DownhillSpeedMultiplier, down!.Legs[0].SpeedMultiplier);
        Assert.True(down.TotalHours < up.TotalHours);
    }

    [Fact]
    public void CraterOnDestination_AppliesPenaltyAndDoesNotStackWithTerrain()
    {
        var neighbours = new[] { new[] { 1 }, new[] { 0 } };
        var biomes = new[] { Mare, Highlands };

        var plan = RoutePlanner.Plan(
            0, 1, neighbours, id => biomes[id],
            id => id == 1 ? CraterKind.SingleCell : CraterKind.None,
            (_, _) => 1.0, 10.0);

        Assert.NotNull(plan);
        Assert.Equal(MovementModel.SingleCellCraterSpeedMultiplier, plan!.Legs[0].SpeedMultiplier);
    }

    [Fact]
    public void PrefersLongerFlatDetourOverShortUphillClimb()
    {
        // 0 -> 2 is a short uphill climb; 0 -> 1 -> 2 is two flat steps of the
        // same length each, so it must win on time.
        var neighbours = new[]
        {
            new[] { 1, 2 },
            new[] { 0, 2 },
            new[] { 0, 1 },
        };
        var biomes = new[] { Mare, Mare, Highlands };
        var distance = (int from, int to) => from == 0 && to == 2 ? 3.0 : 1.0;

        var plan = RoutePlanner.Plan(0, 2, neighbours, id => biomes[id], _ => CraterKind.None, distance, 10.0);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.LegCount);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { plan.Start, plan.Legs[0].To, plan.Goal });
    }

    [Fact]
    public void UnreachableGoal_ReturnsNull()
    {
        var neighbours = new[] { Array.Empty<int>(), Array.Empty<int>() };
        var biomes = new[] { Mare, Midlands };

        var plan = RoutePlanner.Plan(0, 1, neighbours, id => biomes[id], _ => CraterKind.None, (_, _) => 1.0, 10.0);

        Assert.Null(plan);
    }

    [Fact]
    public void SameStartAndGoal_IsAnEmptyRoute()
    {
        var neighbours = new[] { Array.Empty<int>() };
        var biomes = new[] { Mare };

        var plan = RoutePlanner.Plan(0, 0, neighbours, id => biomes[id], _ => CraterKind.None, (_, _) => 1.0, 10.0);

        Assert.NotNull(plan);
        Assert.Equal(0, plan!.LegCount);
        Assert.Equal(0.0, plan.TotalHours);
    }

    [Fact]
    public void TotalHours_MatchesSumOfLegs()
    {
        var neighbours = new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 1 } };
        var biomes = new[] { Mare, Midlands, Mare };

        var plan = RoutePlanner.Plan(0, 2, neighbours, id => biomes[id], _ => CraterKind.None, (_, _) => 3.0, 12.0);

        Assert.NotNull(plan);
        var expected = plan!.Legs.Sum(leg => leg.Hours);
        Assert.Equal(expected, plan.TotalHours, 12);
        Assert.Equal(6.0, plan.TotalDistanceKm, 12);
    }
}
