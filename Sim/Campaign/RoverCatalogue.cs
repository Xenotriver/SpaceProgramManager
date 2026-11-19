namespace Sim.Campaign;

/// <summary>Static definition of a purchasable rover model.</summary>
public sealed record RoverDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyDictionary<string, double> Cost { get; init; }

    public double SpeedKmh { get; init; } = CampaignDefaults.DefaultRoverSpeedKmh;

    public double Health { get; init; } = CampaignDefaults.DefaultRoverHealth;
}

/// <summary>Shared outcome space for rover purchase and dispatch actions.</summary>
public enum RoverActionStatus
{
    Purchased,
    NoGarage,
    GarageNotReady,
    NoCapacity,
    CannotAfford,
    Dispatched,
    UnknownRover,
    NotAvailable,
    WrongLocation,
    Unreachable,
}

/// <summary>Result returned to the UI so it can show a precise message.</summary>
public sealed record RoverActionResult(RoverActionStatus Status, string Message)
{
    public bool Success => Status is RoverActionStatus.Purchased or RoverActionStatus.Dispatched;
}

/// <summary>
/// Rover models are static content. The v4 save stores only the chosen definition
/// id and the campaign-specific speed/health/cargo values.
/// </summary>
public static class RoverCatalogue
{
    public const string GarageDefId = "rover_garage";

    public const string Mk1Id = "rover_mk1";

    public static RoverDefinition Mk1 { get; } = new()
    {
        Id = Mk1Id,
        DisplayName = "Rover Mk I",
        Cost = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["credits"] = 500_000.0,
            ["metals"] = 25.0,
            ["electronics"] = 10.0,
        },
    };

    public static IReadOnlyList<RoverDefinition> All { get; } = new[] { Mk1 };

    public static bool TryGet(string? id, out RoverDefinition? definition)
    {
        definition = string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(rover => rover.Id == id);
        return definition is not null;
    }
}
