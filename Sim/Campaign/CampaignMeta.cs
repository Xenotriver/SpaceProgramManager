using Sim.World;

namespace Sim.Campaign;

/// <summary>
/// Campaign-wide state that is not tied to a single region: clock, treasury,
/// science, unlocked techs, RNG state, visited regions and the last region the
/// player was looking at.
/// </summary>
public sealed class CampaignMeta
{
    public CampaignMeta()
    {
        foreach (var (resource, amount) in CampaignDefaults.StartingStockpile)
            Stockpile[resource] = amount;

        foreach (var tech in CampaignDefaults.StartingTechs)
            UnlockedTechs.Add(tech);

        RngSeed = CampaignDefaults.DeterministicSeed(CreatedUtc);
        RngState = RngSeed;
    }

    /// <summary>Elapsed in-game time in hours.</summary>
    public double GameTimeHours { get; set; }

    /// <summary>Player treasury in credits.</summary>
    public double Credits { get; set; } = CampaignDefaults.StartingCredits;

    /// <summary>Global stockpile (resource id → amount).</summary>
    public Dictionary<string, double> Stockpile { get; } = new(StringComparer.Ordinal);

    /// <summary>Accumulated science points from operational laboratories.</summary>
    public double SciencePoints { get; set; }

    /// <summary>Technologies unlocked for this campaign. The tree UI is M5.</summary>
    public HashSet<string> UnlockedTechs { get; } = new(StringComparer.Ordinal);

    /// <summary>Seed the campaign RNG was created with.</summary>
    public ulong RngSeed { get; set; }

    /// <summary>Opaque RNG position; persisted so reloads continue the sequence.</summary>
    public ulong RngState { get; set; }

    /// <summary>Regions the player has opened at least once.</summary>
    public HashSet<ulong> VisitedRegions { get; } = new();

    /// <summary>Region that was open when the campaign was last saved, if any.</summary>
    public ulong? ActiveRegionIndex { get; set; }

    /// <summary>Campaign creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time anything in the campaign changed (UTC).</summary>
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Identifies the baked body a campaign was created against. A save whose
/// fingerprint no longer matches the loaded bake is refused rather than applied
/// to shifted hexes.
/// </summary>
public readonly record struct BakeFingerprint(
    string BodyKey,
    int FineResolution,
    int RegionResolution,
    int RegionCount,
    string GeneratedUtc)
{
    /// <summary>Captures the fingerprint of a loaded body.</summary>
    public static BakeFingerprint From(BodyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new BakeFingerprint(
            manifest.Body.Key,
            manifest.FineResolution,
            manifest.RegionResolution,
            manifest.Regions.Count,
            manifest.GeneratedUtc);
    }

    public override string ToString()
        => $"{BodyKey} · fine {FineResolution} · region {RegionResolution} · {RegionCount} regions · {GeneratedUtc}";
}
