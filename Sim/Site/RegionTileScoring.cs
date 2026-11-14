using System.Collections.Generic;
using System.Linq;
using H3;
using H3.Algorithms;
using H3.Extensions;
using Sim.Biomes;
using Sim.Hex;
using Sim.Movement;
using Sim.World;

namespace Sim.Site;

/// <summary>
/// Extension methods on <see cref="RegionTile"/> for site scoring.
/// </summary>
public static class RegionTileScoring
{
    /// <summary>
    /// Scores a site footprint centered on <paramref name="centerOrdinal"/> within this region.
    /// </summary>
    /// <param name="tile">The region tile to score within.</param>
    /// <param name="centerOrdinal">Ordinal index of the center hex in this region's tile.</param>
    /// <param name="world">The parent BodyWorld (for biome registry and body metadata).</param>
    /// <param name="config">Scoring configuration (weights, body radius, resource deposits, etc.).</param>
    /// <returns>A <see cref="SiteScore"/> with factor breakdown and composite score.</returns>
    public static SiteScore ScoreSite(
        this RegionTile tile,
        int centerOrdinal,
        BodyWorld world,
        SiteScorerConfig config)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(config);

        if ((uint)centerOrdinal >= (uint)tile.HexCount)
            throw new ArgumentOutOfRangeException(nameof(centerOrdinal), centerOrdinal, $"Region has {tile.HexCount} hexes.");

        // Build footprint: center + neighbors within radius
        var footprint = BuildFootprint(tile, centerOrdinal, config.FootprintRadius);
        var centerHex = tile.Cells[centerOrdinal];

        // Check buildability across footprint
        var buildable = footprint.All(h => IsBuildable(tile, world, h));

        // Compute factors
        var sunlight = ComputeSunlight(footprint, config);
        var comms = ComputeComms(footprint, config);
        var terrain = ComputeTerrain(tile, footprint);
        var resources = ComputeResources(footprint, config);
        var craterClearance = ComputeCraterClearance(tile, footprint);

        var factors = new SiteFactors
        {
            Sunlight = sunlight,
            Comms = comms,
            Terrain = terrain,
            Resources = resources,
            CraterClearance = craterClearance,
            Buildable = buildable
        };

