namespace Sim.Campaign;

/// <summary>
/// Static definition of one contract. Rewards, goals and text live here; the v4
/// save stores only the accepted instance's progress and reward flag.
/// </summary>
public sealed record ContractDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Client { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Stockpile resource id → total amount the agency must deliver.</summary>
    public required IReadOnlyDictionary<string, double> Targets { get; init; }

    public double DurationHours { get; init; } = 168.0;

    public double CreditsReward { get; init; }

    public double ScienceReward { get; init; }

    /// <summary>Fraction of the credit reward added when every target is met.</summary>
    public double CompletionBonus { get; init; }

    public string? RequiredTech { get; init; }
}

/// <summary>Outcome of a contract accept/deliver/claim action.</summary>
public enum ContractActionStatus
{
    Accepted,
    Delivered,
    Claimed,
    Settled,
    UnknownContract,
    UnknownCounter,
    NothingToDeliver,
    InsufficientStock,
    AlreadyAccepted,
    AlreadyClaimed,
    TechLocked,
    NotActive,
}

/// <summary>Result returned to the UI with a precise reason and any payout.</summary>
public sealed record ContractActionResult(
    ContractActionStatus Status,
    string Message,
    double Credits = 0.0,
    double Science = 0.0)
{
    public bool Success => Status is ContractActionStatus.Accepted
        or ContractActionStatus.Delivered
        or ContractActionStatus.Claimed
        or ContractActionStatus.Settled;
}

/// <summary>Static contract offers. No save-schema or fingerprint change is needed.</summary>
public static class ContractCatalogue
{
    public const string OxygenResupplyId = "oxygen_resupply";
    public const string RefinedMetalsOrderId = "refined_metals_order";
    public const string IceDeliveryId = "ice_delivery";

    public static ContractDefinition OxygenResupply { get; } = new()
    {
        Id = OxygenResupplyId,
        DisplayName = "Lunar Outpost Resupply",
        Client = "Lunar Logistics Office",
        Description = "Keep the crewed outpost supplied through one rotation.",
        Targets = Targets(("oxygen", 120.0), ("water", 60.0)),
        DurationHours = 168.0,
        CreditsReward = 1_200_000.0,
        ScienceReward = 12.0,
        CompletionBonus = 0.10,
    };

    public static ContractDefinition RefinedMetalsOrder { get; } = new()
    {
        Id = RefinedMetalsOrderId,
        DisplayName = "Orbital Frame Stock",
        Client = "Orbital Assembly Consortium",
        Description = "Smelt and wiring for a first orbital truss order.",
        Targets = Targets(("refined_metals", 80.0), ("electronics", 20.0)),
        DurationHours = 240.0,
        CreditsReward = 3_000_000.0,
        ScienceReward = 20.0,
        CompletionBonus = 0.10,
        RequiredTech = "smelting",
    };

    public static ContractDefinition IceDelivery { get; } = new()
    {
        Id = IceDeliveryId,
        DisplayName = "Propellant Feedstock",
        Client = "Deep Space Depot",
        Description = "Water and hydrogen for a depot shakedown campaign.",
        Targets = Targets(("water", 200.0), ("hydrogen", 40.0)),
        DurationHours = 240.0,
        CreditsReward = 2_000_000.0,
        ScienceReward = 16.0,
        CompletionBonus = 0.08,
        RequiredTech = "ice_extraction",
    };

    public static IReadOnlyList<ContractDefinition> All { get; } = new[]
    {
        OxygenResupply,
        RefinedMetalsOrder,
        IceDelivery,
    };

    public static bool TryGet(string? id, out ContractDefinition? definition)
    {
        definition = string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(contract => contract.Id == id);
        return definition is not null;
    }

    /// <summary>
    /// Weighted delivery ratio across all targets, clamped to 0..1. A partially
    /// delivered contract still earns a partial payout when it is settled.
    /// </summary>
    public static double CompletionFraction(CampaignContract contract, ContractDefinition definition)
    {
        double target = 0.0;
        double delivered = 0.0;
        foreach (var (counter, amount) in definition.Targets)
        {
            if (amount <= 0.0)
                continue;

            target += amount;
            delivered += Math.Min(amount, Math.Max(0.0, contract.Progress.GetValueOrDefault(counter)));
        }

        return target <= 0.0 ? 0.0 : Math.Clamp(delivered / target, 0.0, 1.0);
    }

    /// <summary>First counter still short of its target, ordered by id for stability.</summary>
    public static string? NextCounter(CampaignContract contract, ContractDefinition definition)
        => definition.Targets
            .Where(item => item.Value > 0.0
                && contract.Progress.GetValueOrDefault(item.Key) + 1e-9 < item.Value)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key)
            .FirstOrDefault();

    private static IReadOnlyDictionary<string, double> Targets(params (string Id, double Amount)[] items)
        => items.ToDictionary(item => item.Id, item => item.Amount, StringComparer.Ordinal);
}
