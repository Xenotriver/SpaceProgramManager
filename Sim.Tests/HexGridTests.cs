using Sim.Hex;
using Xunit;

namespace Sim.Tests;

public class HexGridTests
{
    [Fact]
    public void FromLatLon_ProducesValidCellAtRequestedResolution()
    {
        var cell = HexGrid.FromLatLon(0.0, 0.0, 7);

        Assert.Equal(7, cell.Resolution);
        Assert.True(cell.IsValidCell);
    }

    [Fact]
    public void Neighbors_ReturnsSixDistinctCellsForNonPentagon()
    {
        var cell = HexGrid.FromLatLon(10.0, 20.0, 7);

        var neighbors = HexGrid.Neighbors(cell);

        Assert.Equal(6, neighbors.Count);
        Assert.DoesNotContain((ulong)cell, neighbors.Select(n => (ulong)n));
    }

    [Fact]
    public void Region_IsParentThatContainsTheFineHex()
    {
        var fine = HexGrid.FromLatLon(10.0, 20.0, 7);

        var region = HexGrid.Region(fine, 3);
        var children = HexGrid.FineHexesInRegion(region, 7);

        Assert.Equal(3, region.Resolution);
        Assert.Contains((ulong)fine, children.Select(c => (ulong)c));
    }

    [Fact]
    public void ToLatLon_RoundTripsToSameCell()
    {
        var original = HexGrid.FromLatLon(-12.5, 130.25, 7);

        var (lat, lon) = HexGrid.ToLatLon(original);
        var roundTripped = HexGrid.FromLatLon(lat, lon, 7);

        Assert.Equal((ulong)original, (ulong)roundTripped);
    }

    [Fact]
    public void FromLatLon_MatchesCanonicalH3Example()
    {
        // Canonical H3 documentation example: lat/lng (deg) -> cell at res 9.
        var cell = HexGrid.FromLatLon(37.775938728915946, -122.41795063018799, 9);

        Assert.Equal("8928308280fffff", cell.ToString());
    }
}
