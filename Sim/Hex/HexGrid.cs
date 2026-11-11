using H3;
using H3.Algorithms;
using H3.Extensions;
using H3.Model;

namespace Sim.Hex;

/// <summary>
/// Thin, engine-free helpers over the H3 hexagonal grid.
///
/// Two levels are used across the project:
///   - <b>fine hex</b>: the buildable gameplay cell (H3 resolution 7, ~1.2 km edge)
///   - <b>region</b>: the coarse parent cell used as a data tile / overview unit (H3 resolution 3)
/// </summary>
public static class HexGrid
{
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>Builds an H3 cell from geographic degrees at the given resolution.</summary>
    public static H3Index FromLatLon(double latitudeDegrees, double longitudeDegrees, int resolution)
        => H3Index.FromLatLng(
            new LatLng(latitudeDegrees * DegreesToRadians, longitudeDegrees * DegreesToRadians),
            resolution);

    /// <summary>Returns the centroid of a cell in geographic degrees.</summary>
    public static (double Latitude, double Longitude) ToLatLon(H3Index index)
    {
        var latLng = index.ToLatLng();
        return (latLng.LatitudeDegrees, latLng.LongitudeDegrees);
    }

    /// <summary>
    /// Returns the direct neighbours of a cell (6 normally, 5 on an H3 pentagon).
    /// </summary>
    public static IReadOnlyList<H3Index> Neighbors(H3Index index)
        => index.GridDiskDistances(1)
            .Where(cell => cell.Distance == 1)
            .Select(cell => cell.Index)
            .ToList();

    /// <summary>Returns the region (coarse parent) that contains a fine hex.</summary>
    public static H3Index Region(H3Index fineHex, int regionResolution)
        => fineHex.GetParentForResolution(regionResolution);

    /// <summary>Returns every fine hex contained in a region.</summary>
    public static IReadOnlyList<H3Index> FineHexesInRegion(H3Index region, int fineResolution)
        => region.GetChildrenForResolution(fineResolution).ToList();

    /// <summary>Grid distance in cells, or -1 when it cannot be determined.</summary>
    public static int GridDistance(H3Index a, H3Index b)
        => a.GridDistance(b);
}
