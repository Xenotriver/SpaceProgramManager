using H3;

namespace Sim.Site;

/// <summary>
/// Normalized score factors for a site (0..1 each, higher is better).
/// </summary>
public readonly record struct SiteFactors
{
    /// <summary>Fraction of the year with usable sunlight (0..1).</summary>
    public double Sunlight { get; init; }

    /// <summary>Line-of-sight quality to Earth/relay (0..1). 1 = always visible.</summary>
    public double Comms { get; init; }

    /// <summary>Terrain suitability: inverse of slope/roughness (0..1).</summary>
    public double Terrain { get; init; }

    /// <summary>Proximity to known resources (water ice, minerals) (0..1).</summary>
    public double Resources { get; init; }

    /// <summary>Absence of crater hazards (1 = no crater, lower = rim/floor).</summary>
    public double CraterClearance { get; init; }

    /// <summary>True if all hexes in the footprint are buildable biomes.</summary>
    public bool Buildable { get; init; }

    /// <summary>Weighted composite score (0..1). Only valid if <see cref="Buildable"/>.</summary>
    public double Composite(double wSun, double wComms, double wTerrain, double wRes, double wCrater)
    {
        if (!Buildable) return 0.0;

        var sum = wSun + wComms + wTerrain + wRes + wCrater;
        if (sum <= 0) return 0.0;

        return (Sunlight * wSun + Comms * wComms + Terrain * wTerrain + Resources * wRes + CraterClearance * wCrater) / sum;
    }

    /// <summary>Weighted composite score using the supplied <see cref="SiteWeights"/>.</summary>
    public double Composite(SiteWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        return Composite(
            weights.Sunlight,
            weights.Comms,
            weights.Terrain,
            weights.Resources,
            weights.CraterClearance);
    }
}

/// <summary>
/// Result of scoring a site footprint.
/// </summary>
public readonly record struct SiteScore
{
    /// <summary>Center hex of the footprint.</summary>
    public required H3Index Center { get; init; }

    /// <summary>All hexes in the footprint (center + neighbors within radius).</summary>
    public required IReadOnlyList<H3Index> Footprint { get; init; }

    /// <summary>Normalized factor breakdown.</summary>
    public required SiteFactors Factors { get; init; }

    /// <summary>Composite score using default weights.</summary>
    public double Composite => Factors.Composite(SiteWeights.Default);

    /// <summary>Composite score using the supplied <see cref="SiteWeights"/>.</summary>
    public double CompositeWith(SiteWeights weights) => Factors.Composite(weights);

    /// <summary>True if the site is viable under the default threshold and weights.</summary>
    public bool Viable => Factors.Buildable && Composite > 0.25;

    /// <summary>True if the site is viable under the supplied scoring configuration.</summary>
    public bool IsViable(SiteScorerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Factors.Buildable && CompositeWith(config.Weights) > config.ViabilityThreshold;
    }
}

/// <summary>
/// Weights for the composite score. Tune per mission profile.
/// </summary>
public sealed record SiteWeights
{
    public double Sunlight { get; init; } = 1.0;
    public double Comms { get; init; } = 1.0;
    public double Terrain { get; init; } = 1.0;
    public double Resources { get; init; } = 0.5;
    public double CraterClearance { get; init; } = 0.5;

    public static SiteWeights Default => new();
    public static SiteWeights PowerHeavy => new() { Sunlight = 2.0, Resources = 1.0 };
    public static SiteWeights CommsHeavy => new() { Comms = 2.0 };
    public static SiteWeights SafetyHeavy => new() { Terrain = 1.5, CraterClearance = 1.5 };
}

/// <summary>
/// Configuration for site scoring.
/// </summary>
public sealed record SiteScorerConfig
{
    /// <summary>Footprint radius in hex rings (0 = single hex, 1 = 7 hex, 2 = 19 hex).</summary>
    public int FootprintRadius { get; init; } = 1;

    /// <summary>Body radius in km (for horizon calculations).</summary>
    public required double BodyRadiusKm { get; init; }

    /// <summary>Axial tilt in degrees (Moon ~1.54°, Earth ~23.44°).</summary>
    public double AxialTiltDeg { get; init; } = 1.54;

    /// <summary>Minimum sun elevation angle for "usable" power (degrees).</summary>
    public double MinSunElevationDeg { get; init; } = 5.0;

    /// <summary>Resource deposit locations (lat, lon, type). Empty = no resource data.</summary>
    public IReadOnlyList<ResourceDeposit> ResourceDeposits { get; init; } = Array.Empty<ResourceDeposit>();

    /// <summary>Weights for composite score.</summary>
    public SiteWeights Weights { get; init; } = SiteWeights.Default;

    /// <summary>Minimum composite score to consider viable.</summary>
    public double ViabilityThreshold { get; init; } = 0.25;
}

/// <summary>
/// A known resource deposit location.
/// </summary>
public readonly record struct ResourceDeposit
{
    public double LatitudeDeg { get; init; }
    public double LongitudeDeg { get; init; }
    public string Type { get; init; } // e.g. "water_ice", "helium3", "rare_earth"
    public double Richness { get; init; } // 0..1
}