        return new SiteScore
        {
            Center = centerHex,
            Footprint = footprint,
            Factors = factors
        };
    }

    /// <summary>
    /// Scores all valid sites in this region and returns the top <paramref name="count"/> results.
    /// </summary>
    public static IReadOnlyList<SiteScore> FindBestSites(
        this RegionTile tile,
        BodyWorld world,
        SiteScorerConfig config,
        int count = 10)
    {
        var scores = new List<SiteScore>(tile.HexCount);

        for (int i = 0; i < tile.HexCount; i++)
        {
            var score = tile.ScoreSite(i, world, config);
            if (score.IsViable(config))
                scores.Add(score);
        }

        return scores
            .OrderByDescending(s => s.CompositeWith(config.Weights))
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Builds the footprint hex list: center + all hexes within <paramref name="radius"/> rings.
    /// </summary>
    private static IReadOnlyList<H3Index> BuildFootprint(RegionTile tile, int centerOrdinal, int radius)
    {
        var centerHex = tile.Cells[centerOrdinal];
        var footprint = new List<H3Index> { centerHex };

        if (radius > 0)
        {
            var neighbors = centerHex.GridDiskDistances(radius)
                .Where(c => c.Distance >= 1 && c.Distance <= radius)
                .Select(c => c.Index)
                .ToList();

            // Only keep neighbors that are in this region's tile
            var tileCellSet = new HashSet<ulong>(tile.Cells.Select(c => (ulong)c));
            footprint.AddRange(neighbors.Where(h => tileCellSet.Contains((ulong)h)));
        }

        return footprint;
    }

    private static bool IsBuildable(RegionTile tile, BodyWorld world, H3Index hex)
    {
        var ordinal = FindOrdinal(tile, hex);
        if (ordinal < 0) return false;

        var biomeCode = tile.BiomeCode(ordinal);
        return world.Biomes.TryGet(biomeCode, out var biome) && biome.Buildable;
    }

    private static int FindOrdinal(RegionTile tile, H3Index hex)
    {
        for (int i = 0; i < tile.Cells.Count; i++)
        {
            if ((ulong)tile.Cells[i] == (ulong)hex) return i;
        }
        return -1;
    }

    /// <summary>
    /// Sunlight fraction: fraction of year with sun elevation > MinSunElevationDeg.
    /// Simplified: uses latitude and axial tilt to estimate annual sunlight.
    /// </summary>
    private static double ComputeSunlight(IReadOnlyList<H3Index> footprint, SiteScorerConfig config)
    {
        if (footprint.Count == 0) return 0.0;

        var tiltRad = config.AxialTiltDeg * Math.PI / 180.0;
        var minElevRad = config.MinSunElevationDeg * Math.PI / 180.0;

        double total = 0.0;
        foreach (var hex in footprint)
        {
            var (lat, _) = HexGrid.ToLatLon(hex);
            var latRad = lat * Math.PI / 180.0;

            // Simplified sunlight model: fraction of year where sun is above min elevation
            var polarDay = Math.Abs(latRad) > (Math.PI / 2.0 - tiltRad);
            if (polarDay)
            {
                total += 1.0;
                continue;
            }

            var maxSunElev = Math.PI / 2.0 - Math.Abs(latRad) + tiltRad;
            var minSunElev = Math.PI / 2.0 - Math.Abs(latRad) - tiltRad;

            if (minSunElev >= minElevRad)
                total += 1.0;
            else if (maxSunElev <= minElevRad)
                total += 0.0;
            else
            {
                var frac = (maxSunElev - minElevRad) / (maxSunElev - minSunElev);
                total += Math.Clamp(frac, 0.0, 1.0);
            }
        }

        return total / footprint.Count;
    }

    /// <summary>
    /// Comms quality: line-of-sight to Earth (assumed at 0° lat, 0° lon for Moon near side).
    /// For Moon: near side = 1.0, far side = 0.0, limb = gradient.
    /// </summary>
    private static double ComputeComms(IReadOnlyList<H3Index> footprint, SiteScorerConfig config)
    {
        if (footprint.Count == 0) return 0.0;

        const double EarthLat = 0.0;
        const double EarthLon = 0.0;
        var earthVec = LatLonToVector(EarthLat, EarthLon);

        double total = 0.0;
        foreach (var hex in footprint)
        {
            var (lat, lon) = HexGrid.ToLatLon(hex);
            var hexVec = LatLonToVector(lat, lon);

            var dot = VectorDot(hexVec, earthVec);

            double comms;
            if (dot >= 0.1)
                comms = 1.0;
            else if (dot <= -0.1)
                comms = 0.0;
            else
                comms = (dot + 0.1) / 0.2;

            total += comms;
        }

        return total / footprint.Count;
    }

    /// <summary>
    /// Terrain suitability: inverse of slope/roughness within footprint.
    /// Uses elevation differences between center and neighbors.
    /// </summary>
    private static double ComputeTerrain(RegionTile tile, IReadOnlyList<H3Index> footprint)
    {
        if (footprint.Count <= 1) return 1.0;

        var centerOrdinal = FindOrdinal(tile, footprint[0]);
        if (centerOrdinal < 0) return 0.5;

        var centerElev = tile.ElevationM(centerOrdinal);
        double maxDiff = 0.0;

        foreach (var hex in footprint.Skip(1))
        {
            var ordinal = FindOrdinal(tile, hex);
            if (ordinal < 0) continue;

            var elev = tile.ElevationM(ordinal);
            var diff = Math.Abs(elev - centerElev);
            if (diff > maxDiff) maxDiff = diff;
        }

        return Math.Clamp(1.0 - maxDiff / 1000.0, 0.0, 1.0);
    }

    /// <summary>
    /// Resource proximity: inverse distance to nearest known deposit.
    /// </summary>
    private static double ComputeResources(IReadOnlyList<H3Index> footprint, SiteScorerConfig config)
    {
        if (footprint.Count == 0 || config.ResourceDeposits.Count == 0)
            return 0.5;

        double bestScore = 0.0;

        foreach (var hex in footprint)
        {
            var (lat, lon) = HexGrid.ToLatLon(hex);
            var hexVec = LatLonToVector(lat, lon);

            foreach (var deposit in config.ResourceDeposits)
            {
                var depVec = LatLonToVector(deposit.LatitudeDeg, deposit.LongitudeDeg);
                var dot = VectorDot(hexVec, depVec);
                var angle = Math.Acos(Math.Clamp(dot, -1.0, 1.0));
                var distanceKm = angle * config.BodyRadiusKm;

                var score = deposit.Richness * Math.Exp(-distanceKm / 50.0);
                if (score > bestScore) bestScore = score;
            }
        }

        return Math.Clamp(bestScore, 0.0, 1.0);
    }

    /// <summary>
    /// Crater clearance: 1.0 = no craters in footprint, lower = rim/floor presence.
    /// Single-cell crater = 0.7, multi-cell = 0.5, mixed = average.
    /// </summary>
    private static double ComputeCraterClearance(RegionTile tile, IReadOnlyList<H3Index> footprint)
    {
        if (footprint.Count == 0) return 1.0;

        double total = 0.0;
        foreach (var hex in footprint)
        {
            var ordinal = FindOrdinal(tile, hex);
            if (ordinal < 0) { total += 1.0; continue; }

            total += tile.Crater(ordinal) switch
            {
                CraterKind.None => 1.0,
                CraterKind.SingleCell => 0.7,
                CraterKind.MultiCell => 0.5,
                _ => 1.0
            };
        }

        return total / footprint.Count;
    }

    private static (double X, double Y, double Z) LatLonToVector(double latDeg, double lonDeg)
    {
        var lat = latDeg * Math.PI / 180.0;
        var lon = lonDeg * Math.PI / 180.0;
        var cosLat = Math.Cos(lat);
        return (cosLat * Math.Cos(lon), Math.Sin(lat), cosLat * Math.Sin(lon));
    }

    private static double VectorDot((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}