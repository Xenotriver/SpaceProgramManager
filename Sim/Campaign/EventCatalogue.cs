namespace Sim.Campaign;

/// <summary>What choosing one event option does to campaign-wide state.</summary>
public sealed record EventEffect
{
    public double Credits { get; init; }

    public double Science { get; init; }

    public IReadOnlyDictionary<string, double> Stockpile { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Optional static failure cause applied to an installation in the region.</summary>
    public string? FailureCause { get; init; }

    public required string Summary { get; init; }
}

/// <summary>One player-facing option on a random event.</summary>
public sealed record EventChoiceDefinition
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public string Detail { get; init; } = string.Empty;

    public required EventEffect Effect { get; init; }
}

/// <summary>
/// Static definition of a random event. Text, choices, weights and effects live
/// here; the save stores only the pending instance, cooldown and bounded log.
/// </summary>
public sealed record EventDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public double Weight { get; init; } = 1.0;

    public double CooldownHours { get; init; } = CampaignDefaults.EventCooldownHours;

    /// <summary>Whether the event should pin a visited region in the save record.</summary>
    public bool TargetsRegion { get; init; }

    public required IReadOnlyList<EventChoiceDefinition> Choices { get; init; }
}

/// <summary>Outcome of an event roll or choice resolution.</summary>
public enum EventActionStatus
{
    Raised,
    Resolved,
    NothingRaised,
    UnknownEvent,
    UnknownChoice,
}

public sealed record EventActionResult(EventActionStatus Status, string Message)
{
    public bool Success => Status is EventActionStatus.Raised or EventActionStatus.Resolved;
}

/// <summary>Static random events. No save-schema or fingerprint change is needed.</summary>
public static class EventCatalogue
{
    public const string DustStormId = "dust_storm";
    public const string SupplyDropId = "supply_drop";
    public const string SurveyOpportunityId = "survey_opportunity";

    public static EventDefinition DustStorm { get; } = new()
    {
        Id = DustStormId,
        DisplayName = "Dust Storm",
        Description = "A regional front is rolling across the outpost. Power output will suffer until it passes.",
        Weight = 3.0,
        TargetsRegion = true,
        Choices = new[]
        {
            new EventChoiceDefinition
            {
                Id = "shelter",
                Label = "Shelter the crew",
                Detail = "Lose a little income while operations pause; the science team uses the downtime.",
                Effect = new EventEffect
                {
                    Credits = -50_000.0,
                    Science = 4.0,
                    Summary = "Crew sheltered; instruments idle",
                },
            },
            new EventChoiceDefinition
            {
                Id = "continue",
                Label = "Keep operating",
                Detail = "Dust fouls exposed electronics, but the survey continues.",
                Effect = new EventEffect
                {
                    Stockpile = Stockpile(("electronics", -5.0)),
                    Science = 2.0,
                    FailureCause = FailureCatalogue.DustIntrusionId,
                    Summary = "Dust fouled the arrays",
                },
            },
        },
    };

    public static EventDefinition SupplyDrop { get; } = new()
    {
        Id = SupplyDropId,
        DisplayName = "Unscheduled Supply Drop",
        Description = "A friendly depot has a surplus of consumables and asks only for handling costs.",
        Weight = 2.0,
        Choices = new[]
        {
            new EventChoiceDefinition
            {
                Id = "accept",
                Label = "Buy the surplus",
                Detail = "Food and water arrive in exchange for a modest transfer fee.",
                Effect = new EventEffect
                {
                    Credits = -25_000.0,
                    Stockpile = Stockpile(("food", 50.0), ("water", 50.0)),
                    Summary = "Bought surplus food and water",
                },
            },
            new EventChoiceDefinition
            {
                Id = "decline",
                Label = "Decline politely",
                Detail = "Keep the credits and note the depot's operating pattern.",
                Effect = new EventEffect
                {
                    Science = 1.0,
                    Summary = "Declined the surplus drop",
                },
            },
        },
    };

    public static EventDefinition SurveyOpportunity { get; } = new()
    {
        Id = SurveyOpportunityId,
        DisplayName = "Survey Window",
        Description = "A clear lighting window lets the survey team map a fresh swath of terrain.",
        Weight = 2.0,
        TargetsRegion = true,
        Choices = new[]
        {
            new EventChoiceDefinition
            {
                Id = "survey",
                Label = "Fund the survey",
                Detail = "Spend field costs for a useful science return.",
                Effect = new EventEffect
                {
                    Credits = -25_000.0,
                    Science = 8.0,
                    Summary = "Funded an extra survey pass",
                },
            },
            new EventChoiceDefinition
            {
                Id = "skip",
                Label = "Skip this window",
                Detail = "Save the credits for construction.",
                Effect = new EventEffect
                {
                    Summary = "Skipped the survey window",
                },
            },
        },
    };

    public const string EquipmentFaultId = "equipment_fault";

    public static EventDefinition EquipmentFault { get; } = new()
    {
        Id = EquipmentFaultId,
        DisplayName = "Equipment Fault",
        Description = "Telemetry shows a distribution fault in one of the region's installations.",
        Weight = 1.0,
        TargetsRegion = true,
        Choices = new[]
        {
            new EventChoiceDefinition
            {
                Id = "inspect",
                Label = "Stop and inspect",
                Detail = "Pay field costs for a clean diagnosis; no damage is done.",
                Effect = new EventEffect
                {
                    Credits = -40_000.0,
                    Science = 3.0,
                    Summary = "Inspection caught the fault early",
                },
            },
            new EventChoiceDefinition
            {
                Id = "push_on",
                Label = "Keep operating",
                Detail = "Save the credits and risk taking an installation offline.",
                Effect = new EventEffect
                {
                    FailureCause = FailureCatalogue.PowerSurgeId,
                    Summary = "A power surge took an installation offline",
                },
            },
        },
    };

    public static IReadOnlyList<EventDefinition> All { get; } = new[]
    {
        DustStorm,
        SupplyDrop,
        SurveyOpportunity,
        EquipmentFault,
    };

    public static bool TryGet(string? id, out EventDefinition? definition)
    {
        definition = string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(item => item.Id == id);
        return definition is not null;
    }

    /// <summary>Weighted pick for a roll in [0, 1). Deterministic for equal rolls.</summary>
    public static EventDefinition Pick(double roll)
    {
        var total = All.Sum(item => Math.Max(0.0, item.Weight));
        if (total <= 0.0)
            return DustStorm;

        var cursor = Math.Clamp(roll, 0.0, 0.999999999) * total;
        foreach (var item in All)
        {
            cursor -= Math.Max(0.0, item.Weight);
            if (cursor < 0.0)
                return item;
        }

        return All[^1];
    }

    private static IReadOnlyDictionary<string, double> Stockpile(params (string Id, double Amount)[] items)
        => items.ToDictionary(item => item.Id, item => item.Amount, StringComparer.Ordinal);
}
