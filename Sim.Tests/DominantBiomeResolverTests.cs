using Sim.Biomes;
using Xunit;

namespace Sim.Tests;

public class DominantBiomeResolverTests
{
    private static Biome Biome(string id, int rank = 0) =>
        new() { Id = id, DisplayName = id, ElevationRank = rank };

    private static readonly Biome Highlands = Biome("luna.highlands", 3);
    private static readonly Biome Midlands = Biome("luna.midlands", 2);
    private static readonly Biome Lowlands = Biome("luna.lowlands", 1);
    private static readonly Biome Mares = Biome("luna.mare", 0);

    [Fact]
    public void DominantShare_60_40_PicksHighlands()
    {
        var shares = new Dictionary<Biome, double> { [Highlands] = 0.60, [Mares] = 0.40 };

        var result = DominantBiomeResolver.Resolve(shares, Array.Empty<Biome>());

        Assert.Equal(Highlands, result);
    }

    [Fact]
    public void DominantShare_51_49_PicksLowlands()
    {
        var shares = new Dictionary<Biome, double> { [Lowlands] = 0.51, [Highlands] = 0.49 };

        var result = DominantBiomeResolver.Resolve(shares, Array.Empty<Biome>());

        Assert.Equal(Lowlands, result);
    }

    [Fact]
    public void DominantShare_75_25_PicksMares()
    {
        var shares = new Dictionary<Biome, double> { [Mares] = 0.75, [Highlands] = 0.25 };

        var result = DominantBiomeResolver.Resolve(shares, Array.Empty<Biome>());

        Assert.Equal(Mares, result);
    }

    [Fact]
    public void ExactTie_BrokenByNeighborMajority()
    {
        var shares = new Dictionary<Biome, double> { [Highlands] = 0.50, [Mares] = 0.50 };
        var neighbors = new[] { Highlands, Highlands, Highlands, Highlands, Mares, Mares };

        var result = DominantBiomeResolver.Resolve(shares, neighbors);

        Assert.Equal(Highlands, result);
    }

    [Fact]
    public void TieAndNeighborTie_BrokenByDistance()
    {
        var shares = new Dictionary<Biome, double> { [Highlands] = 0.50, [Mares] = 0.50 };
        var neighbors = new[] { Highlands, Highlands, Highlands, Mares, Mares, Mares };

        double? DistanceToNearest(Biome biome) => biome == Mares ? 2.0 : 5.0;

        var result = DominantBiomeResolver.Resolve(shares, neighbors, DistanceToNearest);

        Assert.Equal(Mares, result);
    }

    [Fact]
    public void AllTied_FallsBackToPriority()
    {
        var shares = new Dictionary<Biome, double> { [Highlands] = 0.50, [Mares] = 0.50 };
        var neighbors = new[] { Highlands, Highlands, Highlands, Mares, Mares, Mares };
        var priority = new[] { Highlands, Mares };

        var result = DominantBiomeResolver.Resolve(shares, neighbors, distanceToNearest: null, priority);

        Assert.Equal(Highlands, result);
    }

    [Fact]
    public void SingleBiome_IsReturnedDirectly()
    {
        var shares = new Dictionary<Biome, double> { [Midlands] = 1.0 };

        var result = DominantBiomeResolver.Resolve(shares, Array.Empty<Biome>());

        Assert.Equal(Midlands, result);
    }
}
