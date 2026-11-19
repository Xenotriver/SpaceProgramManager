namespace Sim.Campaign;

/// <summary>
/// Static technology definition. The save stores only unlocked ids and science
/// points; definitions, costs, prerequisites and unlock descriptions live here.
/// </summary>
public sealed record TechDefinition
{
    /// <summary>Stable id used in <see cref="CampaignMeta.UnlockedTechs"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name shown in the tech screen.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Short explanation shown in the tech screen.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Display tier; also the order in which the tree is presented.</summary>
    public int Tier { get; init; } = 1;

    /// <summary>Science points spent immediately when the tech is researched.</summary>
    public double ScienceCost { get; init; }

    /// <summary>Tech ids that must already be unlocked.</summary>
    public IReadOnlyList<string> Prerequisites { get; init; } = Array.Empty<string>();

    /// <summary>Building definition ids enabled by this tech.</summary>
    public IReadOnlyList<string> UnlockedBuildingIds { get; init; } = Array.Empty<string>();
}

/// <summary>Outcome of a science-point tech purchase.</summary>
public enum TechPurchaseStatus
{
    Purchased,
    UnknownTech,
    AlreadyUnlocked,
    PrerequisitesMissing,
    InsufficientScience,
}

/// <summary>Result returned to the UI so it can show a precise message.</summary>
public sealed record TechPurchaseResult(TechPurchaseStatus Status, string Message)
{
    public bool Success => Status == TechPurchaseStatus.Purchased;
}

/// <summary>
/// The M5 tech tree. Covers every <c>BuildingDef.RequiredTech</c> id so placement
/// never refers to a tech the player cannot research.
/// </summary>
public static class TechCatalogue
{
    public static IReadOnlyList<TechDefinition> All { get; } = new[]
    {
        new TechDefinition
        {
            Id = "ice_extraction",
            DisplayName = "Ice Extraction",
            Description = "Mine buried ice for life support and fuel.",
            Tier = 1,
            ScienceCost = 50.0,
            UnlockedBuildingIds = new[] { "ice_extractor" },
        },
        new TechDefinition
        {
            Id = "nuclear_power",
            DisplayName = "Nuclear Power",
            Description = "Compact radioisotope generators for long nights.",
            Tier = 1,
            ScienceCost = 40.0,
            UnlockedBuildingIds = new[] { "rtg_generator" },
        },
        new TechDefinition
        {
            Id = "advanced_habitats",
            DisplayName = "Advanced Habitats",
            Description = "Pressurised modules for larger crews.",
            Tier = 1,
            ScienceCost = 60.0,
            UnlockedBuildingIds = new[] { "habitat_module_large" },
        },
        new TechDefinition
        {
            Id = "cryogenic_storage",
            DisplayName = "Cryogenic Storage",
            Description = "Long-term tanks for volatile propellants.",
            Tier = 1,
            ScienceCost = 60.0,
            UnlockedBuildingIds = new[] { "cryogenic_tank_farm" },
        },
        new TechDefinition
        {
            Id = "smelting",
            DisplayName = "Smelting",
            Description = "Process regolith into construction metals.",
            Tier = 2,
            ScienceCost = 90.0,
            Prerequisites = new[] { "nuclear_power" },
            UnlockedBuildingIds = new[] { "smelter" },
        },
        new TechDefinition
        {
            Id = "radio_astronomy",
            DisplayName = "Radio Astronomy",
            Description = "Deep-space listening and high-rate science downlink.",
            Tier = 2,
            ScienceCost = 120.0,
            Prerequisites = new[] { "nuclear_power" },
            UnlockedBuildingIds = new[] { "telescope_array" },
        },
        new TechDefinition
        {
            Id = "deep_space_comms",
            DisplayName = "Deep Space Comms",
            Description = "Relay network for interplanetary operations.",
            Tier = 3,
            ScienceCost = 150.0,
            Prerequisites = new[] { "radio_astronomy" },
            UnlockedBuildingIds = new[] { "comms_relay" },
        },
    }.OrderBy(tech => tech.Tier).ThenBy(tech => tech.Id, StringComparer.Ordinal).ToArray();

    private static readonly Dictionary<string, TechDefinition> ById =
        All.ToDictionary(tech => tech.Id, StringComparer.Ordinal);

    /// <summary>Looks up a definition by its stable id.</summary>
    public static bool TryGet(string? id, out TechDefinition? definition)
    {
        definition = null;
        return !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id, out definition);
    }

    /// <summary>True when the id belongs to the built-in tree.</summary>
    public static bool IsKnown(string? id) => TryGet(id, out _);
}
