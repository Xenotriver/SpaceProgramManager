using Sim.Buildings;

namespace Sim.Campaign;

/// <summary>
/// Static definition of an installation failure. Causes, severity ranges and
/// repair costs live here; the v4 shard stores only the resulting typed record.
/// </summary>
public sealed record FailureDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public double MinSeverity { get; init; } = 0.2;

    public double MaxSeverity { get; init; } = 0.8;

    /// <summary>Base repair hours at severity 1.0, before the damaged multiplier.</summary>
    public double RepairHours { get; init; } = 24.0;

    /// <summary>Repair bill at severity 1.0; "credits" is a pseudo-resource.</summary>
    public required IReadOnlyDictionary<string, double> RepairCost { get; init; }
}

/// <summary>Outcome of a damage or repair-start action.</summary>
public enum MaintenanceActionStatus
{
    Damaged,
    RepairStarted,
    UnknownCause,
    UnknownRegion,
    UnknownBuilding,
    Healthy,
    AlreadyDamaged,
    NotOperational,
    AlreadyRepairing,
    CannotAfford,
}

/// <summary>Result returned to the UI with a precise reason and any charge.</summary>
public sealed record MaintenanceActionResult(
    MaintenanceActionStatus Status,
    string Message,
    double Credits = 0.0)
{
    public bool Success => Status is MaintenanceActionStatus.Damaged
        or MaintenanceActionStatus.RepairStarted;
}

/// <summary>Static failure causes. No save-schema or fingerprint change is needed.</summary>
public static class FailureCatalogue
{
    public const string DustIntrusionId = "dust_intrusion";
    public const string PowerSurgeId = "power_surge";
    public const string MicrometeoriteId = "micrometeorite";
    public const string LifeSupportLeakId = "life_support_leak";

    public static FailureDefinition DustIntrusion { get; } = new()
    {
        Id = DustIntrusionId,
        DisplayName = "Dust intrusion",
        Description = "Regolith has fouled seals and exposed electronics.",
        MinSeverity = 0.15,
        MaxSeverity = 0.55,
        RepairHours = 12.0,
        RepairCost = Cost(("credits", 18_000.0), ("electronics", 2.0)),
    };

    public static FailureDefinition PowerSurge { get; } = new()
    {
        Id = PowerSurgeId,
        DisplayName = "Power surge",
        Description = "A distribution fault damaged the installation's power bus.",
        MinSeverity = 0.25,
        MaxSeverity = 0.75,
        RepairHours = 18.0,
        RepairCost = Cost(("credits", 30_000.0), ("electronics", 3.0)),
    };

    public static FailureDefinition Micrometeorite { get; } = new()
    {
        Id = MicrometeoriteId,
        DisplayName = "Micrometeorite impact",
        Description = "A high-velocity strike breached the installation's shell.",
        MinSeverity = 0.4,
        MaxSeverity = 0.95,
        RepairHours = 30.0,
        RepairCost = Cost(("credits", 55_000.0), ("metals", 6.0)),
    };

    public static FailureDefinition LifeSupportLeak { get; } = new()
    {
        Id = LifeSupportLeakId,
        DisplayName = "Life-support leak",
        Description = "A pressure loop is losing consumables into the regolith.",
        MinSeverity = 0.2,
        MaxSeverity = 0.7,
        RepairHours = 20.0,
        RepairCost = Cost(("credits", 26_000.0), ("chemicals", 2.0)),
    };

    public static IReadOnlyList<FailureDefinition> All { get; } = new[]
    {
        DustIntrusion,
        PowerSurge,
        Micrometeorite,
        LifeSupportLeak,
    };

    public static bool TryGet(string? id, out FailureDefinition? definition)
    {
        definition = string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(item => item.Id == id);
        return definition is not null;
    }

    /// <summary>Weighted-free uniform pick; deterministic for the same roll.</summary>
    public static FailureDefinition Pick(double roll)
        => All[Math.Clamp((int)Math.Floor(Math.Clamp(roll, 0.0, 0.999999999) * All.Count), 0, All.Count - 1)];

    /// <summary>Severity in the cause's own range, driven by the campaign RNG.</summary>
    public static double RollSeverity(FailureDefinition definition, double roll)
        => Math.Clamp(
            definition.MinSeverity + Math.Clamp(roll, 0.0, 1.0) * (definition.MaxSeverity - definition.MinSeverity),
            0.0,
            1.0);

    /// <summary>Repair duration in game hours; destroyed installations take longer.</summary>
    public static double RepairHours(FailureDefinition definition, BuildingFailure failure)
    {
        var severity = Math.Clamp(failure.Severity, 0.0, 1.0);
        var multiplier = severity >= CampaignDefaults.FailureDestroyedSeverity ? 1.5 : 1.0;
        return Math.Max(1.0, definition.RepairHours * (0.5 + severity) * multiplier);
    }

    /// <summary>Severity-scaled bill, including the "credits" pseudo-resource.</summary>
    public static IReadOnlyDictionary<string, double> RepairCost(FailureDefinition definition, BuildingFailure failure)
    {
        var scale = 0.5 + Math.Clamp(failure.Severity, 0.0, 1.0);
        return definition.RepairCost.ToDictionary(
            item => item.Key,
            item => item.Value * scale,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, double> Cost(params (string Id, double Amount)[] items)
        => items.ToDictionary(item => item.Id, item => item.Amount, StringComparer.Ordinal);
}
