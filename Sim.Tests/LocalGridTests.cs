using System.Linq;
using Sim.Site;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Geometry of the 37-cell local build grid inside one fine hex.
/// </summary>
public class LocalGridTests
{
    [Fact]
    public void Cells_AreThe37DiskCellsInCentreFirstOrder()
    {
        var cells = LocalGrid.Cells;

        Assert.Equal(37, cells.Count);
        Assert.Equal(37, cells.Distinct().Count());
        Assert.Equal(LocalGrid.Center, cells[0]);
        Assert.All(cells, cell => Assert.True(LocalGrid.IsInside(cell), $"{cell} should be inside"));
        Assert.Equal(7, cells.Count(cell => LocalGrid.Distance(LocalGrid.Center, cell) <= 1));
        Assert.Equal(19, cells.Count(cell => LocalGrid.Distance(LocalGrid.Center, cell) <= 2));
    }

    [Fact]
    public void IsInside_AcceptsTheDiskAndRejectsRingFour()
    {
        Assert.True(LocalGrid.IsInside(new LocalCell(0, 0)));
        Assert.True(LocalGrid.IsInside(new LocalCell(3, 0)));
        Assert.True(LocalGrid.IsInside(new LocalCell(0, 3)));
        Assert.True(LocalGrid.IsInside(new LocalCell(-3, 0)));
        Assert.True(LocalGrid.IsInside(new LocalCell(1, -3)));

        Assert.False(LocalGrid.IsInside(new LocalCell(4, 0)));
        Assert.False(LocalGrid.IsInside(new LocalCell(3, 1)));
        Assert.False(LocalGrid.IsInside(new LocalCell(-2, -2)));
    }

    [Fact]
    public void Distance_UsesAxialHexMetric()
    {
        Assert.Equal(0, LocalGrid.Distance(LocalGrid.Center, LocalGrid.Center));
        Assert.Equal(3, LocalGrid.Distance(LocalGrid.Center, new LocalCell(3, 0)));
        Assert.Equal(3, LocalGrid.Distance(LocalGrid.Center, new LocalCell(3, -3)));
        Assert.Equal(2, LocalGrid.Distance(new LocalCell(1, 0), new LocalCell(-1, 0)));
        Assert.Equal(4, LocalGrid.Distance(new LocalCell(3, 0), new LocalCell(-1, 0)));
    }

    [Fact]
    public void Footprint_RadiusZeroIsTheCentreCell()
    {
        var footprint = LocalGrid.Footprint(new LocalCell(2, -1), 0);

        Assert.Equal(new[] { new LocalCell(2, -1) }, footprint);
    }

    [Fact]
    public void Footprint_RadiusOneIsCentrePlusSixNeighbours()
    {
        var centre = new LocalCell(1, -1);
        var footprint = LocalGrid.Footprint(centre, 1);

        Assert.Equal(7, footprint.Count);
        Assert.Contains(centre, footprint);
        Assert.Equal(7, footprint.Count(cell => LocalGrid.Distance(centre, cell) <= 1));
        Assert.All(footprint, cell => Assert.True(LocalGrid.IsInside(cell)));
    }

    [Fact]
    public void Footprint_OfRingThreeCentreClipsTheDisk()
    {
        var footprint = LocalGrid.Footprint(new LocalCell(3, 0), 1);

        Assert.Equal(7, footprint.Count);
        Assert.Contains(footprint, cell => !LocalGrid.IsInside(cell));
    }

    [Fact]
    public void Footprint_OfRingTwoCentreStillFits()
    {
        var footprint = LocalGrid.Footprint(new LocalCell(2, 0), 1);

        Assert.All(footprint, cell => Assert.True(LocalGrid.IsInside(cell), $"{cell} should fit"));
    }
}
