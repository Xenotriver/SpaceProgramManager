using Sim.World;
using Xunit;

namespace Sim.Tests;

public class KnownBodiesTests
{
    [Fact]
    public void ReadyBodies_AreMoonMarsAndEarth()
    {
        Assert.Equal(new[] { "moon", "mars", "earth" }, KnownBodies.ReadyKeys);
        Assert.All(KnownBodies.ReadyKeys, key => Assert.True(KnownBodies.IsReady(key)));
    }

    [Theory]
    [InlineData("MOON", "moon")]
    [InlineData(" Mars ", "mars")]
    [InlineData("EARTH", "earth")]
    [InlineData("jupiter", null)]
    [InlineData("", null)]
    public void TryNormalize_IsCaseInsensitiveAndRejectsUnknownBodies(string input, string? expected)
        => Assert.Equal(expected, KnownBodies.TryNormalize(input));
}
