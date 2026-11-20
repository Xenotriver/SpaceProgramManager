using Sim.Time;

namespace Sim.Campaign;

/// <summary>
/// Starting conditions for a new campaign. Kept in one place so a fresh game
/// and a migrated v1 save begin on equal footing; unit tests rely on the credit
/// balance as well.
/// </summary>
public static class CampaignDefaults
{
    /// <summary>Enough for a small habitat, a solar array and a little margin.</summary>
    public const double StartingCredits = 10_000_000;

    /// <summary>Construction materials that make the first buildings placeable.</summary>
    public static IReadOnlyDictionary<string, double> StartingStockpile { get; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["metals"] = 250,
            ["electronics"] = 50,
            ["composites"] = 50,
            ["chemicals"] = 20,
            ["oxygen"] = 500,
            ["water"] = 200,
            ["food"] = 100,
        };

    /// <summary>Techs available from the first hour. The tree itself is M5.</summary>
    public static IReadOnlyCollection<string> StartingTechs { get; } = Array.Empty<string>();

    /// <summary>Default rover speed; resolves the 8 vs 10 km/h plan drift in M5 C0.</summary>
    public const double DefaultRoverSpeedKmh = 8.0;

    /// <summary>Full-health default for a new rover.</summary>
    public const double DefaultRoverHealth = 100.0;

    /// <summary>Keeps the persisted event history bounded.</summary>
    public const int MaxEventLogEntries = 50;

    /// <summary>No random events before the first full day of a campaign.</summary>
    public const double EventEarliestGameHour = 24.0;

    /// <summary>Minimum spacing between random events after one is raised.</summary>
    public const double EventCooldownHours = 72.0;

    /// <summary>Severity at or above which a failure destroys rather than damages.</summary>
    public const double FailureDestroyedSeverity = 0.85;

    /// <summary>Per-building, per-game-hour hazard for operational installations.</summary>
    public const double HazardPerBuildingHour = 0.001;

    /// <summary>
    /// Fills credits, stockpile and RNG state for a campaign that has no
    /// authoritative values yet. Existing values win, so this is safe to call on
    /// a migrated save.
    /// </summary>
    public static void Apply(CampaignMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (meta.Credits <= 0.0)
            meta.Credits = StartingCredits;

        foreach (var (resource, amount) in StartingStockpile)
        {
            if (!meta.Stockpile.ContainsKey(resource))
                meta.Stockpile[resource] = amount;
        }

        foreach (var tech in StartingTechs)
            meta.UnlockedTechs.Add(tech);

        if (meta.RngSeed == 0)
            meta.RngSeed = DeterministicSeed(meta.CreatedUtc);
        if (meta.RngState == 0)
            meta.RngState = meta.RngSeed;
    }

    /// <summary>Stable seed for a save that predates stored RNG state.</summary>
    public static ulong DeterministicSeed(DateTimeOffset createdUtc)
        => DeterministicRandom.SeedFrom((ulong)createdUtc.UtcTicks);
}
